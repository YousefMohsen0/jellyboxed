using System;
using System.Collections.Generic;
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
        Interlocked.Exchange(ref _solverOnlyUntilTicks, 0);
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
                ["max_timeout"] = Math.Clamp(timeoutSeconds, 15, 120), // Byparr spells it this way (seconds)
            };

            try
            {
                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!text.TrimStart().StartsWith('{'))
                {
                    // Not JSON at all, e.g. "Internal Server Error": the helper itself failed. Show what it said.
                    var snippet = text.Length > 200 ? text[..200] : text;
                    logger.LogWarning(
                        "Jellyboxed: the solver at {Url} answered HTTP {Status} with {Body}. It failed while trying to open Letterboxd; check its own log (for example 'docker logs <container>').",
                        endpoint,
                        (int)response.StatusCode,
                        string.IsNullOrWhiteSpace(snippet) ? "an empty body" : "\"" + snippet.Trim() + "\"");
                    return _current;
                }

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
                logger.LogInformation("Jellyboxed: got a fresh Cloudflare pass from the solver (browser identity: {UserAgent}).", userAgent.Length > 90 ? userAgent[..90] : userAgent);
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

    private static readonly SemaphoreSlim FetchLock = new(1, 1);
    private static long _solverOnlyUntilTicks;

    /// <summary>True while direct requests are known to be refused, so reads should go through the solver straight away.</summary>
    internal static bool SolverOnly => DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _solverOnlyUntilTicks);

    /// <summary>Remembers (for a while) that direct requests do not get through even with a pass.</summary>
    internal static void PreferSolverFor(TimeSpan duration) =>
        Interlocked.Exchange(ref _solverOnlyUntilTicks, DateTimeOffset.UtcNow.Add(duration).UtcTicks);

    internal sealed record Fetched(int Status, string Body, string FinalUrl);

    /// <summary>
    /// Loads a page through the solver's real browser. Used for read-only requests when Cloudflare still refuses
    /// direct requests from this server even with a pass. One request at a time.
    /// </summary>
    internal static async Task<Fetched?> FetchAsync(string flareSolverrUrl, int timeoutSeconds, string url, IEnumerable<(string Name, string Value)> cookies, ILogger logger, CancellationToken ct)
    {
        await FetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var endpoint = flareSolverrUrl.TrimEnd('/');
            if (!endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                endpoint += "/v1";
            }

            var jar = new JsonArray();
            foreach (var (name, value) in cookies)
            {
                jar.Add(new JsonObject { ["name"] = name, ["value"] = value, ["domain"] = ".letterboxd.com", ["path"] = "/" });
            }

            var payload = new JsonObject
            {
                ["cmd"] = "request.get",
                ["url"] = url,
                ["maxTimeout"] = Math.Clamp(timeoutSeconds, 15, 120) * 1000,
                ["max_timeout"] = Math.Clamp(timeoutSeconds, 15, 120),
                ["cookies"] = jar,
            };

            try
            {
                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!text.TrimStart().StartsWith('{'))
                {
                    logger.LogWarning("Jellyboxed: the solver answered HTTP {Status} with a non-JSON body while loading {Url}.", (int)response.StatusCode, url);
                    return null;
                }

                var root = JsonNode.Parse(text);
                var solution = root?["solution"];
                if (root?["status"]?.GetValue<string>() != "ok" || solution is null)
                {
                    logger.LogWarning("Jellyboxed: the solver could not load {Url}: {Message}", url, root?["message"]?.GetValue<string>() ?? "no message");
                    return null;
                }

                var body = solution["response"]?.GetValue<string>() ?? string.Empty;
                var status = solution["status"]?.GetValue<int>() ?? 200;
                if (body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) || body.Contains("cf-chl", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Jellyboxed: the solver was shown a bot check while loading {Url} and could not pass it.", url);
                    return null;
                }

                return new Fetched(status, body, solution["url"]?.GetValue<string>() ?? url);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                logger.LogWarning("Jellyboxed: could not load {Url} through the solver: {Message}", url, ex.Message);
                return null;
            }
        }
        finally
        {
            FetchLock.Release();
        }
    }
}
