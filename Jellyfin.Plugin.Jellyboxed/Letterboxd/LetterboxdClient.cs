using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellyboxed.Configuration;
using Jellyfin.Plugin.Jellyboxed.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Letterboxd;

/// <summary>Raw result of an HTTP call to Letterboxd.</summary>
public sealed record HttpResult(HttpStatusCode Status, string Body, Uri FinalUri, HttpResponseHeaders Headers)
{
    public bool Ok => (int)Status is >= 200 and < 300;
}

/// <summary>What the user asks to be saved to their Letterboxd diary.</summary>
public sealed record LogRequest(double Rating, string Review, bool Rewatch, bool Loved, bool ContainsSpoilers, DateOnly Date);

/// <summary>
/// One Letterboxd "browser session": a cookie jar plus the scraping / posting operations.
/// Works anonymously (public profile pages) or signed-in (when cookies are present).
/// Instances are cheap and thread-safe enough to be shared for one account.
/// </summary>
public sealed class LetterboxdClient : IDisposable
{
    private const string Base = "https://letterboxd.com";
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly Uri BaseUri = new(Base + "/");

    private readonly RequestGate _gate;
    private readonly ILogger _logger;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (string? ProductionId, string? FilmId)> _filmIds = new();

    public LetterboxdClient(string username, string cookieString, RequestGate gate, ILogger logger)
    {
        Username = username.Trim();
        _gate = gate;
        _logger = logger;

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        SeedCookies(cookieString);
    }

    public string Username { get; }

    /// <summary>Gets a value indicating whether the cookie jar contains a Letterboxd session cookie.</summary>
    public bool HasSessionCookie => _cookies.GetCookies(BaseUri).Cast<Cookie>().Any(c => c.Name == "letterboxd.user.CURRENT" && c.Value.Length > 0)
        || _cookies.GetCookies(BaseUri).Cast<Cookie>().Any(c => c.Name == "letterboxd.signed.in.as" && c.Value.Length > 0);

    /// <summary>Gets the Letterboxd username the session is signed in as (from Letterboxd's own cookie), if any.</summary>
    public string? SignedInAs => _cookies.GetCookies(BaseUri).Cast<Cookie>()
        .FirstOrDefault(c => c.Name == "letterboxd.signed.in.as" && c.Value.Length > 0)?.Value;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Accepts the many formats people paste: "a=b; c=d", a "Cookie:" header line, one cookie per line,
    /// or a JSON array exported by a cookie extension ([{"name":..,"value":..}]).
    /// </summary>
    public static Dictionary<string, string> ParseCookieString(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(input))
        {
            return result;
        }

