using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellyboxed.Configuration;
using Jellyfin.Plugin.Jellyboxed.Data;
using Jellyfin.Plugin.Jellyboxed.Letterboxd;
using Jellyfin.Plugin.Jellyboxed.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Api;

/// <summary>
/// The plugin's HTTP API, used by the injected script and by the settings page.
/// All JSON is camelCase and produced here, so the browser contract never depends on Jellyfin's serializer settings.
/// Errors are never returned as 401/403 (the Jellyfin web client treats those as "you were signed out").
/// </summary>
[ApiController]
[Route("Jellyboxed")]
public class JellyboxedController : ControllerBase
{
    private const string AdminPolicy = "RequiresElevation";

    private static readonly JsonSerializerOptions Out = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
    private static readonly JsonSerializerOptions In = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<Guid, (DateTimeOffset Window, int Count)> ConnectAttempts = new();

    private readonly AccountStore _accounts;
    private readonly AccountStateStore _states;
    private readonly ClientPool _pool;
    private readonly FilmService _films;
    private readonly SyncService _sync;
    private readonly SlugResolver _slugs;
    private readonly JellyfinBridge _jellyfin;
    private readonly PlaybackWatcher _playback;
    private readonly RequestGate _gate;
    private readonly ILogger<JellyboxedController> _logger;

