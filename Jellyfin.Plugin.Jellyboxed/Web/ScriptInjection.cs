using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyboxed.Web;

/// <summary>Puts <see cref="ScriptInjectionMiddleware"/> at the front of the server's request pipeline.</summary>
public sealed class ScriptInjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseMiddleware<ScriptInjectionMiddleware>();
            next(app);
        };
}

/// <summary>
/// Adds two tags (stylesheet + script) to the web client's index.html as it is served. Nothing on disk is modified,
/// so the change disappears when the plugin is disabled or uninstalled and survives web client updates.
/// </summary>
public sealed class ScriptInjectionMiddleware
{
    private const string Marker = "data-jellyboxed";

    private readonly RequestDelegate _next;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    public ScriptInjectionMiddleware(RequestDelegate next, ILogger<ScriptInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>Builds the HTML snippet that is inserted before the closing body tag.</summary>
    public static string BuildSnippet(string version) =>
        $"<link rel=\"stylesheet\" href=\"../Jellyboxed/client.css?v={version}\" {Marker}>" +
        $"<script defer src=\"../Jellyboxed/client.js?v={version}\" {Marker}></script>";

    /// <summary>Inserts the snippet into an HTML document (idempotent).</summary>
    public static string Inject(string html, string snippet)
    {
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            idx = html.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
        }

        return idx < 0 ? html + snippet : html.Insert(idx, snippet);
    }

    internal static bool IsIndexRequest(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Handles one request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.InjectWebUi || !IsIndexRequest(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // We need the plain HTML: no compression, and no "304 Not Modified" for a page we have changed.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");
        context.Request.Headers.Remove("Range");

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false)
            && string.IsNullOrEmpty(context.Response.Headers.ContentEncoding.ToString());

        if (!isHtml)
        {
            await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        try
        {
            var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var bytes = Encoding.UTF8.GetBytes(Inject(html, BuildSnippet(Plugin.Instance!.VersionString)));
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentLength = bytes.Length;
            await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Jellyboxed: could not inject into index.html");
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
