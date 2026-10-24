using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellyboxed.Configuration;
using Jellyfin.Plugin.Jellyboxed.Data;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Services;

/// <summary>
/// Notices (on the server, so it works from any client and survives a closed browser tab) when someone finishes
/// watching a movie and queues a "log it on Letterboxd?" prompt for the web UI to pick up.
/// </summary>
public sealed class PlaybackWatcher : IHostedService
{
    private static readonly TimeSpan PromptLifetime = TimeSpan.FromHours(6);

    private readonly ISessionManager _sessions;
    private readonly AccountStore _accounts;
    private readonly AccountStateStore _states;
    private readonly ILogger<PlaybackWatcher> _logger;
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), PendingPrompt> _pending = new();

    public PlaybackWatcher(ISessionManager sessions, AccountStore accounts, AccountStateStore states, ILogger<PlaybackWatcher> logger)
    {
        _sessions = sessions;
        _accounts = accounts;
        _states = states;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <summary>Prompts waiting for this user (expired and snoozed ones are dropped).</summary>
    public IReadOnlyList<PendingPrompt> List(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<PendingPrompt>();
        foreach (var (key, prompt) in _pending.Where(kv => kv.Key.UserId == userId).ToList())
        {
            if (now - prompt.CreatedAt > PromptLifetime)
            {
                _pending.TryRemove(key, out _);
                continue;
            }

            result.Add(prompt);
        }

        return result.OrderByDescending(p => p.CreatedAt).ToList();
    }

    /// <summary>Removes a prompt and snoozes it so the same film is not offered again for a while.</summary>
    public void Dismiss(Guid userId, Guid itemId, TimeSpan snooze)
    {
        _pending.TryRemove((userId, itemId), out _);
        var file = _states.Get(userId);
        lock (file.SyncRoot)
        {
            file.Value.PromptSnoozedUntil[itemId.ToString("N")] = DateTimeOffset.UtcNow + snooze;
            PruneSnoozes(file.Value);
        }

        file.MarkDirty();
    }

    private static void PruneSnoozes(AccountState state)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in state.PromptSnoozedUntil.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
        {
            state.PromptSnoozedUntil.Remove(key);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var cfg = Config;
            if (!cfg.EnablePlaybackPrompt || !cfg.EnableLogging || e.Item is not Movie movie)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(movie.GetProviderId(MetadataProvider.Tmdb)))
            {
                return;
            }

            var runtime = movie.RunTimeTicks ?? 0;
            var watched = e.PlaybackPositionTicks ?? 0;
            var percent = runtime > 0 ? (double)watched / runtime * 100 : (e.PlayedToCompletion ? 100 : 0);
            if (!e.PlayedToCompletion && percent < Math.Clamp(cfg.PromptMinWatchedPercent, 1, 100))
            {
                return;
            }

            foreach (var user in e.Users)
            {
                var account = _accounts.Get(user.Id);
                if (account is null || account.Level != AuthLevel.Session || !account.PromptAfterPlayback)
                {
                    continue;
                }

                var file = _states.Get(user.Id);
                lock (file.SyncRoot)
                {
                    if (file.Value.PromptSnoozedUntil.TryGetValue(movie.Id.ToString("N"), out var until) && until > DateTimeOffset.UtcNow)
                    {
                        continue;
                    }
                }

                _pending[(user.Id, movie.Id)] = new PendingPrompt
                {
                    ItemId = movie.Id,
                    Name = movie.Name,
                    Year = movie.ProductionYear,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyboxed: could not process a playback-stopped event");
        }
    }
}