    public JellyboxedController(
        AccountStore accounts,
        AccountStateStore states,
        ClientPool pool,
        FilmService films,
        SyncService sync,
        SlugResolver slugs,
        JellyfinBridge jellyfin,
        PlaybackWatcher playback,
        RequestGate gate,
        ILogger<JellyboxedController> logger)
    {
        _accounts = accounts;
        _states = states;
        _pool = pool;
        _films = films;
        _sync = sync;
        _slugs = slugs;
        _jellyfin = jellyfin;
        _playback = playback;
        _gate = gate;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;

    // ====================================================================== static assets (anonymous)

    [HttpGet("client.js")]
    [AllowAnonymous]
    public IActionResult ClientScript() => Resource("client.js", "application/javascript; charset=utf-8");

    [HttpGet("client.css")]
    [AllowAnonymous]
    public IActionResult ClientStyles() => Resource("client.css", "text/css; charset=utf-8");

    private IActionResult Resource(string name, string contentType)
    {
        var assembly = typeof(Plugin).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Web.{name}");
        if (stream is null)
        {
            return NotFound();
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var etag = $"\"{Plugin.Instance?.VersionString}-{ms.Length}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "no-cache";
        if (Request.Headers.IfNoneMatch.ToString() == etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(ms.ToArray(), contentType);
    }

    // ---- image proxy: avatars come through the Jellyfin origin (no third-party requests from browsers, no CSP surprises)

    private static readonly HttpClient ImageHttp = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly ConcurrentDictionary<string, (byte[] Bytes, string Type, DateTimeOffset At)> ImageCache = new();

    /// <summary>Returns true for hosts we are willing to fetch images from.</summary>
    internal static bool IsAllowedImageUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = parsed.Host.ToLowerInvariant();
        var ok = host == "ltrbxd.com" || host.EndsWith(".ltrbxd.com", StringComparison.Ordinal)
            || host == "letterboxd.com" || host.EndsWith(".letterboxd.com", StringComparison.Ordinal);
        if (ok)
        {
            uri = parsed;
        }

        return ok;
    }

    [HttpGet("img")]
    [AllowAnonymous]
    public async Task<IActionResult> Image([FromQuery] string u, CancellationToken ct)
    {
        if (!IsAllowedImageUrl(u, out var uri) || uri is null)
        {
            return BadRequest();
        }

        Response.Headers.CacheControl = "public, max-age=604800";
        if (ImageCache.TryGetValue(uri.AbsoluteUri, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromDays(7))
        {
            return File(hit.Bytes, hit.Type);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; Jellyboxed)");
            using var response = await ImageHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!response.IsSuccessStatusCode || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > 2_000_000)
            {
                return NotFound();
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 2_000_000)
            {
                return NotFound();
            }

            if (ImageCache.Count > 300)
            {
                ImageCache.Clear();
            }

            ImageCache[uri.AbsoluteUri] = (bytes, type, DateTimeOffset.UtcNow);
            return File(bytes, type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return NotFound();
        }
    }

    // ====================================================================== current user

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var userId = CurrentUserId;
        if (userId == Guid.Empty)
        {
            return Fail(StatusCodes.Status400BadRequest, "nouser", "This request is not tied to a Jellyfin user.");
        }

        var cfg = Config;
        var account = _accounts.Get(userId);
        return Ok(new
        {
            version = Plugin.Instance?.VersionString,
            features = new
            {
                friends = cfg.EnableFriendsFeed,
                friendReviews = cfg.EnableFriendReviews,
                myRating = cfg.EnableMyRating,
                logging = cfg.EnableLogging,
                watchlist = cfg.EnableWatchlist,
                badges = cfg.EnablePosterBadges,
                prompt = cfg.EnablePlaybackPrompt && cfg.EnableLogging,
                passwordLogin = cfg.AllowPasswordLogin,
                cookieLogin = cfg.AllowCookieLogin,
                selfConnect = cfg.AllowUserSelfConnect,
                watchlistToFavorites = cfg.AllowWatchlistToFavorites,
            },
            ui = new
            {
                accent = cfg.AccentColor,
                badgePosition = cfg.BadgePosition,
                badgeRating = cfg.BadgeShowRating,
                badgeLoved = cfg.BadgeShowLoved,
                badgeWatchlist = cfg.BadgeShowWatchlist,
                detailSelector = cfg.DetailPageSelector,
                cardSelector = cfg.CardSelector,
                debug = cfg.DebugLogging,
            },
            account = AccountView(account),
        });
    }

    [HttpPut("me/settings")]
    [Authorize]
    public async Task<IActionResult> SaveSettings()
    {
        var userId = CurrentUserId;
        var body = await ReadBodyAsync<SettingsInput>().ConfigureAwait(false);
        if (body is null || _accounts.Get(userId) is null)
        {
            return Fail(StatusCodes.Status400BadRequest, "noaccount", "Connect a Letterboxd account first.");
        }

        var updated = _accounts.Update(userId, a =>
        {
            if (body.WatchlistToFavorites is { } w)
            {
                a.WatchlistToFavorites = w && Config.AllowWatchlistToFavorites;
            }

            if (body.LovedToFavorite is { } l)
            {
                a.LovedToFavorite = l;
            }

            if (body.MarkPlayedOnLog is { } m)
            {
                a.MarkPlayedOnLog = m;
            }

            if (body.PromptAfterPlayback is { } p)
            {
                a.PromptAfterPlayback = p;
            }
        });

        if (body.WatchlistToFavorites == true)
        {
            _sync.Enqueue(userId, SyncKind.Resolve); // applies the mirror right away
        }

        return Ok(AccountView(updated));
    }

    [HttpPost("me/connect")]
    [Authorize]
    public async Task<IActionResult> Connect(CancellationToken ct)
    {
        var userId = CurrentUserId;
        var cfg = Config;
        if (!cfg.AllowUserSelfConnect)
        {
            return Fail(StatusCodes.Status400BadRequest, "disabled", "Your administrator manages Letterboxd accounts on this server.");
        }

        if (TooManyAttempts(userId))
        {
            return Fail(StatusCodes.Status429TooManyRequests, "toomany", "Too many attempts. Wait a minute and try again.");
        }

        var body = await ReadBodyAsync<ConnectInput>().ConfigureAwait(false);
        if (body is null)
        {
            return Fail(StatusCodes.Status400BadRequest, "badrequest", "Missing request body.");
        }

        return await ConnectAccountAsync(userId, body, ct).ConfigureAwait(false);
    }

    [HttpPost("me/disconnect")]
    [Authorize]
    public IActionResult Disconnect()
    {
        DisconnectAccount(CurrentUserId);
        return Ok(new { ok = true });
    }

    [HttpGet("me/sync")]
    [Authorize]
    public IActionResult SyncState() => Ok(_sync.GetStatus(CurrentUserId));

    [HttpPost("me/sync")]
    [Authorize]
    public IActionResult SyncNow()
    {
        var userId = CurrentUserId;
        if (_accounts.Get(userId) is not { Level: not AuthLevel.None })
        {
            return Fail(StatusCodes.Status400BadRequest, "noaccount", "Connect a Letterboxd account first.");
        }

        _sync.Enqueue(userId, full: true);
        return Ok(_sync.GetStatus(userId));
    }

    // ====================================================================== films

    [HttpGet("item/{itemId:guid}")]
    [Authorize]
    public async Task<IActionResult> ItemInfo(Guid itemId, CancellationToken ct)
    {
        var userId = CurrentUserId;
        var movie = _jellyfin.GetVisibleMovie(itemId, userId);
        if (movie is null)
        {
            return Ok(new { isMovie = false });
        }

        var (item, tmdb) = movie.Value;
        var account = _accounts.Get(userId);
        if (tmdb is null || account is null || account.Level == AuthLevel.None)
        {
            return Ok(new { isMovie = true, hasTmdb = tmdb is not null, name = item.Name, mine = (MineDto?)null });
        }

        try
        {
            var mine = await _films.GetMineAsync(account, tmdb, ct).ConfigureAwait(false);
            return Ok(new { isMovie = true, hasTmdb = true, name = item.Name, year = item.ProductionYear, mine });
        }
        catch (LetterboxdException ex)
        {
            return LetterboxdFail(ex);
        }
    }

    [HttpGet("item/{itemId:guid}/friends")]
    [Authorize]
    public async Task<IActionResult> Friends(Guid itemId, CancellationToken ct)
    {
        if (!Config.EnableFriendsFeed)
        {
            return Ok(new { friends = Array.Empty<object>() });
        }

        var (account, tmdb, failure) = Resolve(itemId);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            var friends = await _films.GetFriendsAsync(account!, tmdb!, ct).ConfigureAwait(false);
            var includeReviews = Config.EnableFriendReviews;
            return Ok(new
            {
                friends = friends.Select(f => new
                {
                    name = f.Name,
                    avatar = IsAllowedImageUrl(f.Avatar, out _) ? $"img?u={Uri.EscapeDataString(f.Avatar)}" : string.Empty,
                    rating = f.Rating,
                    ratingValue = f.RatingValue,
                    review = includeReviews ? f.Review : string.Empty,
                    url = ProfileUrl(f.ProfileUrl),
                }),
            });
        }
        catch (LetterboxdException ex)
        {
            return LetterboxdFail(ex);
        }
    }

