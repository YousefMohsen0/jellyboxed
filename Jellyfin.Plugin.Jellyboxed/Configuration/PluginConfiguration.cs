using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellyboxed.Configuration;

/// <summary>
/// Server-wide settings, edited by an administrator on the plugin's settings page.
/// Per-user Letterboxd accounts and their secrets are NOT stored here; see <c>AccountStore</c>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---- Feature switches (an admin can turn any feature off for everybody) ----

    /// <summary>Gets or sets a value indicating whether the plugin automatically adds its script to the Jellyfin web UI.</summary>
    public bool InjectWebUi { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Activity from friends" row is shown on movie pages.</summary>
    public bool EnableFriendsFeed { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether friends' review text can be opened.</summary>
    public bool EnableFriendReviews { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the user's own rating row is shown on movie pages.</summary>
    public bool EnableMyRating { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether users can log / rate / review films on Letterboxd.</summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the watchlist button is shown.</summary>
    public bool EnableWatchlist { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether rating badges are drawn on library posters.</summary>
    public bool EnablePosterBadges { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether users are offered to log a film after they watch it.</summary>
    public bool EnablePlaybackPrompt { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether users may sign in with a username and password.</summary>
    public bool AllowPasswordLogin { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether users may paste browser cookies as a fallback sign-in.</summary>
    public bool AllowCookieLogin { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether non-admin users may connect their own Letterboxd account.</summary>
    public bool AllowUserSelfConnect { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether background syncing is enabled.</summary>
    public bool EnableBackgroundSync { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether users may mirror their Letterboxd watchlist onto Jellyfin favourites.</summary>
    public bool AllowWatchlistToFavorites { get; set; } = true;

    // ---- Behaviour ----

    /// <summary>Gets or sets the percentage of a film that must be watched before the "log it?" prompt appears.</summary>
    public int PromptMinWatchedPercent { get; set; } = 80;

    /// <summary>Gets or sets how long (hours) the plugin waits before asking about the same film again.</summary>
    public int PromptCooldownHours { get; set; } = 24;

    /// <summary>Gets or sets the maximum number of friends shown per film.</summary>
    public int MaxFriendsShown { get; set; } = 40;

    /// <summary>Gets or sets where the rating badge is drawn on a poster: top/middle/bottom combined with left/center/right, e.g. "bottom-center".</summary>
    public string BadgePosition { get; set; } = "top-left";

    /// <summary>Gets or sets a value indicating whether the star rating is shown on badges.</summary>
    public bool BadgeShowRating { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "loved" heart is shown on badges.</summary>
    public bool BadgeShowLoved { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether films on the watchlist get a small marker on badges.</summary>
    public bool BadgeShowWatchlist { get; set; } = true;

    /// <summary>Gets or sets the accent colour used by the plugin's UI.</summary>
    public string AccentColor { get; set; } = "#00e054";

    // ---- Caching & sync ----

    /// <summary>Gets or sets how long (minutes) a user's own rating is trusted before it is re-checked.</summary>
    public int RatingCacheMinutes { get; set; } = 1440;

    /// <summary>Gets or sets how long (minutes) a film's friends feed is cached.</summary>
    public int FriendsCacheMinutes { get; set; } = 60;

    /// <summary>Gets or sets the hours between full background syncs.</summary>
    public int FullSyncIntervalHours { get; set; } = 6;

    /// <summary>Gets or sets the minutes between quick "what's new" checks (0 disables).</summary>
    public int QuickSyncIntervalMinutes { get; set; } = 10;

    /// <summary>Gets or sets the minimum delay (ms) between requests to Letterboxd.</summary>
    public int RequestDelayMs { get; set; } = 300;

    /// <summary>Gets or sets the maximum number of simultaneous requests to Letterboxd.</summary>
    public int MaxConcurrentRequests { get; set; } = 3;

    /// <summary>Gets or sets the timeout (seconds) for a single Letterboxd request.</summary>
    public int RequestTimeoutSeconds { get; set; } = 20;

    // ---- Advanced ----

    /// <summary>Gets or sets a custom User-Agent; empty uses a built-in browser-like value.</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>Gets or sets a CSS selector (tried first) for where the film panel is inserted on a details page.</summary>
    public string DetailPageSelector { get; set; } = string.Empty;

    /// <summary>Gets or sets a CSS selector (tried first) that matches library poster cards.</summary>
    public string CardSelector { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether verbose logging and browser-console output is enabled.</summary>
    public bool DebugLogging { get; set; }

    /// <summary>
    /// Gets or sets the address of a FlareSolverr instance (for example http://192.168.1.10:8191). When Letterboxd shows a
    /// Cloudflare bot check to this server, a fresh pass is requested from it automatically. Leave empty to disable.
    /// </summary>
    public string FlareSolverrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets how long (seconds) FlareSolverr may take to solve a bot check.</summary>
    public int FlareSolverrTimeoutSeconds { get; set; } = 60;
}
