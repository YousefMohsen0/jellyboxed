using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Jellyboxed.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Jellyboxed;

/// <summary>Jellyboxed: Letterboxd inside Jellyfin.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin's permanent id. Never change this: Jellyfin uses it to find the plugin's settings.</summary>
    public static readonly Guid PluginId = Guid.Parse("7b2f4a9e-3c61-4d58-9e0a-5f1c8d2b6a47");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Jellyboxed";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Letterboxd inside Jellyfin: your ratings on posters, friends' activity, log films after watching, and a synced watchlist.";

    /// <summary>Gets the version string used to cache-bust the injected script.</summary>
    public string VersionString => Version.ToString(4);

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            DisplayName = "Jellyboxed",
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "movie_filter",
        };
    }
}