    [HttpPost("item/{itemId:guid}/log")]
    [Authorize]
    public async Task<IActionResult> Log(Guid itemId, CancellationToken ct)
    {
        if (!Config.EnableLogging)
        {
            return Fail(StatusCodes.Status400BadRequest, "disabled", "Logging is turned off by the administrator.");
        }

        var (account, tmdb, failure) = Resolve(itemId);
        if (failure is not null)
        {
            return failure;
        }

        var input = await ReadBodyAsync<LogInput>().ConfigureAwait(false);
        if (input is null)
        {
            return Fail(StatusCodes.Status400BadRequest, "badrequest", "Missing request body.");
        }

        try
        {
            var mine = await _films.LogAsync(account!, tmdb!, itemId, input, ct).ConfigureAwait(false);
            _playback.Dismiss(CurrentUserId, itemId, TimeSpan.FromHours(Math.Max(1, Config.PromptCooldownHours)));
            return Ok(new { mine });
        }
        catch (LetterboxdException ex)
        {
            return LetterboxdFail(ex);
        }
    }

    [HttpPost("item/{itemId:guid}/watchlist")]
    [Authorize]
    public async Task<IActionResult> Watchlist(Guid itemId, CancellationToken ct)
    {
        if (!Config.EnableWatchlist)
        {
            return Fail(StatusCodes.Status400BadRequest, "disabled", "The watchlist is turned off by the administrator.");
        }

        var (account, tmdb, failure) = Resolve(itemId);
        if (failure is not null)
        {
            return failure;
        }

        var input = await ReadBodyAsync<WatchlistInput>().ConfigureAwait(false);
        try
        {
            var mine = await _films.SetWatchlistAsync(account!, tmdb!, itemId, input?.Add ?? true, ct).ConfigureAwait(false);
            return Ok(new { mine });
        }
        catch (LetterboxdException ex)
        {
            return LetterboxdFail(ex);
        }
    }

    // ====================================================================== library badges & prompts

