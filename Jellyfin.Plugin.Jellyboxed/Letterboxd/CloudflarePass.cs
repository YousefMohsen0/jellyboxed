using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Letterboxd;

/// <summary>
/// Gets past Letterboxd's Cloudflare bot check by asking a FlareSolverr instance (a small helper that drives a real
/// browser) for a fresh <c>cf_clearance</c> cookie and the browser identity it belongs to. The pass is shared by all
/// accounts because Cloudflare ties it to this server's address, not to a user. It is optional: without a configured
/// FlareSolverr URL nothing here is used.
/// </summary>
internal static class CloudflarePass
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(150) };
    private static volatile Pass? _current;
    private static DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    internal sealed record Pass(string Clearance, string UserAgent, DateTimeOffset ObtainedAt);

    /// <summary>Gets the pass obtained so far (null when none).</summary>
    internal static Pass? Current => _current;

    /// <summary>Forgets the pass (used when settings change or "clear caches" is pressed).</summary>
    internal static void Reset()
    {
        _current = null;
        _lastAttempt = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Asks FlareSolverr for a new pass. Only one request runs at a time and attempts are spaced at least 30 seconds
    /// apart; callers that arrive in between get whatever pass was just obtained.
    /// </summary>
    internal static async Task<Pass?> SolveAsync(string flareSolverrUrl, int timeoutSeconds, Pass? stale, ILogger logger, CancellationToken ct)
    {
        await Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Somebody else already refreshed it while we waited.
            if (_current is { } now && !ReferenceEquals(now, stale))
            {
                return now;
            }

            if (DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromSeconds(30))
            {
                return _current;
            }

            _lastAttempt = DateTimeOffset.UtcNow;
            var endpoint = flareSolverrUrl.TrimEnd('/');
            if (!endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                endpoint += "/v1";
            }

            var payload = new JsonObject
            {
                ["cmd"] = "request.get",
                ["url"] = "https://letterboxd.com/",
                ["maxTimeout"] = Math.Clamp(timeoutSeconds, 15, 120) * 1000,
            };

            try
            {
                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var root = JsonNode.Parse(text);
                if (root?["status"]?.GetValue<string>() != "ok")
                {
                    logger.LogWarning("Jellyboxed: FlareSolverr could not solve the bot check: {Message}", root?["message"]?.GetValue<string>() ?? ("HTTP " + (int)response.StatusCode));
                    return _current;
                }

                var solution = root["solution"];
                var clearance = solution?["cookies"]?.AsArray()
                    .Select(c => (Name: c?["name"]?.GetValue<string>(), Value: c?["value"]?.GetValue<string>()))
                    .FirstOrDefault(c => c.Name == "cf_clearance").Value;
                var userAgent = solution?["userAgent"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(clearance) || string.IsNullOrWhiteSpace(userAgent))
                {
                    logger.LogWarning("Jellyboxed: FlareSolverr answered but returned no cf_clearance cookie (Letterboxd may not be showing a challenge to it).");
                    return _current;
                }

                _current = new Pass(clearance, userAgent, DateTimeOffset.UtcNow);
                logger.LogInformation("Jellyboxed: got a fresh Cloudflare pass from FlareSolverr.");
                return _current;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                logger.LogWarning("Jellyboxed: could not reach FlareSolverr at {Url}: {Message}", endpoint, ex.Message);
                return _current;
            }
        }
        finally
        {
            Lock.Release();
        }
    }
}
