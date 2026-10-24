using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Services;

/// <summary>A movie in the Jellyfin library that has a TMDB id.</summary>
public sealed record LibraryMovie(Guid ItemId, string TmdbId);

/// <summary>The only place that touches Jellyfin's library / user-data APIs, so version changes stay in one file.</summary>
public sealed class JellyfinBridge
{
    private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(5);

    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly ILogger<JellyfinBridge> _logger;
    private readonly object _indexLock = new();
    private List<LibraryMovie> _movies = new();
    private Dictionary<Guid, string> _tmdbByItem = new();
    private DateTimeOffset _indexBuilt = DateTimeOffset.MinValue;
    private long _indexStamp;

    public JellyfinBridge(IUserManager users, ILibraryManager library, IUserDataManager userData, ILogger<JellyfinBridge> logger)
    {
        _users = users;
        _library = library;
        _userData = userData;
        _logger = logger;
    }

    /// <summary>Gets a stamp that changes whenever the movie index is rebuilt.</summary>
    public long IndexStamp
    {
        get
        {
            EnsureIndex();
            return _indexStamp;
        }
    }

    public User? GetUser(Guid userId) => _users.GetUserById(userId);

    public IEnumerable<(Guid Id, string Name)> GetAllUsers() => _users.GetUsers().Select(u => (u.Id, u.Username));

    /// <summary>All library movies that have a TMDB id (cached for a few minutes).</summary>
    public IReadOnlyList<LibraryMovie> GetMovies()
    {
        EnsureIndex();
        lock (_indexLock)
        {
            return _movies;
        }
    }

    public string? TmdbIdOf(Guid itemId)
    {
        EnsureIndex();
        lock (_indexLock)
        {
            return _tmdbByItem.GetValueOrDefault(itemId);
        }
    }

    public void InvalidateIndex()
    {
        lock (_indexLock)
        {
            _indexBuilt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>Gets a movie the user is allowed to see, or null (not found, not a movie, or hidden from this user).</summary>
    public (BaseItem Item, string? TmdbId)? GetVisibleMovie(Guid itemId, Guid userId)
    {
        var user = _users.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var item = _library.GetItemById<BaseItem>(itemId, user);
        if (item is not Movie)
        {
            return null;
        }

        return (item, item.GetProviderId(MetadataProvider.Tmdb));
    }

    public (string Name, long? RuntimeTicks, int? Year)? DescribeItem(BaseItem item) => (item.Name, item.RunTimeTicks, item.ProductionYear);

    public void SetFavorite(Guid userId, Guid itemId, bool favorite)
    {
        var user = _users.GetUserById(userId);
        var item = _library.GetItemById<BaseItem>(itemId);
        if (user is null || item is null)
        {
            return;
        }

        try
        {
            _userData.SaveUserData(user, item, new UpdateUserItemDataDto { IsFavorite = favorite }, UserDataSaveReason.UpdateUserRating);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyboxed: could not change the favourite flag of {Item}", itemId);
        }
    }

    public bool IsFavorite(Guid userId, Guid itemId)
    {
        var user = _users.GetUserById(userId);
        var item = _library.GetItemById<BaseItem>(itemId);
        return user is not null && item is not null && (_userData.GetUserData(user, item)?.IsFavorite ?? false);
    }

    public void MarkPlayed(Guid userId, Guid itemId)
    {
        var user = _users.GetUserById(userId);
        var item = _library.GetItemById<BaseItem>(itemId);
        if (user is null || item is null)
        {
            return;
        }

        try
        {
            var existing = _userData.GetUserData(user, item);
            var dto = new UpdateUserItemDataDto
            {
                Played = true,
                PlayCount = Math.Max(1, existing?.PlayCount ?? 0),
                LastPlayedDate = DateTime.UtcNow,
            };
            _userData.SaveUserData(user, item, dto, UserDataSaveReason.TogglePlayed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyboxed: could not mark {Item} as played", itemId);
        }
    }

    private void EnsureIndex()
    {
        lock (_indexLock)
        {
            if (DateTimeOffset.UtcNow - _indexBuilt < IndexTtl)
            {
                return;
            }

            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    Recursive = true,
                    DtoOptions = new DtoOptions(false),
                };

                var movies = new List<LibraryMovie>();
                var byItem = new Dictionary<Guid, string>();
                foreach (var item in _library.GetItemList(query))
                {
                    var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
                    if (!string.IsNullOrWhiteSpace(tmdb))
                    {
                        movies.Add(new LibraryMovie(item.Id, tmdb));
                        byItem[item.Id] = tmdb;
                    }
                }

                _movies = movies;
                _tmdbByItem = byItem;
                _indexStamp++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jellyboxed: could not read the movie library");
            }

            _indexBuilt = DateTimeOffset.UtcNow;
        }
    }
}