    [HttpGet("badges")]
    [Authorize]
    public IActionResult Badges()
    {
        var account = _accounts.Get(CurrentUserId);
        if (!Config.EnablePosterBadges || account is null || account.Level == AuthLevel.None)
        {
            return Ok(new { items = new Dictionary<string, BadgeDto>() });
        }

        var set = _films.BuildBadges(account);
        Response.Headers.ETag = set.Etag;
        Response.Headers.CacheControl = "private, no-cache";
        if (Request.Headers.IfNoneMatch.ToString() == set.Etag)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Json(new { items = set.Items });
    }

    [HttpGet("pending")]
    [Authorize]
    public IActionResult Pending()
    {
        var account = _accounts.Get(CurrentUserId);
        if (!Config.EnablePlaybackPrompt || account is null || account.Level != AuthLevel.Session || !account.PromptAfterPlayback)
        {
            return Ok(new { prompts = Array.Empty<object>() });
        }

        return Ok(new
        {
            prompts = _playback.List(CurrentUserId).Select(p => new { itemId = p.ItemId.ToString("N"), name = p.Name, year = p.Year }),
        });
    }

    [HttpPost("pending/{itemId:guid}/dismiss")]
    [Authorize]
    public async Task<IActionResult> DismissPrompt(Guid itemId)
    {
        var input = await ReadBodyAsync<DismissInput>().ConfigureAwait(false);
        var snooze = input?.Forever == true ? TimeSpan.FromDays(3650) : TimeSpan.FromHours(Math.Max(1, Config.PromptCooldownHours));
        _playback.Dismiss(CurrentUserId, itemId, snooze);
        return Ok(new { ok = true });
    }

    // ====================================================================== administration

    [HttpGet("admin/accounts")]
    [Authorize(Policy = AdminPolicy)]
    public IActionResult AdminAccounts()
    {
        var byUser = _accounts.All().ToDictionary(a => a.UserId);
        var rows = _jellyfin.GetAllUsers().OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Select(u =>
        {
            byUser.TryGetValue(u.Id, out var account);
            return new { userId = u.Id.ToString("N"), userName = u.Name, account = AccountView(account) };
        });
        return Ok(new { users = rows });
    }

