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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Services;

/// <summary>
/// The brain behind the API: answers "what does this user think of this film" from the cache first,
/// refreshes stale data in the background (stale-while-revalidate) and never lets two requests scrape the same page at once.
/// </summary>
public sealed class FilmService
{
    private const int MaxFriendCacheEntries = 400;

    private readonly AccountStore _accounts;
    private readonly AccountStateStore _states;
    private readonly ClientPool _pool;
    private readonly SlugResolver _slugs;
    private readonly JellyfinBridge _jellyfin;
    private readonly SyncService _sync;
    private readonly ILogger<FilmService> _logger;

    private readonly ConcurrentDictionary<string, Task> _refreshing = new();
    private readonly ConcurrentDictionary<string, FriendsEntry> _friends = new();
    private readonly ConcurrentDictionary<string, Task<List<FriendActivity>>> _friendsInflight = new();
    private readonly ConcurrentDictionary<Guid, (string Key, BadgeSet Set)> _badgeCache = new();

    public FilmService(
        AccountStore accounts,
        AccountStateStore states,
        ClientPool pool,
        SlugResolver slugs,
        JellyfinBridge jellyfin,
        SyncService sync,
        ILogger<FilmService> logger)
    {
        _accounts = accounts;
        _states = states;
        _pool = pool;
        _slugs = slugs;
        _jellyfin = jellyfin;
        _sync = sync;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public static string FilmUrl(string slug) => $"https://letterboxd.com/film/{slug}/";

    // ------------------------------------------------------------------ own rating

    public async Task<MineDto> GetMineAsync(LetterboxdAccount account, string tmdbId, CancellationToken ct)
    {
        var dto = new MineDto { Level = LevelName(account.Level) };
        if (account.Level == AuthLevel.None)
        {
            return dto;
        }

        var slug = await _slugs.ResolveAsync(tmdbId, ct).ConfigureAwait(false);
        if (slug is null)
        {
            throw new LetterboxdException(LetterboxdErrorKind.NotFound, "This film was not found on Letterboxd.");
        }

        dto.Slug = slug;
        dto.Url = FilmUrl(slug);

        var file = _states.Get(account.UserId);
        RatingEntry? entry;
        DateTimeOffset? syncedAt;
        bool everFullySynced;
        lock (file.SyncRoot)
        {
            everFullySynced = file.Value.FullSyncAt is not null;
            entry = file.Value.Ratings.TryGetValue(slug, out var e) ? Copy(e) : null;
            dto.InWatchlist = file.Value.Watchlist.Contains(slug);
            syncedAt = file.Value.FullSyncAt ?? file.Value.QuickSyncAt;
        }

        var ttl = TimeSpan.FromMinutes(Math.Max(5, Config.RatingCacheMinutes));
        var client = _pool.Get(account);

        if (!everFullySynced)
        {
            // Nothing synced yet: ask Letterboxd about this one film so the page is useful straight away,
            // and make sure the full sync is on its way.
            _sync.Enqueue(account.UserId, full: true);
            entry = await ScrapeAndStoreAsync(account, client, slug, ct).ConfigureAwait(false) ?? entry;
        }
        else
        {
            dto.Synced = true;
            if (syncedAt is null || DateTimeOffset.UtcNow - syncedAt > ttl)
            {
                dto.Stale = true;
                RefreshInBackground(account, slug);
            }
        }

        if (entry is not null && (entry.Rating is not null || entry.Loved) && !entry.ReviewKnown)
        {
            try
            {
                var review = await client.GetOwnReviewAsync(slug, ct).ConfigureAwait(false);
                entry.Review = review;
                entry.ReviewKnown = true;
                lock (file.SyncRoot)
                {
                    if (file.Value.Ratings.TryGetValue(slug, out var stored))
                    {
                        stored.Review = review;
                        stored.ReviewKnown = true;
                    }
                }

                file.MarkDirty();
            }
            catch (LetterboxdException ex)
            {
                _logger.LogDebug(ex, "Jellyboxed: could not load the user's review for {Slug}", slug);
            }
        }

        Fill(dto, entry);
        _pool.PersistCookies(account.UserId);
        return dto;
    }

    private void RefreshInBackground(LetterboxdAccount account, string slug)
    {
        var key = $"{account.UserId:N}/{slug}";
        _refreshing.GetOrAdd(key, k => Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await ScrapeAndStoreAsync(account, _pool.Get(account), slug, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Jellyboxed: background refresh of {Slug} failed", slug);
            }
            finally
            {
                _refreshing.TryRemove(key, out _);
            }
        }));
    }

    private async Task<RatingEntry?> ScrapeAndStoreAsync(LetterboxdAccount account, LetterboxdClient client, string slug, CancellationToken ct)
    {
        var scraped = await client.ScrapeOwnRatingAsync(slug, account.Level == AuthLevel.Session, ct).ConfigureAwait(false);
        if (scraped is null)
        {
            return null;
        }

        var file = _states.Get(account.UserId);
        lock (file.SyncRoot)
        {
            if (scraped.Rating is null && !scraped.Loved)
            {
                file.Value.Ratings.Remove(slug);
            }
            else
            {
                file.Value.Ratings[slug] = scraped;
            }
        }

        _states.Touch(account.UserId);
        return scraped.Rating is null && !scraped.Loved ? null : Copy(scraped);
    }

    // ------------------------------------------------------------------ friends

    public async Task<List<FriendActivity>> GetFriendsAsync(LetterboxdAccount account, string tmdbId, CancellationToken ct)
    {
        if (account.Level != AuthLevel.Session)
        {
            throw new LetterboxdException(LetterboxdErrorKind.NotSignedIn, "Sign in to Letterboxd to see your friends' activity.");
        }

        var slug = await _slugs.ResolveAsync(tmdbId, ct).ConfigureAwait(false)
            ?? throw new LetterboxdException(LetterboxdErrorKind.NotFound, "This film was not found on Letterboxd.");

        var key = $"{account.UserId:N}/{slug}";
        var ttl = TimeSpan.FromMinutes(Math.Max(1, Config.FriendsCacheMinutes));

        if (_friends.TryGetValue(key, out var cached))
        {
            var age = DateTimeOffset.UtcNow - cached.At;
            if (age < ttl)
            {
                return Trim(cached.Items);
            }

            if (age < ttl * 6)
            {
                // Serve the old answer instantly, refresh quietly.
                _ = LoadFriendsAsync(account, slug, key);
                return Trim(cached.Items);
            }
        }

        var items = await LoadFriendsAsync(account, slug, key).WaitAsync(ct).ConfigureAwait(false);
        return Trim(items);
    }

    private Task<List<FriendActivity>> LoadFriendsAsync(LetterboxdAccount account, string slug, string key) =>
        _friendsInflight.GetOrAdd(key, k => Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var items = await _pool.Get(account)
                    .GetFriendsAsync(slug, Config.EnableFriendReviews, cts.Token).ConfigureAwait(false);
                _friends[key] = new FriendsEntry(DateTimeOffset.UtcNow, items);
                if (_friends.Count > MaxFriendCacheEntries)
                {
                    foreach (var old in _friends.OrderBy(kv => kv.Value.At).Take(_friends.Count - MaxFriendCacheEntries).ToList())
                    {
                        _friends.TryRemove(old.Key, out _);
                    }
                }

                _pool.PersistCookies(account.UserId);
                return items;
            }
            finally
            {
                _friendsInflight.TryRemove(key, out _);
            }
        }));

    private static List<FriendActivity> Trim(List<FriendActivity> items) =>
        items.Take(Math.Clamp(Config.MaxFriendsShown, 1, 200)).ToList();

    public void ClearFriendsCache() => _friends.Clear();

    // ------------------------------------------------------------------ writes

    public async Task<MineDto> LogAsync(LetterboxdAccount account, string tmdbId, Guid itemId, LogInput input, CancellationToken ct)
    {
        if (account.Level != AuthLevel.Session)
        {
            throw new LetterboxdException(LetterboxdErrorKind.NotSignedIn, "Sign in to Letterboxd to log films.");
        }

        var slug = await _slugs.ResolveAsync(tmdbId, ct).ConfigureAwait(false)
            ?? throw new LetterboxdException(LetterboxdErrorKind.NotFound, "This film was not found on Letterboxd.");

        var rating = Math.Round(Math.Clamp(input.Rating, 0, 5) * 2, MidpointRounding.AwayFromZero) / 2;
        var date = DateOnly.FromDateTime(DateTime.Now);
        if (!string.IsNullOrWhiteSpace(input.Date)
            && DateOnly.TryParseExact(input.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed <= date)
        {
            date = parsed;
        }

        var review = (input.Review ?? string.Empty).Trim();
        if (review.Length > 20_000)
        {
            review = review[..20_000];
        }

        var client = _pool.Get(account);
        await client.LogFilmAsync(slug, new LogRequest(rating, review, input.Rewatch, input.Loved, input.Spoilers, date), ct).ConfigureAwait(false);
        _pool.PersistCookies(account.UserId);

        var file = _states.Get(account.UserId);
        lock (file.SyncRoot)
        {
            file.Value.Ratings[slug] = new RatingEntry
            {
                Rating = rating > 0 ? rating : null,
                Loved = input.Loved,
                Review = review,
                ReviewKnown = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            file.Value.PromptSnoozedUntil[itemId.ToString("N")] = DateTimeOffset.UtcNow.AddHours(Math.Max(1, Config.PromptCooldownHours));
        }

        _states.Touch(account.UserId);

        if (account.MarkPlayedOnLog)
        {
            _jellyfin.MarkPlayed(account.UserId, itemId);
        }

        if (account.LovedToFavorite)
        {
            _jellyfin.SetFavorite(account.UserId, itemId, input.Loved);
        }

        return await GetMineAsync(account, tmdbId, ct).ConfigureAwait(false);
    }

    public async Task<MineDto> SetWatchlistAsync(LetterboxdAccount account, string tmdbId, Guid itemId, bool add, CancellationToken ct)
    {
        if (account.Level != AuthLevel.Session)
        {
            throw new LetterboxdException(LetterboxdErrorKind.NotSignedIn, "Sign in to Letterboxd to change your watchlist.");
        }

        var slug = await _slugs.ResolveAsync(tmdbId, ct).ConfigureAwait(false)
            ?? throw new LetterboxdException(LetterboxdErrorKind.NotFound, "This film was not found on Letterboxd.");

        await _pool.Get(account).SetWatchlistAsync(slug, add, ct).ConfigureAwait(false);
        _pool.PersistCookies(account.UserId);

        var file = _states.Get(account.UserId);
        lock (file.SyncRoot)
        {
            if (add)
            {
                file.Value.Watchlist.Add(slug);
            }
            else
            {
                file.Value.Watchlist.Remove(slug);
            }
        }

        _states.Touch(account.UserId);

        if (account.WatchlistToFavorites && Config.AllowWatchlistToFavorites)
        {
            _jellyfin.SetFavorite(account.UserId, itemId, add);
        }

        return await GetMineAsync(account, tmdbId, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ poster badges

    /// <summary>Joins the user's Letterboxd data with the movies in the library. Cached until either side changes.</summary>
    public BadgeSet BuildBadges(LetterboxdAccount account)
    {
        var stamp = $"{_jellyfin.IndexStamp}-{_states.Version(account.UserId)}";
        if (_badgeCache.TryGetValue(account.UserId, out var hit) && hit.Key == stamp)
        {
            return hit.Set;
        }

        var set = new BadgeSet { Etag = $"\"{stamp}\"" };
        var file = _states.Get(account.UserId);
        lock (file.SyncRoot)
        {
            foreach (var movie in _jellyfin.GetMovies())
            {
                var slug = _slugs.Peek(movie.TmdbId);
                if (slug is null)
                {
                    continue;
                }

                file.Value.Ratings.TryGetValue(slug, out var entry);
                var onWatchlist = file.Value.Watchlist.Contains(slug);
                if ((entry is null || (entry.Rating is null && !entry.Loved)) && !onWatchlist)
                {
                    continue;
                }

                set.Items[movie.ItemId.ToString("N")] = new BadgeDto { Rating = entry?.Rating, Loved = entry?.Loved ?? false, Watchlist = onWatchlist };
            }
        }

        _badgeCache[account.UserId] = (stamp, set);
        return set;
    }

    // ------------------------------------------------------------------ helpers

    public static string LevelName(AuthLevel level) => level switch
    {
        AuthLevel.Session => "session",
        AuthLevel.Public => "public",
        _ => "none",
    };

    private static void Fill(MineDto dto, RatingEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        dto.Rating = entry.Rating;
        dto.Loved = entry.Loved;
        dto.Review = entry.Review;
        dto.Rated = entry.Rating is not null || entry.Loved;
    }

    private static RatingEntry Copy(RatingEntry e) => new()
    {
        Rating = e.Rating,
        Loved = e.Loved,
        Review = e.Review,
        ReviewKnown = e.ReviewKnown,
        UpdatedAt = e.UpdatedAt,
    };

    private sealed record FriendsEntry(DateTimeOffset At, List<FriendActivity> Items);
}
