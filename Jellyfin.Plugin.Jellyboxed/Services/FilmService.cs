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
            dto.Watched = file.Value.Watched.Contains(slug);
            if (file.Value.Diary.TryGetValue(slug, out var diary))
            {
                dto.Watched = true;
                dto.WatchedOn = diary.Last;
                dto.Watches = diary.Count;
                dto.Rewatch = diary.Rewatch || diary.Count > 1;
            }

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
        if (dto.Rating is not null)
        {
            dto.Watched = true; // a rating implies the film was seen
        }

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
            file.Value.NoteViewing(slug, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), input.Rewatch);
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

    /// <summary>Likes or un-likes a film on Letterboxd (the heart), without adding a diary entry.</summary>
    public async Task SetLikedAsync(LetterboxdAccount account, string tmdbId, bool liked, CancellationToken ct)
    {
        if (account.Level != AuthLevel.Session)
        {
            throw new LetterboxdException(LetterboxdErrorKind.NotSignedIn, "Sign in to Letterboxd to like films.");
        }

        var slug = await _slugs.ResolveAsync(tmdbId, ct).ConfigureAwait(false)
            ?? throw new LetterboxdException(LetterboxdErrorKind.NotFound, "This film was not found on Letterboxd.");

        await _pool.Get(account).SetLikeAsync(slug, liked, ct).ConfigureAwait(false);
        _pool.PersistCookies(account.UserId);

        var file = _states.Get(account.UserId);
        lock (file.SyncRoot)
        {
            if (file.Value.Ratings.TryGetValue(slug, out var entry))
            {
                entry.Loved = liked;
                entry.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (liked)
            {
                file.Value.Ratings[slug] = new RatingEntry { Loved = true, UpdatedAt = DateTimeOffset.UtcNow };
            }
        }

        _states.Touch(account.UserId);
    }

    // ------------------------------------------------------------------ poster badges

    /// <summary>Letterboxd's usual slug for a film: the title, plus the year when another film shares the name.</summary>
    internal static IEnumerable<string> GuessSlugs(string name, int? year)
    {
        var sb = new System.Text.StringBuilder();
        var decomposed = name.Normalize(System.Text.NormalizationForm.FormD);
        foreach (var c in decomposed)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark || c == '\'' || c == '\u2019')
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        if (slug.Length == 0)
        {
            yield break;
        }

        if (year is not null)
        {
            yield return slug + "-" + year;
        }

        yield return slug;
    }

    /// <summary>Joins the user's Letterboxd data with the movies in the library. Cached until either side changes.</summary>
    public BadgeSet BuildBadges(LetterboxdAccount account)
    {
        var stamp = $"{_jellyfin.IndexStamp}-{_states.Version(account.UserId)}-{_slugs.Stamp}-{(Config.BadgeShowWatched ? 1 : 0)}{(Config.BadgeShowDate ? 1 : 0)}";
        if (_badgeCache.TryGetValue(account.UserId, out var hit) && hit.Key == stamp)
        {
            return hit.Set;
        }

        var set = new BadgeSet { Etag = $"\"{stamp}\"" };
        var file = _states.Get(account.UserId);
        var libraryCount = 0;
        var verified = 0;
        var guessed = 0;
        int ratingCount;
        int watchlistCount;
        lock (file.SyncRoot)
        {
            ratingCount = file.Value.Ratings.Count;
            watchlistCount = file.Value.Watchlist.Count;
            var showWatched = Config.BadgeShowWatched;
            var showDate = Config.BadgeShowDate;
            foreach (var movie in _jellyfin.GetMovies())
            {
                libraryCount++;

                // Prefer the verified match; when it finds nothing (or there is none yet), guess Letterboxd's slug from
                // title and year so badges still appear.
                var candidates = new List<string>();
                var verifiedSlug = _slugs.Peek(movie.TmdbId);
                if (verifiedSlug is not null)
                {
                    candidates.Add(verifiedSlug);
                }

                var verifiedCount = candidates.Count;
                candidates.AddRange(GuessSlugs(movie.Name, movie.Year));

                RatingEntry? entry = null;
                var onWatchlist = false;
                var watched = false;
                WatchInfo? diary = null;
                var viaGuess = false;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var slug = candidates[i];
                    file.Value.Ratings.TryGetValue(slug, out entry);
                    onWatchlist = file.Value.Watchlist.Contains(slug);
                    file.Value.Diary.TryGetValue(slug, out diary);
                    watched = file.Value.Watched.Contains(slug) || diary is not null || entry?.Rating is not null;
                    var interesting = entry is not null || onWatchlist || (watched && (showWatched || showDate));
                    if (interesting)
                    {
                        viaGuess = i >= verifiedCount;
                        break;
                    }
                }

                var hasRating = entry is not null && (entry.Rating is not null || entry.Loved);
                var showSeen = showWatched && watched;
                var date = showDate ? diary?.Last : null;
                if (!hasRating && !onWatchlist && !showSeen && date is null)
                {
                    continue;
                }

                if (viaGuess)
                {
                    guessed++;
                }
                else
                {
                    verified++;
                }

                set.Items[movie.ItemId.ToString("N")] = new BadgeDto
                {
                    Rating = entry?.Rating,
                    Loved = entry?.Loved ?? false,
                    Watchlist = onWatchlist,
                    Watched = showSeen,
                    WatchedOn = date,
                    Watches = date is null ? 0 : (diary?.Count ?? 0),
                };
            }
        }

        set.Stats = new BadgeStats { Library = libraryCount, Ratings = ratingCount, Watchlist = watchlistCount, Verified = verified, Guessed = guessed };
        _logger.LogInformation(
            "Jellyboxed: poster badges for {Username}: {Library} library films with a TMDB id, {Ratings} rated/liked and {Watchlist} watchlist films synced, {Verified} matched, {Guessed} matched by title",
            account.Username, libraryCount, ratingCount, watchlistCount, verified, guessed);
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
