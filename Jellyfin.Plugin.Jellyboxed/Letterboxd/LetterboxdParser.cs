using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.Jellyboxed.Data;

namespace Jellyfin.Plugin.Jellyboxed.Letterboxd;

/// <summary>A friend row from the "friends who watched this" page plus the link to their full review (if any).</summary>
public sealed record FriendRow(FriendActivity Friend, string? ReviewHref);

/// <summary>An entry from a member's public RSS diary feed.</summary>
public sealed record RssEntry(string Slug, string? TmdbId, double? Rating, bool Liked, string Title);

/// <summary>
/// All knowledge of Letterboxd's HTML lives here, in pure functions, so that when Letterboxd changes
/// its markup only this file needs touching (and it can be unit tested against saved pages).
/// </summary>
public static class LetterboxdParser
{
    private static readonly Regex RatedClass = new(@"\brated-(\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex CheckedRating = new(
        "value=\"(\\d{1,2})\"[^>]{0,80}checked|checked[^>]{0,80}value=\"(\\d{1,2})\"", RegexOptions.Compiled);
    private static readonly Regex TmdbLink = new(@"themoviedb\.org/movie/(\d+)", RegexOptions.Compiled);
    private static readonly string[] LovedMarkers = { "icon-liked", "like-link-target liked", "\"liked\":true", "liked-large-icon" };

    /// <summary>Converts Letterboxd's half-star index (1..10) to stars (0.5..5).</summary>
    public static double? StarsFromIndex(int index) => index is >= 1 and <= 10 ? index / 2.0 : null;

    /// <summary>Renders a rating as ★★★½.</summary>
    public static string StarText(double? rating)
    {
        if (rating is null or <= 0)
        {
            return string.Empty;
        }

        var whole = (int)Math.Floor(rating.Value);
        return new string('★', whole) + (rating.Value - whole >= 0.5 ? "½" : string.Empty);
    }

    public static double? RatingFromClasses(HtmlNode? node)
    {
        if (node is null)
        {
            return null;
        }

        foreach (var cls in node.Classes)
        {
            var m = RatedClass.Match(cls);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
            {
                return StarsFromIndex(idx);
            }
        }

        return null;
    }

    public static List<FriendRow> ParseFriends(string html)
    {
        var doc = HtmlParser.Parse(html);
        var result = new List<FriendRow>();

        foreach (var row in doc.Select("table.member-table tbody tr"))
        {
            var nameNode = row.SelectOne(".name");
            var name = nameNode?.InnerText.Trim();
            if (string.IsNullOrEmpty(name))
            {
                name = "Unknown";
            }

            var avatar = row.SelectOne(".avatar img")?.Attr("src") ?? string.Empty;
            var profileHref = row.SelectOne(".name")?.Attr("href")
                ?? row.SelectOne("a.name")?.Attr("href")
                ?? row.SelectOne(".avatar")?.Attr("href")
                ?? string.Empty;

            var ratingSpan = row.SelectOne(".col-rating span.rating") ?? row.SelectOne("span.rating");
            var rating = RatingFromClasses(ratingSpan);

            var friend = new FriendActivity
            {
                Name = name,
                Avatar = avatar,
                Rating = StarText(rating),
                RatingValue = rating,
                ProfileUrl = profileHref,
            };

            var href = row.SelectOne(".col-review a")?.Attr("href");
            result.Add(new FriendRow(friend, string.IsNullOrWhiteSpace(href) ? null : href));
        }

        return result;
    }

    /// <summary>Extracts the visible text of a review page, without the collapsed "hidden" duplicate.</summary>
    public static string ParseReviewBody(string html)
    {
        var doc = HtmlParser.Parse(html);
        var body = doc.SelectOne(".js-review-body");
        if (body is null)
        {
            return string.Empty;
        }

        foreach (var hidden in body.Select(".hidden").ToList())
        {
            hidden.Remove();
        }

        return body.InnerText.Trim();
    }

    /// <summary>Slugs of all films in a poster grid (rated list, likes, watchlist...).</summary>
    public static HashSet<string> ParseGridSlugs(string html)
    {
        var doc = HtmlParser.Parse(html);
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in doc.Select("[data-item-slug]"))
        {
            var slug = node.Attr("data-item-slug");
            if (!string.IsNullOrWhiteSpace(slug))
            {
                slugs.Add(slug);
            }
        }

