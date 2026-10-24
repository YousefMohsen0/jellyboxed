using System;
using System.Collections.Concurrent;
using Jellyfin.Plugin.Jellyboxed.Data;
using Jellyfin.Plugin.Jellyboxed.Letterboxd;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Services;

/// <summary>
/// Keeps one <see cref="LetterboxdClient"/> per Jellyfin user, so cookie rotation done by Letterboxd
/// is tracked in the cookie jar and written back to accounts.json (the old proxy lost rotated cookies on every restart).
/// </summary>
public sealed class ClientPool : IDisposable
{
    private readonly RequestGate _gate;
    private readonly AccountStore _accounts;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<Guid, Entry> _clients = new();

    public ClientPool(RequestGate gate, AccountStore accounts, ILoggerFactory loggerFactory)
    {
        _gate = gate;
        _accounts = accounts;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Gets the client for an account, creating (or recreating, if the account changed) as needed.</summary>
    public LetterboxdClient Get(LetterboxdAccount account)
    {
        while (true)
        {
            if (_clients.TryGetValue(account.UserId, out var existing))
            {
                if (existing.Username == account.Username && (existing.Cookies == account.Cookies || existing.Persisted == account.Cookies))
                {
                    return existing.Client;
                }

                if (_clients.TryRemove(new System.Collections.Generic.KeyValuePair<Guid, Entry>(account.UserId, existing)))
                {
                    existing.Client.Dispose();
                }

                continue;
            }

            var fresh = new Entry(
                new LetterboxdClient(account.Username, account.Cookies, _gate, _loggerFactory.CreateLogger<LetterboxdClient>()),
                account.Username,
                account.Cookies);
            if (_clients.TryAdd(account.UserId, fresh))
            {
                return fresh.Client;
            }

            fresh.Client.Dispose();
        }
    }

    /// <summary>Saves the cookie jar back to the account if Letterboxd rotated any cookies.</summary>
    public void PersistCookies(Guid userId)
    {
        if (!_clients.TryGetValue(userId, out var entry) || !entry.Client.HasSessionCookie)
        {
            return;
        }

        var exported = entry.Client.ExportCookies();
        if (string.IsNullOrEmpty(exported) || exported == entry.Persisted)
        {
            return;
        }

        entry.Persisted = exported;
        _accounts.Update(userId, a =>
        {
            if (a.Level == AuthLevel.Session)
            {
                a.Cookies = exported;
            }
        });
    }

    /// <summary>Drops the client so the next use starts from the stored cookies.</summary>
    public void Forget(Guid userId)
    {
        if (_clients.TryRemove(userId, out var entry))
        {
            entry.Client.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var e in _clients.Values)
        {
            e.Client.Dispose();
        }

        _clients.Clear();
    }

    private sealed class Entry
    {
        public Entry(LetterboxdClient client, string username, string cookies)
        {
            Client = client;
            Username = username;
            Cookies = cookies;
            Persisted = cookies;
        }

        public LetterboxdClient Client { get; }

        public string Username { get; }

        public string Cookies { get; }

        public string Persisted { get; set; }
    }
}
