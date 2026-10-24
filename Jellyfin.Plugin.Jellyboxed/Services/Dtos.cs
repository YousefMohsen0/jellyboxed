using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Jellyboxed.Data;

namespace Jellyfin.Plugin.Jellyboxed.Services;

/// <summary>The user's own Letterboxd state for one film, as sent to the browser.</summary>
public sealed class MineDto
{
    public string Level { get; set; } = "none";

    public string? Slug { get; set; }

    public string? Url { get; set; }

    public double? Rating { get; set; }

    public bool Loved { get; set; }

    public string Review { get; set; } = string.Empty;

    public bool Rated { get; set; }

    public bool InWatchlist { get; set; }

    /// <summary>Gets or sets a value indicating whether the data is older than the cache time and is being refreshed.</summary>
    public bool Stale { get; set; }

    /// <summary>Gets or sets a value indicating whether a first sync has completed (so "not rated" is trustworthy).</summary>
    public bool Synced { get; set; }
}

/// <summary>Poster badge data (compact on purpose: one of these per rated film in the library).</summary>
public sealed class BadgeDto
{
    [JsonPropertyName("r")]
    public double? Rating { get; set; }

    [JsonPropertyName("l")]
    public bool Loved { get; set; }

    [JsonPropertyName("w")]
    public bool Watchlist { get; set; }
}

public sealed class BadgeSet
{
    public string Etag { get; set; } = string.Empty;

    public Dictionary<string, BadgeDto> Items { get; set; } = new();
}

/// <summary>Input for logging a film.</summary>
public sealed class LogInput
{
    public double Rating { get; set; }

    public string Review { get; set; } = string.Empty;

    public bool Rewatch { get; set; }

    public bool Loved { get; set; }

    public bool Spoilers { get; set; }

    /// <summary>Gets or sets the watched date as yyyy-MM-dd; empty means today.</summary>
    public string? Date { get; set; }
}

/// <summary>A "want to log this?" prompt created after a film was watched.</summary>
public sealed class PendingPrompt
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Progress of a background sync, shown on the settings page.</summary>
public sealed class SyncStatus
{
    public bool Running { get; set; }

    public string Phase { get; set; } = "idle";

    public int Done { get; set; }

    public int Total { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastFinished { get; set; }

    public string? LastResult { get; set; }

    /// <summary>Gets or sets an internal counter shared by parallel workers (not sent to clients).</summary>
    [JsonIgnore]
    public int DoneCounter;
}
