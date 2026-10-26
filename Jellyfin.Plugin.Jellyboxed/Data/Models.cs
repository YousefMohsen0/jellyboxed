using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellyboxed.Data;

/// <summary>How well Jellyboxed can talk to Letterboxd for an account.</summary>
public enum AuthLevel
{
    /// <summary>No Letterboxd username is known.</summary>
    None = 0,

    /// <summary>Only a username: public profile data can be read (ratings, likes, watchlist).</summary>
    Public = 1,

    /// <summary>A signed-in session: everything works (logging, watchlist edits, friends feed).</summary>
    Session = 2,
}

/// <summary>A Jellyfin user's link to a Letterboxd account. Persisted in accounts.json (never in the plugin XML).</summary>
public class LetterboxdAccount
{
    public Guid UserId { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the raw cookie string of the signed-in session (never sent to browsers).</summary>
    public string Cookies { get; set; } = string.Empty;

    /// <summary>Gets or sets how the session was obtained: "none", "login" or "cookies".</summary>
    public string AuthMode { get; set; } = "none";

    public DateTimeOffset? ConnectedAt { get; set; }

    /// <summary>Gets or sets a value indicating whether the stored session was last seen working.</summary>
    public bool SessionValid { get; set; }

    public string? LastError { get; set; }

    // ---- Per-user preferences (admin switches in PluginConfiguration take precedence) ----

    public bool WatchlistToFavorites { get; set; }

    public bool LovedToFavorite { get; set; }

    public bool MarkPlayedOnLog { get; set; }

    public bool PromptAfterPlayback { get; set; } = true;

    public AuthLevel Level =>
        string.IsNullOrWhiteSpace(Username) ? AuthLevel.None :
        (SessionValid && !string.IsNullOrWhiteSpace(Cookies)) ? AuthLevel.Session : AuthLevel.Public;
}

/// <summary>What we know about the user's own rating of one film (keyed by Letterboxd slug).</summary>
public class RatingEntry
{
    public double? Rating { get; set; }

    public bool Loved { get; set; }

    public string Review { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether <see cref="Review"/> has been fetched yet.</summary>
    public bool ReviewKnown { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What the diary says about one film: when it was last watched, how often, and whether any viewing was a rewatch.</summary>
public class WatchInfo
{
    /// <summary>Gets or sets the date of the most recent viewing, as yyyy-MM-dd.</summary>
    public string? Last { get; set; }

    public int Count { get; set; }

    public bool Rewatch { get; set; }
}

/// <summary>Everything cached for a single Jellyfin user's Letterboxd account.</summary>
public class AccountState
{
    public Dictionary<string, RatingEntry> Ratings { get; set; } = new(StringComparer.Ordinal);

    public HashSet<string> Watchlist { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets every film marked as watched on Letterboxd (rated or not).</summary>
    public HashSet<string> Watched { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets diary details (last viewing date, number of viewings, rewatch) per film slug.</summary>
    public Dictionary<string, WatchInfo> Diary { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Records that a film was watched, optionally on a given day (yyyy-MM-dd). Safe to call again for the same viewing.</summary>
    public bool NoteViewing(string slug, string? date, bool rewatch)
    {
        var changed = Watched.Add(slug);
        if (date is null)
        {
            return changed;
        }

        if (!Diary.TryGetValue(slug, out var info))
        {
            Diary[slug] = new WatchInfo { Last = date, Count = 1, Rewatch = rewatch };
            return true;
        }

        if (info.Last is null || string.CompareOrdinal(date, info.Last) > 0)
        {
            info.Last = date;
            info.Count = Math.Max(1, info.Count) + 1;
            info.Rewatch = info.Rewatch || rewatch || info.Count > 1;
            return true;
        }

        return changed;
    }

    public DateTimeOffset? FullSyncAt { get; set; }

    public DateTimeOffset? QuickSyncAt { get; set; }

    public int LastSyncRated { get; set; }

    public int LastSyncLiked { get; set; }

    public int LastSyncWatchlist { get; set; }

    /// <summary>Gets or sets the Jellyfin item ids the user asked not to be prompted about (until the given time).</summary>
    public Dictionary<string, DateTimeOffset> PromptSnoozedUntil { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>TMDB id to Letterboxd slug (shared by all users; slugs essentially never change).</summary>
public class SlugEntry
{
    public string? Slug { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One friend's activity on a film.</summary>
public class FriendActivity
{
    public string Name { get; set; } = string.Empty;

    public string Avatar { get; set; } = string.Empty;

    public string Rating { get; set; } = string.Empty;

    public double? RatingValue { get; set; }

    public string Review { get; set; } = string.Empty;

    public string ProfileUrl { get; set; } = string.Empty;
}
