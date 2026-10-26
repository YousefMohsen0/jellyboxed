using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellyboxed.Configuration;
using Jellyfin.Plugin.Jellyboxed.Data;
using Jellyfin.Plugin.Jellyboxed.Letterboxd;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Services;

/// <summary>What a queued sync should do.</summary>
public enum SyncKind
{
    /// <summary>Re-read ratings, likes and the whole watchlist.</summary>
    Full,

    /// <summary>Cheap check for recent activity (RSS diary + first watchlist page).</summary>
    Quick,

    /// <summary>Continue matching Letterboxd films to library movies.</summary>
    Resolve,
}

/// <summary>
/// Keeps each connected account's cache fresh in the background. Designed to be gentle: one account at a time,
/// every request goes through the shared <see cref="RequestGate"/>, and a failed run never wipes existing data.
/// </summary>
public sealed class SyncService : BackgroundService
{
    private const int MaxResolvePerRun = 1200;
    private static readonly double[] RatingBuckets = { 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5 };

    private readonly AccountStore _accounts;
    private readonly AccountStateStore _states;
    private readonly ClientPool _pool;
    private readonly SlugResolver _slugs;
    private readonly JellyfinBridge _jellyfin;
    private readonly ILogger<SyncService> _logger;
    private readonly ConcurrentDictionary<Guid, SyncStatus> _status = new();
    private readonly ConcurrentQueue<(Guid UserId, SyncKind Kind)> _queue = new();
    private readonly ConcurrentDictionary<(Guid, SyncKind), byte> _queued = new();
    private readonly SemaphoreSlim _wake = new(0);