        var text = input.Trim();
        if (text.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("name", out var n) && el.TryGetProperty("value", out var v)
                        && n.ValueKind == JsonValueKind.String && v.ValueKind == JsonValueKind.String)
                    {
                        result[n.GetString()!] = v.GetString()!;
                    }
                }

                return result;
            }
            catch (JsonException)
            {
                // Fall through to header parsing.
            }
        }

        if (text.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..];
        }

        foreach (var part in text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim().Trim('"');
            if (name.Length > 0 && value.Length > 0)
            {
                result[name] = value;
            }
        }

        return result;
    }

    /// <summary>Gets the current cookies serialised as a header string (so rotated cookies can be persisted).</summary>
    public string ExportCookies() =>
        string.Join("; ", _cookies.GetCookies(BaseUri).Cast<Cookie>().Where(c => c.Value.Length > 0).Select(c => $"{c.Name}={c.Value}"));

    // ------------------------------------------------------------------ sign-in

    /// <summary>Checks (cheaply) whether the cookie jar is a signed-in Letterboxd session.</summary>
    public async Task<bool> IsSignedInAsync(CancellationToken ct)
    {
        var res = await GetAsync(Base + "/", ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            return false;
        }

        var cookieSaysSo = _cookies.GetCookies(BaseUri).Cast<Cookie>().Any(c => c.Name == "letterboxd.signed.in.as" && c.Value.Length > 0);
        return cookieSaysSo
            || res.Body.Contains("Sign out", StringComparison.Ordinal)
            || res.Body.Contains("/" + Username + "/", StringComparison.OrdinalIgnoreCase) && res.Body.Contains("sign-out", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Signs in with a username and password. The password is used once, here, and is never stored;
    /// only the resulting session cookies are kept. Letterboxd may demand a captcha, in which case
    /// <see cref="LetterboxdErrorKind.Captcha"/> is thrown and the UI offers the cookie fallback.
    /// </summary>
    public async Task LoginAsync(string usernameOrEmail, string password, CancellationToken ct)
    {
        var page = await GetAsync(Base + "/sign-in/", ct).ConfigureAwait(false);
        if (!page.Ok)
        {
            throw new LetterboxdException(LetterboxdErrorKind.Failed, $"Could not open Letterboxd's sign-in page (HTTP {(int)page.Status}).");
        }

        var csrf = _cookies.GetCookies(BaseUri).Cast<Cookie>().FirstOrDefault(c => c.Name == "com.xk72.webparts.csrf")?.Value;
        if (string.IsNullOrEmpty(csrf))
        {
            throw new LetterboxdException(LetterboxdErrorKind.Failed, "Letterboxd did not provide a security token for sign-in.");
        }

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = usernameOrEmail,
            ["password"] = password,
            ["remember"] = "true",
            ["__csrf"] = csrf,
        });

        var res = await SendAsync(HttpMethod.Post, Base + "/user/login.do", form, new Dictionary<string, string>
        {
            ["Referer"] = Base + "/sign-in/",
            ["X-Requested-With"] = "XMLHttpRequest",
            ["Accept"] = "application/json, text/javascript, */*; q=0.01",
        }, ct).ConfigureAwait(false);

        var body = res.Body;
        if (body.Contains("captcha", StringComparison.OrdinalIgnoreCase) || body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || res.Status == HttpStatusCode.Forbidden)
        {
            throw new LetterboxdException(
                LetterboxdErrorKind.Captcha,
                "Letterboxd asked for extra verification (captcha), which cannot be completed from the server. Use the cookie method instead.");
        }

        string? result = null;
        string? message = null;
        try
        {
            var json = JsonNode.Parse(body);
            result = json?["result"]?.GetValue<string>();
            message = (json?["messages"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Not JSON; handled below.
        }

        if (!string.Equals(result, "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new LetterboxdException(
                LetterboxdErrorKind.BadCredentials,
                string.IsNullOrWhiteSpace(message) ? "Letterboxd rejected the username or password." : StripTags(message));
        }

        if (!await IsSignedInAsync(ct).ConfigureAwait(false))
        {
            throw new LetterboxdException(LetterboxdErrorKind.Failed, "Signed in, but Letterboxd did not give us a usable session.");
        }
    }

    // ------------------------------------------------------------------ lookups

    /// <summary>TMDB id to Letterboxd slug (uses Letterboxd's own /tmdb/{id}/ redirect).</summary>
    public async Task<string?> ResolveSlugAsync(string tmdbId, CancellationToken ct)
    {
        var res = await GetAsync($"{Base}/tmdb/{Uri.EscapeDataString(tmdbId)}/", ct).ConfigureAwait(false);
        if (res.Status == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!res.Ok)
        {
            // Transient problem: do not let the caller cache this as "film does not exist".
            throw new LetterboxdException(LetterboxdErrorKind.Failed, $"Letterboxd answered HTTP {(int)res.Status} while looking up a film.");
        }

        return LetterboxdParser.SlugFromFilmUrl(res.FinalUri.AbsolutePath);
    }

    /// <summary>Letterboxd slug to TMDB id (read from the film page).</summary>
    public async Task<string?> ResolveTmdbIdAsync(string slug, CancellationToken ct)
    {
        var res = await GetAsync($"{Base}/film/{slug}/", ct).ConfigureAwait(false);
        return res.Ok ? LetterboxdParser.ParseTmdbId(res.Body) : null;
    }

    /// <summary>Walks a paginated poster grid and collects slugs. Stops at the first empty or repeated page.</summary>
    public async Task<HashSet<string>> ScrapeGridAsync(Func<int, string> urlForPage, int maxPages, CancellationToken ct)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= maxPages; page++)
        {
            var res = await GetAsync(urlForPage(page), ct).ConfigureAwait(false);
            if (res.Status == HttpStatusCode.NotFound)
            {
                // Page 1 missing: an empty bucket (normal) or no such profile; later pages: we ran past the end.
                break;
            }

            if (!res.Ok)
            {
                if (page == 1 && (res.Status == HttpStatusCode.Forbidden || res.Status == HttpStatusCode.Unauthorized))
                {
                    throw new LetterboxdException(LetterboxdErrorKind.ProfileUnavailable, "This Letterboxd profile is private or unavailable.");
                }

                // Never return a half-read list: callers replace their cache with the result.
                throw new LetterboxdException(LetterboxdErrorKind.Failed, $"Letterboxd answered HTTP {(int)res.Status} while reading a list.");
            }

            var found = LetterboxdParser.ParseGridSlugs(res.Body);
            var before = slugs.Count;
            slugs.UnionWith(found);
            if (found.Count == 0 || slugs.Count == before)
            {
                break;
            }
        }

        return slugs;
    }

    public async Task<List<RssEntry>> GetRssAsync(CancellationToken ct)
    {
        var res = await GetAsync($"{Base}/{Uri.EscapeDataString(Username)}/rss/", ct).ConfigureAwait(false);
        if (res.Status == HttpStatusCode.NotFound)
        {
            throw new LetterboxdException(LetterboxdErrorKind.ProfileUnavailable, $"No Letterboxd profile named '{Username}' was found.");
        }

        return res.Ok ? LetterboxdParser.ParseRss(res.Body) : new List<RssEntry>();
    }

    /// <summary>First page of the watchlist (newest additions first).</summary>
    public async Task<HashSet<string>> GetWatchlistFirstPageAsync(CancellationToken ct)
    {
        var res = await GetAsync($"{Base}/{Uri.EscapeDataString(Username)}/watchlist/", ct).ConfigureAwait(false);
        return res.Ok ? LetterboxdParser.ParseGridSlugs(res.Body) : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Scrapes the user's own rating, like and review text for one film (used when the synced data is stale).</summary>
    public async Task<RatingEntry?> ScrapeOwnRatingAsync(string slug, bool signedIn, CancellationToken ct)
    {
        var loved = false;
        if (signedIn)
        {
            var side = await GetAsync($"{Base}/csi/film/{slug}/sidebar-user-actions/?esiAllowUser=true", ct, new Dictionary<string, string>
            {
                ["Referer"] = $"{Base}/film/{slug}/",
            }).ConfigureAwait(false);
            if (side.Ok)
            {
                loved = LetterboxdParser.ParseLoved(side.Body);
            }
        }

        double? rating = null;
        var review = string.Empty;
        var reviewKnown = false;

        var profile = await GetAsync($"{Base}/{Uri.EscapeDataString(Username)}/film/{slug}/", ct).ConfigureAwait(false);
        if (profile.Ok)
        {
            rating = LetterboxdParser.ParseOwnRating(profile.Body);
            if (rating is not null)
            {
                review = LetterboxdParser.ParseReviewBody(profile.Body);
                reviewKnown = true;
            }
        }

        if (rating is null && profile.Ok)
        {
            var activity = await GetAsync($"{Base}/{Uri.EscapeDataString(Username)}/film/{slug}/activity/", ct).ConfigureAwait(false);
            if (activity.Ok)
            {
                rating = LetterboxdParser.ParseOwnRating(activity.Body);
            }
        }

        return new RatingEntry
        {
            Rating = rating,
            Loved = loved,
            Review = review,
            ReviewKnown = reviewKnown,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Fetches the text of the user's own review for a film.</summary>
    public async Task<string> GetOwnReviewAsync(string slug, CancellationToken ct)
    {
        var profile = await GetAsync($"{Base}/{Uri.EscapeDataString(Username)}/film/{slug}/", ct).ConfigureAwait(false);
        return profile.Ok ? LetterboxdParser.ParseReviewBody(profile.Body) : string.Empty;
    }

    /// <summary>Friends' ratings (and review text) for a film. Requires a signed-in session.</summary>
    public async Task<List<FriendActivity>> GetFriendsAsync(string slug, bool includeReviews, CancellationToken ct)
    {
        var res = await GetAsync($"{Base}/{Uri.EscapeDataString(Username)}/friends/film/{slug}/", ct).ConfigureAwait(false);
        if (res.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new LetterboxdException(LetterboxdErrorKind.NotSignedIn, "Sign in to Letterboxd to see your friends' activity.");
        }

        if (!res.Ok)
        {
            return new List<FriendActivity>();
        }

        var rows = LetterboxdParser.ParseFriends(res.Body);
        if (includeReviews)
        {
            using var limiter = new SemaphoreSlim(4);
            await Task.WhenAll(rows.Where(r => r.ReviewHref is not null).Select(async row =>
            {
                await limiter.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var href = row.ReviewHref!;
                    var url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : Base + href;
                    var page = await GetAsync(url, ct).ConfigureAwait(false);
                    if (page.Ok)
                    {
                        row.Friend.Review = LetterboxdParser.ParseReviewBody(page.Body);
                    }
                }
                catch (LetterboxdException ex) when (ex.Kind != LetterboxdErrorKind.Blocked)
                {
                    _logger.LogDebug(ex, "Jellyboxed: could not load a friend's review");
                }
                finally
                {
                    limiter.Release();
                }
            })).ConfigureAwait(false);
        }

        return rows.Select(r => r.Friend)
            .OrderByDescending(f => f.Review.Length > 0)
            .ThenByDescending(f => f.RatingValue ?? 0)
            .ToList();
    }

    // ------------------------------------------------------------------ writes

    /// <summary>Adds or updates a diary entry (rating, like, review). Requires a signed-in session.</summary>
    public async Task LogFilmAsync(string slug, LogRequest request, CancellationToken ct)
    {
        var (productionId, filmId) = await GetFilmIdsAsync(slug, ct).ConfigureAwait(false);
        if (productionId is null && filmId is null)
        {
            throw new LetterboxdException(LetterboxdErrorKind.Failed, "Could not find Letterboxd's internal id for this film.");
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var csrf = await GetCsrfAsync(attempt > 0, ct).ConfigureAwait(false);

            var payload = new JsonObject
            {
                ["diaryDetails"] = new JsonObject
                {
                    ["diaryDate"] = request.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    ["rewatch"] = request.Rewatch,
                },
                ["like"] = request.Loved,

                // Always send the rating explicitly: null clears it when editing.
                ["rating"] = request.Rating > 0 ? JsonValue.Create(request.Rating) : null,
            };

            if (!string.IsNullOrWhiteSpace(request.Review))
            {
                payload["review"] = new JsonObject { ["text"] = request.Review, ["containsSpoilers"] = request.ContainsSpoilers };
            }

            HttpResult? last = null;
            foreach (var (endpoint, field, id) in new[]
            {
                ($"{Base}/api/v0/production-log-entries", "productionId", productionId),
                ($"{Base}/api/v0/log-entries", "filmId", filmId),
            })
            {
                if (id is null)
                {
                    continue;
                }

                var body = JsonNode.Parse(payload.ToJsonString())!.AsObject();
                body[field] = id;
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                last = await SendAsync(HttpMethod.Post, endpoint, content, WriteHeaders($"{Base}/film/{slug}/", csrf), ct).ConfigureAwait(false);
                if (last.Status == HttpStatusCode.NotFound)
                {
                    continue;
                }

                break;
            }

            if (last is null)
            {
                break;
            }

            if (last.Ok)
            {
                return;
            }

            if (last.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 0)
            {
                continue; // refresh the CSRF token once and try again
            }

            if (last.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new LetterboxdException(LetterboxdErrorKind.NotSignedIn, "Your Letterboxd session has expired. Sign in again.");
            }

            _logger.LogWarning("Jellyboxed: Letterboxd rejected the diary entry (HTTP {Status}): {Body}", (int)last.Status, Truncate(last.Body, 300));
            throw new LetterboxdException(LetterboxdErrorKind.Failed, $"Letterboxd rejected the entry (HTTP {(int)last.Status}).");
        }

        throw new LetterboxdException(LetterboxdErrorKind.Failed, "Could not save the entry to Letterboxd.");
    }

    /// <summary>Adds or removes a film from the watchlist. Requires a signed-in session.</summary>
    public async Task SetWatchlistAsync(string slug, bool add, CancellationToken ct)
    {
        var (_, filmId) = await GetFilmIdsAsync(slug, ct).ConfigureAwait(false);
        if (filmId is null)
        {
            throw new LetterboxdException(LetterboxdErrorKind.Failed, "Could not find Letterboxd's film id; the watchlist was not changed.");
        }

        var action = add ? "add-to-watchlist" : "remove-from-watchlist";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var csrf = await GetCsrfAsync(attempt > 0, ct).ConfigureAwait(false);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["__csrf"] = csrf });
            var res = await SendAsync(HttpMethod.Post, $"{Base}/film/{filmId}/{action}/", form, WriteHeaders($"{Base}/film/{slug}/", csrf), ct)
                .ConfigureAwait(false);

            try
            {
                if (JsonNode.Parse(res.Body)?["result"]?.GetValue<bool>() == true)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // Not JSON.
            }

            if (res.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 0)
            {
                continue;
            }

            if (res.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new LetterboxdException(LetterboxdErrorKind.NotSignedIn, "Your Letterboxd session has expired. Sign in again.");
            }

            _logger.LogWarning("Jellyboxed: watchlist {Action} failed (HTTP {Status}): {Body}", action, (int)res.Status, Truncate(res.Body, 200));
            throw new LetterboxdException(LetterboxdErrorKind.Failed, $"Letterboxd rejected the watchlist change (HTTP {(int)res.Status}).");
        }
    }

    // ------------------------------------------------------------------ plumbing

    public void Dispose() => _http.Dispose();

    private static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", string.Empty).Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static Dictionary<string, string> WriteHeaders(string referer, string csrf) => new()
    {
        ["Referer"] = referer,
        ["X-Requested-With"] = "XMLHttpRequest",
        ["X-CSRF-TOKEN"] = csrf,
        ["Accept"] = "application/json",
        ["sec-fetch-dest"] = "empty",
        ["sec-fetch-mode"] = "cors",
        ["sec-fetch-site"] = "same-origin",
    };

    private void SeedCookies(string cookieString)
    {
        foreach (var (name, value) in ParseCookieString(cookieString))
        {
            try
            {
                _cookies.Add(BaseUri, new Cookie(name, value, "/", "letterboxd.com"));
            }
            catch (CookieException ex)
            {
                _logger.LogDebug(ex, "Jellyboxed: skipped an invalid cookie '{Name}'", name);
            }
        }
    }

    private async Task<string> GetCsrfAsync(bool forceRefresh, CancellationToken ct)
    {
        string? Token() => _cookies.GetCookies(BaseUri).Cast<Cookie>().FirstOrDefault(c => c.Name == "com.xk72.webparts.csrf")?.Value;

        var token = Token();
        if (forceRefresh || string.IsNullOrEmpty(token))
        {
            await GetAsync(Base + "/", ct).ConfigureAwait(false);
            token = Token();
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new LetterboxdException(LetterboxdErrorKind.NotSignedIn, "No Letterboxd security token: the session cookies are missing or expired.");
        }

        return token;
    }

    private async Task<(string? ProductionId, string? FilmId)> GetFilmIdsAsync(string slug, CancellationToken ct)
    {
        if (_filmIds.TryGetValue(slug, out var cached))
        {
            return cached;
        }

        var res = await GetAsync($"{Base}/film/{slug}/", ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            throw new LetterboxdException(LetterboxdErrorKind.NotFound, "That film was not found on Letterboxd.");
        }

        string? header = null;
        if (res.Headers.TryGetValues("x-letterboxd-identifier", out var values))
        {
            header = values.FirstOrDefault();
        }

        var ids = LetterboxdParser.ParseFilmIds(res.Body, header, slug);
        if (ids.ProductionId is not null || ids.FilmId is not null)
        {
            _filmIds[slug] = ids;
        }

        return ids;
    }

    private Task<HttpResult> GetAsync(string url, CancellationToken ct, IDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Get, url, null, headers, ct);

    private async Task<HttpResult> SendAsync(HttpMethod method, string url, HttpContent? content, IDictionary<string, string>? headers, CancellationToken ct)
    {
        using var slot = await _gate.EnterAsync(ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, url) { Content = content };
        var ua = string.IsNullOrWhiteSpace(Config.UserAgent) ? DefaultUserAgent : Config.UserAgent;
        request.Headers.TryAddWithoutValidation("User-Agent", ua);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.5");
        request.Headers.TryAddWithoutValidation("Referer", Base + "/");
        if (method != HttpMethod.Get)
        {
            request.Headers.TryAddWithoutValidation("Origin", Base);
        }

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                request.Headers.Remove(k);
                request.Headers.TryAddWithoutValidation(k, v);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Config.RequestTimeoutSeconds, 5, 120)));

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var botCheck = response.StatusCode == HttpStatusCode.Forbidden
                && (body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) || body.Contains("cf-chl", StringComparison.OrdinalIgnoreCase)
                    || response.Headers.Server.ToString().Contains("cloudflare", StringComparison.OrdinalIgnoreCase));
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
            _gate.Report(response.StatusCode, retryAfter, botCheck);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Jellyboxed: {Method} {Url} -> {Status}", method, url, (int)response.StatusCode);
            }

            return new HttpResult(response.StatusCode, body, response.RequestMessage?.RequestUri ?? new Uri(url), response.Headers);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LetterboxdException(LetterboxdErrorKind.Failed, "Letterboxd took too long to respond.");
        }
        catch (HttpRequestException ex)
        {
            throw new LetterboxdException(LetterboxdErrorKind.Failed, "Could not reach Letterboxd: " + ex.Message);
        }
    }
}
