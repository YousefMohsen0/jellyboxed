using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Data;

/// <summary>
/// A JSON document on disk with an in-memory copy. Saves are debounced and atomic
/// (write to a temp file, then move) so a crash mid-write can never corrupt the cache,
/// and a corrupt file is set aside instead of crashing the plugin.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public sealed class DebouncedJsonFile<T> : IDisposable
    where T : class, new()
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _dirty;
    private bool _disposed;

    public DebouncedJsonFile(string path, ILogger logger, TimeSpan? delay = null, bool restrictPermissions = false)
    {
        _path = path;
        _logger = logger;
        _delay = delay ?? TimeSpan.FromSeconds(2);
        RestrictPermissions = restrictPermissions;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Value = Load();
    }

    /// <summary>Gets the live document. Callers must hold <see cref="SyncRoot"/> while reading or mutating it.</summary>
    public T Value { get; private set; }

    /// <summary>Gets the lock that guards <see cref="Value"/>.</summary>
    public object SyncRoot => _gate;

    private bool RestrictPermissions { get; }

    /// <summary>Marks the document as changed and schedules a save.</summary>
    public void MarkDirty()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _dirty = true;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes pending changes to disk immediately.</summary>
    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            json = JsonSerializer.Serialize(Value, JsonOptions);
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (RestrictPermissions && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyboxed: could not save {Path}", _path);
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
        Flush();
    }

    private T Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<T>(File.ReadAllText(_path), JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyboxed: {Path} was unreadable and has been set aside; starting empty", _path);
            try
            {
                File.Move(_path, _path + ".bad", overwrite: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }

        return new T();
    }
}
