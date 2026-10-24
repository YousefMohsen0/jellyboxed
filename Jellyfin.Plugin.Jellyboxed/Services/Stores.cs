using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellyboxed.Data;
using Jellyfin.Plugin.Jellyboxed.Letterboxd;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Services;

internal static class PluginPaths
{
    public static string DataFolder
    {
        get
        {
            var dir = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "jellyboxed");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

/// <summary>Document behind accounts.json.</summary>
public sealed class AccountsDocument
{
    public List<LetterboxdAccount> Accounts { get; set; } = new();
}

/// <summary>Letterboxd accounts, one per Jellyfin user. Holds secrets, so lives in its own owner-only file.</summary>
public sealed class AccountStore : IDisposable
{
    private readonly DebouncedJsonFile<AccountsDocument> _file;

    public AccountStore(ILogger<AccountStore> logger)
    {
        _file = new DebouncedJsonFile<AccountsDocument>(Path.Combine(PluginPaths.DataFolder, "accounts.json"), logger, restrictPermissions: true);
    }

    public event Action<Guid>? AccountChanged;

    public IReadOnlyList<LetterboxdAccount> All()
    {
        lock (_file.SyncRoot)
        {
            return _file.Value.Accounts.Select(Clone).ToList();
        }
    }

    public LetterboxdAccount? Get(Guid userId)
    {
        lock (_file.SyncRoot)
        {
            var found = _file.Value.Accounts.FirstOrDefault(a => a.UserId == userId);
            return found is null ? null : Clone(found);
        }
    }

    /// <summary>Creates or modifies an account atomically.</summary>
    public LetterboxdAccount Update(Guid userId, Action<LetterboxdAccount> change)
    {
        LetterboxdAccount copy;
        lock (_file.SyncRoot)
        {
            var account = _file.Value.Accounts.FirstOrDefault(a => a.UserId == userId);
            if (account is null)
            {
                account = new LetterboxdAccount { UserId = userId };
                _file.Value.Accounts.Add(account);
            }

            change(account);
            copy = Clone(account);
        }

        _file.MarkDirty();
        AccountChanged?.Invoke(userId);
        return copy;
    }

    public bool Remove(Guid userId)
    {
        bool removed;
        lock (_file.SyncRoot)
        {
            removed = _file.Value.Accounts.RemoveAll(a => a.UserId == userId) > 0;
        }

        if (removed)
        {
            _file.MarkDirty();
            AccountChanged?.Invoke(userId);
        }

        return removed;
    }

    public void Dispose() => _file.Dispose();

    private static LetterboxdAccount Clone(LetterboxdAccount a) => new()
    {
        UserId = a.UserId,
        Username = a.Username,
        Cookies = a.Cookies,
        AuthMode = a.AuthMode,
        ConnectedAt = a.ConnectedAt,
        SessionValid = a.SessionValid,
        LastError = a.LastError,
        WatchlistToFavorites = a.WatchlistToFavorites,
        LovedToFavorite = a.LovedToFavorite,
        MarkPlayedOnLog = a.MarkPlayedOnLog,
        PromptAfterPlayback = a.PromptAfterPlayback,
    };
}

/// <summary>Per-user cached Letterboxd data (ratings, likes, watchlist).</summary>
public sealed class AccountStateStore : IDisposable
{
    private readonly ILogger<AccountStateStore> _logger;
    private readonly ConcurrentDictionary<Guid, DebouncedJsonFile<AccountState>> _files = new();
    private readonly ConcurrentDictionary<Guid, long> _versions = new();

    public AccountStateStore(ILogger<AccountStateStore> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets the document for a user. Hold <see cref="DebouncedJsonFile{T}.SyncRoot"/> while using it.</summary>
    public DebouncedJsonFile<AccountState> Get(Guid userId) =>
        _files.GetOrAdd(userId, id => new DebouncedJsonFile<AccountState>(Path.Combine(PluginPaths.DataFolder, $"state-{id:N}.json"), _logger));

    /// <summary>Gets a counter that changes whenever the user's cached data changes (used for HTTP ETags).</summary>
    public long Version(Guid userId) => _versions.GetValueOrDefault(userId);

    /// <summary>Records that the user's data changed and schedules a save.</summary>
    public void Touch(Guid userId)
    {
        _versions.AddOrUpdate(userId, 1, (_, v) => v + 1);
        Get(userId).MarkDirty();
    }

    /// <summary>Forgets everything cached for the user (also deletes the file).</summary>
    public void Delete(Guid userId)
    {
        if (_files.TryRemove(userId, out var file))
        {
            file.Dispose();
        }

        try
        {
            File.Delete(Path.Combine(PluginPaths.DataFolder, $"state-{userId:N}.json"));
        }
        catch (IOException)
        {
            // Best effort.
        }

        _versions.AddOrUpdate(userId, 1, (_, v) => v + 1);
    }

    public void Dispose()
    {
        foreach (var f in _files.Values)
        {
            f.Dispose();
        }
    }
}

/// <summary>Document behind slugs.json.</summary>
public sealed class SlugDocument
{
    public Dictionary<string, SlugEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The shared TMDB id to Letterboxd slug map. Slugs practically never change, so positive entries are kept
/// for a year; "not found" answers are re-checked after three days. Concurrent lookups of the same id share one request.
/// </summary>
public sealed class SlugResolver : IDisposable
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(365);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(3);

    private readonly DebouncedJsonFile<SlugDocument> _file;
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();
    private readonly LetterboxdClient _anonymous;
    private Dictionary<string, string> _reverse = new(StringComparer.Ordinal);
    private bool _reverseDirty = true;

    public SlugResolver(RequestGate gate, ILogger<SlugResolver> logger)
    {
        _file = new DebouncedJsonFile<SlugDocument>(Path.Combine(PluginPaths.DataFolder, "slugs.json"), logger);
        _anonymous = new LetterboxdClient(string.Empty, string.Empty, gate, logger);
    }

    /// <summary>Returns the cached slug, or null when unknown / known-missing (see <see cref="IsKnown"/>).</summary>
    public string? Peek(string tmdbId)
    {
        lock (_file.SyncRoot)
        {
            return _file.Value.Entries.TryGetValue(tmdbId, out var e) && Fresh(e) ? e.Slug : null;
        }
    }

    /// <summary>True when we have a fresh answer (positive or negative) for this TMDB id.</summary>
    public bool IsKnown(string tmdbId)
    {
        lock (_file.SyncRoot)
        {
            return _file.Value.Entries.TryGetValue(tmdbId, out var e) && Fresh(e);
        }
    }

    public string? TmdbForSlug(string slug)
    {
        lock (_file.SyncRoot)
        {
            if (_reverseDirty)
            {
                _reverse = _file.Value.Entries
                    .Where(kv => kv.Value.Slug is not null)
                    .GroupBy(kv => kv.Value.Slug!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
                _reverseDirty = false;
            }

            return _reverse.GetValueOrDefault(slug);
        }
    }

    public void Remember(string tmdbId, string? slug)
    {
        lock (_file.SyncRoot)
        {
            _file.Value.Entries[tmdbId] = new SlugEntry { Slug = slug, UpdatedAt = DateTimeOffset.UtcNow };
            _reverseDirty = true;
        }

        _file.MarkDirty();
    }

    /// <summary>Gets the slug for a TMDB id, asking Letterboxd only when it is not cached.</summary>
    public Task<string?> ResolveAsync(string tmdbId, CancellationToken ct)
    {
        lock (_file.SyncRoot)
        {
            if (_file.Value.Entries.TryGetValue(tmdbId, out var e) && Fresh(e))
            {
                return Task.FromResult(e.Slug);
            }
        }

        return _inflight.GetOrAdd(tmdbId, id => FetchAsync(id));
    }

    public void Clear()
    {
        lock (_file.SyncRoot)
        {
            _file.Value.Entries.Clear();
            _reverseDirty = true;
        }

        _file.MarkDirty();
    }

    public int Count
    {
        get
        {
            lock (_file.SyncRoot)
            {
                return _file.Value.Entries.Count;
            }
        }
    }

    public void Dispose()
    {
        _file.Dispose();
        _anonymous.Dispose();
    }

    private static bool Fresh(SlugEntry e) => DateTimeOffset.UtcNow - e.UpdatedAt < (e.Slug is null ? NegativeTtl : PositiveTtl);

    private async Task<string?> FetchAsync(string tmdbId)
    {
        try
        {
            // Not tied to the caller's cancellation token: other requests may be waiting on this same lookup.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var slug = await _anonymous.ResolveSlugAsync(tmdbId, cts.Token).ConfigureAwait(false);
            Remember(tmdbId, slug);
            return slug;
        }
        finally
        {
            _inflight.TryRemove(tmdbId, out _);
        }
    }
}
