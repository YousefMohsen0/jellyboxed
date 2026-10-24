using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellyboxed.Configuration;

namespace Jellyfin.Plugin.Jellyboxed.Letterboxd;

/// <summary>Why a Letterboxd operation failed, in terms the UI can act on.</summary>
public enum LetterboxdErrorKind
{
    /// <summary>Something unexpected went wrong.</summary>
    Failed,

    /// <summary>The film or page does not exist on Letterboxd.</summary>
    NotFound,

    /// <summary>The operation needs a signed-in session and there is none (or it expired).</summary>
    NotSignedIn,

    /// <summary>Letterboxd is rate-limiting us or showing a bot check; we are backing off.</summary>
    Blocked,

    /// <summary>Letterboxd wants a captcha / extra verification during sign-in.</summary>
    Captcha,

    /// <summary>The credentials were rejected.</summary>
    BadCredentials,

    /// <summary>The user's profile is private or missing.</summary>
    ProfileUnavailable,
}

/// <summary>An error talking to Letterboxd with a machine-readable kind.</summary>
public sealed class LetterboxdException : Exception
{
    public LetterboxdException(LetterboxdErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public LetterboxdErrorKind Kind { get; }
}

/// <summary>
/// One shared valve for every request to Letterboxd: limits concurrency, spaces requests out,
/// and when Letterboxd pushes back (429 / 503 / bot check) stops *all* traffic for a while
/// instead of hammering the site and getting the server's IP banned.
/// </summary>
public sealed class RequestGate
{
    private readonly object _lock = new();
    private int _active;
    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private string? _blockReason;

    /// <summary>Gets a value indicating whether requests are currently paused, with the reason.</summary>
    public (bool Blocked, string? Reason, DateTimeOffset Until) Status
    {
        get
        {
            lock (_lock)
            {
                return (_blockedUntil > DateTimeOffset.UtcNow, _blockReason, _blockedUntil);
            }
        }
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Waits for a free slot. Throws <see cref="LetterboxdException"/> immediately if we are backing off.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var wait = TimeSpan.Zero;
            var reserved = false;
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                if (_blockedUntil > now)
                {
                    throw new LetterboxdException(
                        LetterboxdErrorKind.Blocked,
                        $"{_blockReason ?? "Letterboxd asked us to slow down"}; retrying in about {(int)Math.Ceiling((_blockedUntil - now).TotalMinutes)} min.");
                }

                var max = Math.Clamp(Config.MaxConcurrentRequests, 1, 8);
                if (_active < max)
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Clamp(Config.RequestDelayMs, 0, 10_000));
                    var start = _nextSlot > now ? _nextSlot : now;
                    _nextSlot = start + delay;
                    _active++;
                    wait = start - now;
                    reserved = true;
                }
            }

            if (reserved)
            {
                var releaser = new Releaser(this);
                try
                {
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                    }
                }
                catch
                {
                    releaser.Dispose();
                    throw;
                }

                return releaser;
            }

            await Task.Delay(40, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Feeds an HTTP result back so the gate can back off when needed.</summary>
    public void Report(HttpStatusCode status, TimeSpan? retryAfter, bool botCheck)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (status == HttpStatusCode.TooManyRequests)
            {
                Block(now + Clamp(retryAfter ?? TimeSpan.FromSeconds(60)), "Letterboxd is rate-limiting requests");
            }
            else if (status == HttpStatusCode.ServiceUnavailable)
            {
                Block(now + Clamp(retryAfter ?? TimeSpan.FromSeconds(30)), "Letterboxd is temporarily unavailable");
            }
            else if (status == HttpStatusCode.Forbidden && botCheck)
            {
                Block(now + TimeSpan.FromMinutes(10), "Letterboxd is showing a bot check to this server");
            }
        }
    }

    /// <summary>Clears any back-off (used by the "retry now" button).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _blockedUntil = DateTimeOffset.MinValue;
            _blockReason = null;
        }
    }

    private static TimeSpan Clamp(TimeSpan t) => t > TimeSpan.FromMinutes(15) ? TimeSpan.FromMinutes(15) : (t < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : t);

    private void Block(DateTimeOffset until, string reason)
    {
        if (until > _blockedUntil)
        {
            _blockedUntil = until;
            _blockReason = reason;
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            _active--;
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly RequestGate _gate;
        private int _released;

        public Releaser(RequestGate gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _gate.Release();
            }
        }
    }
}