    [HttpPut("admin/accounts/{userId:guid}")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<IActionResult> AdminConnect(Guid userId, CancellationToken ct)
    {
        if (_jellyfin.GetUser(userId) is null)
        {
            return Fail(StatusCodes.Status404NotFound, "nouser", "No such Jellyfin user.");
        }

        var body = await ReadBodyAsync<ConnectInput>().ConfigureAwait(false);
        if (body is null)
        {
            return Fail(StatusCodes.Status400BadRequest, "badrequest", "Missing request body.");
        }

        return await ConnectAccountAsync(userId, body, ct, adminOverride: true).ConfigureAwait(false);
    }

    [HttpDelete("admin/accounts/{userId:guid}")]
    [Authorize(Policy = AdminPolicy)]
    public IActionResult AdminDisconnect(Guid userId)
    {
        DisconnectAccount(userId);
        return Ok(new { ok = true });
    }

    [HttpPost("admin/accounts/{userId:guid}/sync")]
    [Authorize(Policy = AdminPolicy)]
    public IActionResult AdminSync(Guid userId)
    {
        _sync.Enqueue(userId, full: true);
        return Ok(_sync.GetStatus(userId));
    }

    [HttpGet("admin/status")]
    [Authorize(Policy = AdminPolicy)]
    public IActionResult AdminStatus()
    {
        var (blocked, reason, until) = _gate.Status;
        return Ok(new
        {
            version = Plugin.Instance?.VersionString,
            blocked,
            reason,
            until = blocked ? until : (DateTimeOffset?)null,
            knownFilms = _slugs.Count,
            libraryMovies = _jellyfin.GetMovies().Count,
        });
    }

    [HttpPost("admin/resume")]
    [Authorize(Policy = AdminPolicy)]
    public IActionResult AdminResume()
    {
        _gate.Reset();
        return Ok(new { ok = true });
    }

    [HttpPost("admin/clear-cache")]
    [Authorize(Policy = AdminPolicy)]
    public IActionResult AdminClearCache()
    {
        _films.ClearFriendsCache();
        _slugs.Clear();
        _jellyfin.InvalidateIndex();
        foreach (var account in _accounts.All())
        {
            _states.Delete(account.UserId);
            if (account.Level != AuthLevel.None)
            {
                _sync.Enqueue(account.UserId, full: true);
            }
        }

        return Ok(new { ok = true });
    }

    // ====================================================================== shared helpers

    private async Task<IActionResult> ConnectAccountAsync(Guid userId, ConnectInput body, CancellationToken ct, bool adminOverride = false)
    {
        var cfg = Config;
        var username = (body.Username ?? string.Empty).Trim();
        var password = body.Password ?? string.Empty;
        var cookies = (body.Cookies ?? string.Empty).Trim();

        if (!adminOverride)
        {
            if (password.Length > 0 && !cfg.AllowPasswordLogin)
            {
                return Fail(StatusCodes.Status400BadRequest, "disabled", "Password sign-in is turned off by the administrator.");
            }

            if (cookies.Length > 0 && !cfg.AllowCookieLogin)
            {
                return Fail(StatusCodes.Status400BadRequest, "disabled", "Cookie sign-in is turned off by the administrator.");
            }
        }

        if (username.Length == 0 && cookies.Length == 0)
        {
            return Fail(StatusCodes.Status400BadRequest, "badrequest", "Enter your Letterboxd username.");
        }

        if (username.Length > 100 || password.Length > 200 || cookies.Length > 20_000 || username.Any(char.IsWhiteSpace))
        {
            return Fail(StatusCodes.Status400BadRequest, "badrequest", "That does not look like a valid Letterboxd username.");
        }

        using var probe = new LetterboxdClient(username, cookies, _gate, _logger);
        string mode;
        string finalUsername = username;
        var sessionValid = false;
        try
        {
            if (password.Length > 0)
            {
                await probe.LoginAsync(username, password, ct).ConfigureAwait(false);
                mode = "login";
                sessionValid = true;
                finalUsername = probe.SignedInAs ?? username;
            }
            else if (cookies.Length > 0)
            {
                if (!await probe.IsSignedInAsync(ct).ConfigureAwait(false))
                {
                    return Fail(StatusCodes.Status422UnprocessableEntity, "badcookies", "Those cookies are not a signed-in Letterboxd session. Copy them again from a logged-in browser tab.");
                }

                mode = "cookies";
                sessionValid = true;
                finalUsername = probe.SignedInAs ?? username;
                if (finalUsername.Length == 0)
                {
                    return Fail(StatusCodes.Status422UnprocessableEntity, "badrequest", "Enter your Letterboxd username as well.");
                }
            }
            else
            {
                // Username only: read-only mode based on the public profile.
                var probeClient = new LetterboxdClient(username, string.Empty, _gate, _logger);
                using (probeClient)
                {
                    await probeClient.GetRssAsync(ct).ConfigureAwait(false);
                }

                mode = "none";
            }
        }
        catch (LetterboxdException ex)
        {
            return LetterboxdFail(ex);
        }

        var cookieString = sessionValid ? probe.ExportCookies() : string.Empty;
        var updated = _accounts.Update(userId, a =>
        {
            a.Username = finalUsername;
            a.Cookies = cookieString;
            a.AuthMode = mode;
            a.SessionValid = sessionValid;
            a.ConnectedAt = DateTimeOffset.UtcNow;
            a.LastError = null;
        });

        _pool.Forget(userId);
        _states.Delete(userId);
        _sync.Enqueue(userId, full: true);
        _logger.LogInformation("Jellyboxed: Jellyfin user {User} connected Letterboxd account {Username} ({Mode})", userId, finalUsername, mode);
        return Ok(AccountView(updated));
    }

    private void DisconnectAccount(Guid userId)
    {
        _accounts.Remove(userId);
        _pool.Forget(userId);
        _states.Delete(userId);
    }

    private bool TooManyAttempts(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = ConnectAttempts.AddOrUpdate(
            userId,
            _ => (now, 1),
            (_, old) => now - old.Window > TimeSpan.FromMinutes(1) ? (now, 1) : (old.Window, old.Count + 1));
        return entry.Count > 6;
    }

    private (LetterboxdAccount? Account, string? Tmdb, IActionResult? Failure) Resolve(Guid itemId)
    {
        var userId = CurrentUserId;
        var movie = _jellyfin.GetVisibleMovie(itemId, userId);
        if (movie is null || movie.Value.TmdbId is null)
        {
            return (null, null, Fail(StatusCodes.Status404NotFound, "notmovie", "This item is not a movie with a TMDB id."));
        }

        var account = _accounts.Get(userId);
        if (account is null || account.Level == AuthLevel.None)
        {
            return (null, null, Fail(StatusCodes.Status422UnprocessableEntity, "noaccount", "Connect your Letterboxd account first."));
        }

        return (account, movie.Value.TmdbId, null);
    }

    private object? AccountView(LetterboxdAccount? account)
    {
        if (account is null)
        {
            return new { level = "none", connected = false };
        }

        var file = _states.Get(account.UserId);
        DateTimeOffset? lastFull;
        int rated;
        int liked;
        int watchlist;
        lock (file.SyncRoot)
        {
            lastFull = file.Value.FullSyncAt;
            rated = file.Value.LastSyncRated;
            liked = file.Value.LastSyncLiked;
            watchlist = file.Value.LastSyncWatchlist;
        }

        return new
        {
            connected = account.Level != AuthLevel.None,
            level = FilmService.LevelName(account.Level),
            username = account.Username,
            authMode = account.AuthMode,
            sessionValid = account.SessionValid,
            lastError = account.LastError,
            connectedAt = account.ConnectedAt,
            settings = new
            {
                watchlistToFavorites = account.WatchlistToFavorites,
                lovedToFavorite = account.LovedToFavorite,
                markPlayedOnLog = account.MarkPlayedOnLog,
                promptAfterPlayback = account.PromptAfterPlayback,
            },
            sync = _sync.GetStatus(account.UserId),
            lastFullSync = lastFull,
            counts = new { rated, liked, watchlist },
        };
    }

    private static string ProfileUrl(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return string.Empty;
        }

        var absolute = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : "https://letterboxd.com" + (href.StartsWith('/') ? href : "/" + href);
        return Uri.TryCreate(absolute, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && (u.Host == "letterboxd.com" || u.Host.EndsWith(".letterboxd.com", StringComparison.Ordinal))
            ? u.AbsoluteUri
            : string.Empty;
    }

    private async Task<T?> ReadBodyAsync<T>()
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(Request.Body, In).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ContentResult Json(object value, int status = 200) =>
        new() { Content = JsonSerializer.Serialize(value, Out), ContentType = "application/json; charset=utf-8", StatusCode = status };

    private new IActionResult Ok(object? value) => Json(value ?? new { });

    private IActionResult Fail(int status, string error, string message) => Json(new { error, message }, status);

    private IActionResult LetterboxdFail(LetterboxdException ex)
    {
        var (status, code) = ex.Kind switch
        {
            LetterboxdErrorKind.NotFound => (StatusCodes.Status404NotFound, "notfound"),
            LetterboxdErrorKind.NotSignedIn => (StatusCodes.Status422UnprocessableEntity, "notsignedin"),
            LetterboxdErrorKind.Blocked => (StatusCodes.Status503ServiceUnavailable, "blocked"),
            LetterboxdErrorKind.Captcha => (StatusCodes.Status422UnprocessableEntity, "captcha"),
            LetterboxdErrorKind.BadCredentials => (StatusCodes.Status422UnprocessableEntity, "badcredentials"),
            LetterboxdErrorKind.ProfileUnavailable => (StatusCodes.Status422UnprocessableEntity, "profile"),
            _ => (StatusCodes.Status502BadGateway, "failed"),
        };

        if (ex.Kind == LetterboxdErrorKind.NotSignedIn)
        {
            var userId = CurrentUserId;
            if (userId != Guid.Empty)
            {
                _accounts.Update(userId, a =>
                {
                    if (a.Level == AuthLevel.Session)
                    {
                        a.SessionValid = false;
                        a.LastError = ex.Message;
                    }
                });
            }
        }

        return Fail(status, code, ex.Message);
    }

    // ---- request bodies

    public sealed class ConnectInput
    {
        public string? Username { get; set; }

        public string? Password { get; set; }

        public string? Cookies { get; set; }
    }

    public sealed class SettingsInput
    {
        public bool? WatchlistToFavorites { get; set; }

        public bool? LovedToFavorite { get; set; }

        public bool? MarkPlayedOnLog { get; set; }

        public bool? PromptAfterPlayback { get; set; }
    }

    public sealed class WatchlistInput
    {
        public bool Add { get; set; } = true;
    }

    public sealed class DismissInput
    {
        public bool Forever { get; set; }
    }
}