    public SyncService(
        AccountStore accounts,
        AccountStateStore states,
        ClientPool pool,
        SlugResolver slugs,
        JellyfinBridge jellyfin,
        ILogger<SyncService> logger)
    {
        _accounts = accounts;
        _states = states;
        _pool = pool;
        _slugs = slugs;
        _jellyfin = jellyfin;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public SyncStatus GetStatus(Guid userId) => _status.GetOrAdd(userId, _ => new SyncStatus());

    /// <summary>Asks for a sync as soon as the worker is free (duplicate requests are merged).</summary>
    public void Enqueue(Guid userId, bool full) => Enqueue(userId, full ? SyncKind.Full : SyncKind.Quick);

    public void Enqueue(Guid userId, SyncKind kind)
    {
        if (_queued.TryAdd((userId, kind), 0))
        {
            _queue.Enqueue((userId, kind));
            _wake.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Config.EnableBackgroundSync)
                {
                    ScheduleDueAccounts();
                }

                while (_queue.TryDequeue(out var job))
                {
                    _queued.TryRemove(job, out _);
                    await RunJobAsync(job.UserId, job.Kind, stoppingToken).ConfigureAwait(false);
                }

                await _wake.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jellyboxed: sync loop error");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void ScheduleDueAccounts()
    {
        var cfg = Config;
        var fullEvery = TimeSpan.FromHours(Math.Max(1, cfg.FullSyncIntervalHours));
        var quickEvery = TimeSpan.FromMinutes(cfg.QuickSyncIntervalMinutes);
        var now = DateTimeOffset.UtcNow;

        foreach (var account in _accounts.All().Where(a => a.Level != AuthLevel.None))
        {
            var file = _states.Get(account.UserId);
            DateTimeOffset? full;
            DateTimeOffset? quick;
            lock (file.SyncRoot)
            {
                full = file.Value.FullSyncAt;
                quick = file.Value.QuickSyncAt;
            }

            if (full is null || now - full > fullEvery)
            {
                // After a failed attempt wait a little before retrying.
                if (GetStatus(account.UserId).LastFinished is not { } last || now - last > TimeSpan.FromMinutes(15))
                {
                    Enqueue(account.UserId, SyncKind.Full);
                }
            }
            else if (cfg.QuickSyncIntervalMinutes > 0 && (quick is null || now - quick > quickEvery))
            {
                Enqueue(account.UserId, SyncKind.Quick);
            }
        }
    }

    private async Task RunJobAsync(Guid userId, SyncKind kind, CancellationToken ct)
    {
        // Only background work may read pages through the solver; page views must never wait on it.
        CloudflarePass.Background = true;
        var account = _accounts.Get(userId);
        if (account is null || account.Level == AuthLevel.None)
        {
            return;
        }

        var status = GetStatus(userId);
        if (status.Running)
        {
            return;
        }

        status.Running = true;
        status.LastError = null;
        status.Done = 0;
        status.Total = 0;
        try
        {
            status.Phase = kind.ToString().ToLowerInvariant();
            switch (kind)
            {
                case SyncKind.Full:
                    await FullSyncAsync(account, status, ct).ConfigureAwait(false);
                    break;
                case SyncKind.Quick:
                    await QuickSyncAsync(account, status, ct).ConfigureAwait(false);
                    break;
                default:
                    await ResolveAsync(account, status, ct).ConfigureAwait(false);
                    break;
            }

            _accounts.Update(userId, a => a.LastError = null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LetterboxdException ex)
        {
            status.LastError = ex.Message;
            _logger.LogWarning("Jellyboxed: {Kind} sync for {Username} stopped: {Message}", kind, account.Username, ex.Message);
            _accounts.Update(userId, a =>
            {
                a.LastError = ex.Message;
                if (ex.Kind == LetterboxdErrorKind.NotSignedIn)
                {
                    a.SessionValid = false;
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // The account was reconnected or disconnected while this sync was running (its session was replaced).
            _logger.LogInformation("Jellyboxed: {Kind} sync for {Username} was interrupted because the account changed; it will start again.", kind, account.Username);
            status.LastError = null;
            if (_accounts.Get(userId) is not null)
            {
                Enqueue(userId, kind);
            }
        }
        catch (Exception ex)
        {
            status.LastError = "Unexpected error: " + ex.Message;
            _logger.LogError(ex, "Jellyboxed: {Kind} sync for {Username} failed", kind, account.Username);
        }
        finally
        {
            status.Running = false;
            status.Phase = "idle";
            status.LastFinished = DateTimeOffset.UtcNow;
            _pool.PersistCookies(userId);
        }
    }

    // ------------------------------------------------------------------ full sync

    private async Task FullSyncAsync(LetterboxdAccount account, SyncStatus status, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var client = _pool.Get(account);

        if (account.Level == AuthLevel.Session && !await client.IsSignedInAsync(ct).ConfigureAwait(false))
        {
            _accounts.Update(account.UserId, a =>
            {
                a.SessionValid = false;
                a.LastError = "Your Letterboxd session has expired. Sign in again to keep logging and see friends' activity.";
            });
            account = _accounts.Get(account.UserId) ?? account;
            _pool.Forget(account.UserId);
            client = _pool.Get(account);
        }

        var user = Uri.EscapeDataString(account.Username);

        // 1. Ratings, one list per star value (so we know which films have which rating).
        var ratings = new Dictionary<string, double>(StringComparer.Ordinal);
        status.Total = RatingBuckets.Length + 4;
        foreach (var value in RatingBuckets)
        {
            status.Phase = $"reading {value.ToString("0.#", CultureInfo.InvariantCulture)}★ films";
            var slugs = await ScrapeRatedBucketAsync(client, user, value, ct).ConfigureAwait(false);
            foreach (var slug in slugs)
            {
                ratings[slug] = value;
            }

            status.Done++;
        }

        // 2. Likes and watchlist.
        status.Phase = "reading likes";
        var liked = await client.ScrapeGridAsync(p => $"https://letterboxd.com/{user}/likes/films/page/{p}/", 100, ct).ConfigureAwait(false);
        status.Done++;

        status.Phase = "reading watchlist";
        HashSet<string>? watchlist = null;
        try
        {
            watchlist = await client.ScrapeGridAsync(p => $"https://letterboxd.com/{user}/watchlist/page/{p}/", 100, ct).ConfigureAwait(false);
        }
        catch (LetterboxdException ex) when (ex.Kind == LetterboxdErrorKind.ProfileUnavailable)
        {
            _logger.LogInformation("Jellyboxed: watchlist of {Username} is not readable; skipping it", account.Username);
        }

        status.Done++;

        // 2b. Everything marked as watched, and the diary (dates, rewatches). Optional detail: a failure here
        // keeps what we already know instead of failing the whole sync.
        status.Phase = "reading watched films";
        HashSet<string>? watchedAll = null;
        try
        {
            watchedAll = await client.ScrapeGridAsync(p => $"https://letterboxd.com/{user}/films/page/{p}/", 100, ct).ConfigureAwait(false);
        }
        catch (LetterboxdException ex) when (ex.Kind != LetterboxdErrorKind.Blocked)
        {
            _logger.LogInformation("Jellyboxed: could not read the watched films of {Username}: {Message}", account.Username, ex.Message);
        }

        status.Done++;

        status.Phase = "reading diary";
        List<DiaryRow>? diaryRows = null;
        try
        {
            diaryRows = await client.ScrapeDiaryAsync(user, 60, ct).ConfigureAwait(false);
        }
        catch (LetterboxdException ex) when (ex.Kind != LetterboxdErrorKind.Blocked)
        {
            _logger.LogInformation("Jellyboxed: could not read the diary of {Username}: {Message}", account.Username, ex.Message);
        }

        status.Done++;

        // 3. Apply atomically. Nothing above has touched the cache, so a failure keeps the old data intact.
        var file = _states.Get(account.UserId);
        lock (file.SyncRoot)
        {
            var existing = file.Value.Ratings;
            var next = new Dictionary<string, RatingEntry>(StringComparer.Ordinal);
            foreach (var slug in ratings.Keys.Union(liked))
            {
                existing.TryGetValue(slug, out var old);
                var rating = ratings.TryGetValue(slug, out var r) ? r : (double?)null;
                var sameRating = old is not null && Nullable.Equals(old.Rating, rating);
                next[slug] = new RatingEntry
                {
                    Rating = rating,
                    Loved = liked.Contains(slug),
                    Review = sameRating ? old!.Review : string.Empty,
                    ReviewKnown = sameRating && old!.ReviewKnown,
                    UpdatedAt = started,
                };
            }

            // Keep anything the user changed while we were reading (Letterboxd pages can lag a few seconds).
            foreach (var (slug, old) in existing)
            {
                if (!next.ContainsKey(slug) && old.UpdatedAt > started.AddMinutes(-5))
                {
                    next[slug] = old;
                }
            }

            file.Value.Ratings = next;
            if (watchedAll is not null)
            {
                file.Value.Watched = new HashSet<string>(watchedAll, StringComparer.Ordinal);
            }

            file.Value.Watched.UnionWith(next.Keys.Where(k => next[k].Rating is not null));
            if (diaryRows is { Count: > 0 })
            {
                var diary = new Dictionary<string, WatchInfo>(StringComparer.Ordinal);
                foreach (var group in diaryRows.GroupBy(r => r.Slug, StringComparer.Ordinal))
                {
                    var count = group.Count();
                    diary[group.Key] = new WatchInfo
                    {
                        Last = group.Where(r => r.Date is not null).Select(r => r.Date!).DefaultIfEmpty().Max(),
                        Count = count,
                        Rewatch = count > 1 || group.Any(r => r.Rewatch),
                    };
                }

                file.Value.Diary = diary;
                file.Value.Watched.UnionWith(diary.Keys);
            }

            if (watchlist is not null)
            {
                file.Value.Watchlist = watchlist;
            }

            file.Value.FullSyncAt = DateTimeOffset.UtcNow;
            file.Value.LastSyncRated = ratings.Count;
            file.Value.LastSyncLiked = liked.Count;
            file.Value.LastSyncWatchlist = watchlist?.Count ?? file.Value.LastSyncWatchlist;
        }

        _states.Touch(account.UserId);
        _jellyfin.InvalidateIndex();

        // 4. Match Letterboxd films with library movies.
        await ResolveAsync(account, status, ct).ConfigureAwait(false);

        status.LastResult = $"{ratings.Count} rated, {liked.Count} liked, {watchlist?.Count ?? 0} on watchlist";
        _logger.LogInformation("Jellyboxed: full sync for {Username}: {Result}", account.Username, status.LastResult);
    }

    private static async Task<HashSet<string>> ScrapeRatedBucketAsync(LetterboxdClient client, string user, double value, CancellationToken ct)
    {
        // Letterboxd writes half stars as ".5", "1.5", ...; fall back to "0.5" in case that is what it expects.
        var forms = value < 1
            ? new[] { ".5", "0.5" }
            : new[] { value.ToString("0.#", CultureInfo.InvariantCulture) };

        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (var form in forms)
        {
            result = await client.ScrapeGridAsync(p => $"https://letterboxd.com/{user}/films/rated/{form}/page/{p}/", 100, ct).ConfigureAwait(false);
            if (result.Count > 0)
            {
                break;
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ matching films to the library

    /// <summary>
    /// Letterboxd lists films by slug, Jellyfin by TMDB id, and the only bridge is one request per film.
    /// Instead of resolving everything, we resolve whichever side is smaller (the unmatched history, or the unmatched library),
    /// remember the result forever, and only ever do the difference on later runs.
    /// </summary>
    private async Task ResolveAsync(LetterboxdAccount account, SyncStatus status, CancellationToken ct)
    {
        var file = _states.Get(account.UserId);
        HashSet<string> historySlugs;
        lock (file.SyncRoot)
        {
            historySlugs = new HashSet<string>(file.Value.Ratings.Keys, StringComparer.Ordinal);
            historySlugs.UnionWith(file.Value.Watchlist);
        }

        var libraryTmdb = _jellyfin.GetMovies().Select(m => m.TmdbId).Distinct(StringComparer.Ordinal).ToList();
        var unknownLibrary = libraryTmdb.Where(t => !_slugs.IsKnown(t)).ToList();
        var unknownHistory = historySlugs.Where(s => _slugs.TmdbForSlug(s) is null && !_slugs.IsKnown("slug:" + s)).ToList();

        if (unknownLibrary.Count == 0 || unknownHistory.Count == 0)
        {
            return;
        }

        var client = _pool.Get(account);
        var byLibrary = unknownLibrary.Count <= unknownHistory.Count;
        var work = byLibrary ? unknownLibrary : unknownHistory;
        var batch = work.Take(MaxResolvePerRun).ToList();
        status.Phase = byLibrary ? "matching library movies" : "matching your films";
        status.Done = 0;
        status.Total = work.Count;

        var options = new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct };
        try
        {
            await Parallel.ForEachAsync(batch, options, async (item, token) =>
            {
                if (byLibrary)
                {
                    await _slugs.ResolveAsync(item, token).ConfigureAwait(false);
                }
                else
                {
                    var tmdb = await client.ResolveTmdbIdAsync(item, token).ConfigureAwait(false);
                    if (tmdb is not null)
                    {
                        _slugs.Remember(tmdb, item);
                    }
                    else
                    {
                        _slugs.Remember("slug:" + item, null); // no TMDB id (e.g. a TV entry): do not retry for a while
                    }
                }

                var done = Interlocked.Increment(ref status.DoneCounter);
                status.Done = done;
                if (done % 100 == 0)
                {
                    _states.Touch(account.UserId); // let poster badges appear progressively
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            status.DoneCounter = 0;
            _states.Touch(account.UserId);
        }

        if (work.Count > batch.Count)
        {
            _logger.LogInformation("Jellyboxed: {Left} films still to match for {Username}; continuing shortly", work.Count - batch.Count, account.Username);
            Enqueue(account.UserId, SyncKind.Resolve);
        }
        else
        {
            MirrorWatchlist(account, null);
        }
    }

    // ------------------------------------------------------------------ quick sync

    private async Task QuickSyncAsync(LetterboxdAccount account, SyncStatus status, CancellationToken ct)
    {
        var client = _pool.Get(account);
        status.Phase = "checking recent activity";

        var rss = await client.GetRssAsync(ct).ConfigureAwait(false);
        var recentWatchlist = await client.GetWatchlistFirstPageAsync(ct).ConfigureAwait(false);

        var file = _states.Get(account.UserId);
        var newWatchlist = new List<string>();
        var changed = false;
        lock (file.SyncRoot)
        {
            // Newest entry first in the feed; apply oldest-first so the latest state wins.
            foreach (var entry in Enumerable.Reverse(rss))
            {
                if (entry.TmdbId is not null)
                {
                    _slugs.Remember(entry.TmdbId, entry.Slug);
                }

                if (file.Value.NoteViewing(entry.Slug, entry.WatchedDate, entry.Rewatch))
                {
                    changed = true;
                }

                if (entry.Rating is null && !entry.Liked)
                {
                    continue;
                }

                file.Value.Ratings.TryGetValue(entry.Slug, out var old);
                var rating = entry.Rating ?? old?.Rating;
                var loved = entry.Liked || (old?.Loved ?? false);
                if (old is null || !Nullable.Equals(old.Rating, rating) || old.Loved != loved)
                {
                    file.Value.Ratings[entry.Slug] = new RatingEntry
                    {
                        Rating = rating,
                        Loved = loved,
                        Review = old is not null && Nullable.Equals(old.Rating, rating) ? old.Review : string.Empty,
                        ReviewKnown = old is not null && Nullable.Equals(old.Rating, rating) && old.ReviewKnown,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    changed = true;
                }
            }

            foreach (var slug in recentWatchlist)
            {
                if (file.Value.Watchlist.Add(slug))
                {
                    newWatchlist.Add(slug);
                    changed = true;
                }
            }

            file.Value.QuickSyncAt = DateTimeOffset.UtcNow;
        }

        if (changed)
        {
            _states.Touch(account.UserId);
        }
        else
        {
            file.MarkDirty();
        }

        if (newWatchlist.Count > 0)
        {
            MirrorWatchlist(account, newWatchlist);
        }

        status.LastResult = changed ? "found new activity" : "nothing new";
    }

    // ------------------------------------------------------------------ watchlist -> favourites

    /// <summary>Adds Jellyfin favourites for watchlisted films that are in the library. Never removes anything.</summary>
    private void MirrorWatchlist(LetterboxdAccount account, ICollection<string>? only)
    {
        if (!account.WatchlistToFavorites || !Config.AllowWatchlistToFavorites)
        {
            return;
        }

        var file = _states.Get(account.UserId);
        HashSet<string> slugs;
        lock (file.SyncRoot)
        {
            slugs = only is null ? new HashSet<string>(file.Value.Watchlist, StringComparer.Ordinal) : new HashSet<string>(only, StringComparer.Ordinal);
        }

        if (slugs.Count == 0)
        {
            return;
        }

        var byTmdb = _jellyfin.GetMovies().GroupBy(m => m.TmdbId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(m => m.ItemId).ToList(), StringComparer.Ordinal);
        var added = 0;
        foreach (var slug in slugs)
        {
            var tmdb = _slugs.TmdbForSlug(slug);
            if (tmdb is null || !byTmdb.TryGetValue(tmdb, out var items))
            {
                continue;
            }

            foreach (var itemId in items)
            {
                if (!_jellyfin.IsFavorite(account.UserId, itemId))
                {
                    _jellyfin.SetFavorite(account.UserId, itemId, true);
                    added++;
                }
            }
        }

        if (added > 0)
        {
            _logger.LogInformation("Jellyboxed: marked {Count} watchlist film(s) as favourites for {Username}", added, account.Username);
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
