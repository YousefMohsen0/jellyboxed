using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellyboxed.Letterboxd;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellyboxed.Tests;

public class CloudflarePassTests
{
    private static HttpListener Start(string reply, out string url)
    {
        var port = new Random().Next(20000, 40000);
        var listener = new HttpListener();
        url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(url);
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                using var reader = new StreamReader(ctx.Request.InputStream);
                _ = await reader.ReadToEndAsync();
                var bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                // Listener stopped.
            }
        });
        return listener;
    }

    [Fact]
    public async Task SolveAsync_ReadsClearanceCookieAndUserAgent()
    {
        CloudflarePass.Reset();
        using var listener = Start(
            "{\"status\":\"ok\",\"solution\":{\"cookies\":[{\"name\":\"other\",\"value\":\"x\"},{\"name\":\"cf_clearance\",\"value\":\"abc123\"}],\"userAgent\":\"Mozilla/5.0 Test\"}}",
            out var url);
        var pass = await CloudflarePass.SolveAsync(url, 30, null, NullLogger.Instance, CancellationToken.None);
        Assert.NotNull(pass);
        Assert.Equal("abc123", pass!.Clearance);
        Assert.Equal("Mozilla/5.0 Test", pass.UserAgent);
        Assert.Same(pass, CloudflarePass.Current);
        CloudflarePass.Reset();
    }

    [Fact]
    public async Task SolveAsync_ReturnsNullWhenSolverFails()
    {
        CloudflarePass.Reset();
        using var listener = Start("{\"status\":\"error\",\"message\":\"nope\"}", out var url);
        var pass = await CloudflarePass.SolveAsync(url, 30, null, NullLogger.Instance, CancellationToken.None);
        Assert.Null(pass);
        CloudflarePass.Reset();
    }

    [Fact]
    public async Task SolveAsync_SurvivesNonJsonAnswer()
    {
        CloudflarePass.Reset();
        using var listener = Start("Internal Server Error", out var url);
        var pass = await CloudflarePass.SolveAsync(url, 30, null, NullLogger.Instance, CancellationToken.None);
        Assert.Null(pass);
        CloudflarePass.Reset();
    }

    [Fact]
    public async Task FetchAsync_ReturnsPageBody()
    {
        CloudflarePass.Reset();
        using var listener = Start(
            "{\"status\":\"ok\",\"solution\":{\"url\":\"https://letterboxd.com/film/x/\",\"status\":200,\"response\":\"<html>hello</html>\"}}",
            out var url);
        var page = await CloudflarePass.FetchAsync(url, 30, "https://letterboxd.com/film/x/", Array.Empty<(string, string)>(), NullLogger.Instance, CancellationToken.None);
        Assert.NotNull(page);
        Assert.Equal(200, page!.Status);
        Assert.Contains("hello", page.Body);
    }

    [Fact]
    public async Task FetchAsync_ReturnsNullOnBotCheckPage()
    {
        CloudflarePass.Reset();
        using var listener = Start(
            "{\"status\":\"ok\",\"solution\":{\"status\":403,\"response\":\"<title>Just a moment...</title>\"}}",
            out var url);
        var page = await CloudflarePass.FetchAsync(url, 30, "https://letterboxd.com/", Array.Empty<(string, string)>(), NullLogger.Instance, CancellationToken.None);
        Assert.Null(page);
    }
}