        if (slugs.Count == 0)
        {
            foreach (var node in doc.Select("[data-film-slug]"))
            {
                var slug = node.Attr("data-film-slug");
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    slugs.Add(slug);
                }
            }
        }

        return slugs;
    }

    public static string? ParseTmdbId(string html)
    {
        var m = TmdbLink.Match(html);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        var doc = HtmlParser.Parse(html);
        var body = doc.SelectOne("body");
        var id = body?.Attr("data-tmdb-id");
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>Finds Letterboxd's internal production id / film id used by the logging and watchlist actions.</summary>
    public static (string? ProductionId, string? FilmId) ParseFilmIds(string html, string? identifierHeader, string slug)
    {
        var doc = HtmlParser.Parse(html);
        var productionId = identifierHeader;

        if (string.IsNullOrWhiteSpace(productionId))
        {
            var poster = doc.SelectOne("[data-postered-identifier]")?.Attr("data-postered-identifier");
            if (!string.IsNullOrWhiteSpace(poster))
            {
                try
                {
                    using var json = JsonDocument.Parse(poster);
                    if (json.RootElement.TryGetProperty("lid", out var lid) && lid.ValueKind == JsonValueKind.String)
                    {
                        productionId = lid.GetString();
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed attribute.
                }
            }
        }

        // The watchlist action needs the numeric film id, so always try to find it as well.
        var filmEl = doc.Select("[data-film-slug]").FirstOrDefault(n => n.Attr("data-film-slug") == slug && n.Attr("data-film-id") is not null)
            ?? doc.Select("[data-item-link]").FirstOrDefault(n => n.Attr("data-item-link") == $"/film/{slug}/" && n.Attr("data-film-id") is not null)
            ?? doc.SelectOne("[data-film-id]");
        var filmId = filmEl?.Attr("data-film-id");

        // Letterboxd keeps moving this around (and lazy-loads posters), so fall back to the other places it appears.
        if (string.IsNullOrWhiteSpace(filmId))
        {
            var posterJson = doc.SelectOne("[data-postered-identifier]")?.Attr("data-postered-identifier");
            filmId = FirstGroup(posterJson, "\"uid\"\\s*:\\s*\"film:(\\d+)\"");
        }

        filmId ??= FirstGroup(html, "data-film-id=[\"'](\\d+)[\"']")
            ?? FirstGroup(html, "\"filmId\"\\s*:\\s*\"?(\\d+)")
            ?? FirstGroup(html, "[\"'&;]film:(\\d+)[\"'&;]")
            ?? FirstGroup(html, "/film/(\\d+)/(?:add-to-watchlist|remove-from-watchlist|rating|like)");

        return (string.IsNullOrWhiteSpace(productionId) ? null : productionId, string.IsNullOrWhiteSpace(filmId) ? null : filmId);
    }

    private static string? FirstGroup(string? input, string pattern)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        var m = System.Text.RegularExpressions.Regex.Match(input, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool ParseLoved(string html)
    {
        if (html.Contains("data-is-liked=\"true\"", StringComparison.Ordinal))
        {
            return true;
        }

        return LovedMarkers.Any(m => html.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>Reads the user's own rating from a film page of their profile (/user/film/slug/).</summary>
    public static double? ParseOwnRating(string html)
    {
        var m = RatedClass.Match(html);
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
        {
            return StarsFromIndex(idx);
        }

        var c = CheckedRating.Match(html);
        if (c.Success)
        {
            var value = c.Groups[1].Success ? c.Groups[1].Value : c.Groups[2].Value;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var idx2))
            {
                return StarsFromIndex(idx2);
            }
        }

        return null;
    }

    /// <summary>Parses a member's public RSS feed (their most recent diary entries, with TMDB ids included).</summary>
    public static List<RssEntry> ParseRss(string xml)
    {
        var result = new List<RssEntry>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return result;
        }

        XNamespace lb = "https://letterboxd.com";
        XNamespace tmdb = "https://themoviedb.org";

        foreach (var item in doc.Descendants("item"))
        {
            var link = (string?)item.Element("link");
            var slug = SlugFromFilmUrl(link);
            if (slug is null)
            {
                continue;
            }

            var title = (string?)item.Element(lb + "filmTitle") ?? slug;
            var tmdbId = (string?)item.Element(tmdb + "movieId");
            double? rating = null;
            var ratingText = (string?)item.Element(lb + "memberRating");
            if (ratingText is not null && double.TryParse(ratingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
            {
                rating = r;
            }

            var liked = string.Equals((string?)item.Element(lb + "memberLike"), "Yes", StringComparison.OrdinalIgnoreCase);
            result.Add(new RssEntry(slug, string.IsNullOrWhiteSpace(tmdbId) ? null : tmdbId, rating, liked, title));
        }

        return result;
    }

    /// <summary>Gets the slug from ".../film/{slug}/..." (also works for ".../user/film/{slug}/1/").</summary>
    public static string? SlugFromFilmUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var idx = url.IndexOf("/film/", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var rest = url[(idx + 6)..];
        var end = rest.IndexOfAny(new[] { '/', '?', '#' });
        var slug = end < 0 ? rest : rest[..end];
        return string.IsNullOrWhiteSpace(slug) ? null : slug;
    }
}
