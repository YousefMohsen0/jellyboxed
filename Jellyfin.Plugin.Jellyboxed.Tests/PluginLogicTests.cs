using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellyboxed.Api;
using Jellyfin.Plugin.Jellyboxed.Data;
using Jellyfin.Plugin.Jellyboxed.Letterboxd;
using Jellyfin.Plugin.Jellyboxed.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellyboxed.Tests;

public class PluginLogicTests
{
    [Fact]
    public void Cookies_AcceptEveryCommonPasteFormat()
    {
        var header = LetterboxdClient.ParseCookieString("letterboxd.user=abc; com.xk72.webparts.csrf=zzz; letterboxd.user.CURRENT=; useMobileSite=no");
        Assert.Equal(3, header.Count); // the empty cookie is skipped
        Assert.Equal("abc", header["letterboxd.user"]);

        Assert.Equal("1", LetterboxdClient.ParseCookieString("Cookie: a=1; b=2")["a"]);
        Assert.Equal("2", LetterboxdClient.ParseCookieString("[{\"name\":\"a\",\"value\":\"1\"},{\"name\":\"b\",\"value\":\"2\"}]")["b"]);
        Assert.Equal(3, LetterboxdClient.ParseCookieString("a=1\nb=2\r\nc=3").Count);
    }

    [Fact]
    public void Injection_AddsTagsOnceBeforeClosingBody()
    {
        var snippet = ScriptInjectionMiddleware.BuildSnippet("1.0.0.0");
        var html = ScriptInjectionMiddleware.Inject("<html><body><div/></body></html>", snippet);

        Assert.Contains("client.js?v=1.0.0.0", html);
        Assert.True(html.IndexOf("</body>", StringComparison.Ordinal) > html.IndexOf("client.js", StringComparison.Ordinal));
        Assert.Equal(html, ScriptInjectionMiddleware.Inject(html, snippet)); // idempotent
    }

    [Theory]
    [InlineData("https://a.ltrbxd.com/resized/a.jpg", true)]
    [InlineData("https://letterboxd.com/a.jpg", true)]
    [InlineData("https://evil.com/a.jpg", false)]
    [InlineData("https://ltrbxd.com.evil.com/a.jpg", false)]
    [InlineData("http://a.ltrbxd.com/a.jpg", false)]
    [InlineData("https://169.254.169.254/", false)]
    public void ImageProxy_OnlyAllowsLetterboxdHosts(string url, bool allowed) =>
        Assert.Equal(allowed, JellyboxedController.IsAllowedImageUrl(url, out _));

    [Fact]
    public void JsonFile_RoundTripsAndSetsCorruptFilesAside()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jbx-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "state.json");
        try
        {
            using (var file = new DebouncedJsonFile<AccountState>(path, NullLogger.Instance, TimeSpan.FromMilliseconds(50)))
            {
                lock (file.SyncRoot)
                {
                    file.Value.Ratings["heat"] = new RatingEntry { Rating = 4.5, Loved = true };
                    file.Value.Watchlist.Add("dune-2021");
                }

                file.MarkDirty();
            }

            using (var reloaded = new DebouncedJsonFile<AccountState>(path, NullLogger.Instance))
            {
                Assert.Equal(4.5, reloaded.Value.Ratings["heat"].Rating);
                Assert.Contains("dune-2021", reloaded.Value.Watchlist);
            }

            File.WriteAllText(path, "{ not json");
            using var recovered = new DebouncedJsonFile<AccountState>(path, NullLogger.Instance);
            Assert.Empty(recovered.Value.Ratings);
            Assert.True(File.Exists(path + ".bad"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RequestGate_BacksOffOnRateLimitAndResumes()
    {
        var gate = new RequestGate();
        using (await gate.EnterAsync(CancellationToken.None)) { }

        gate.Report(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30), botCheck: false);
        var ex = await Assert.ThrowsAsync<LetterboxdException>(() => gate.EnterAsync(CancellationToken.None));
        Assert.Equal(LetterboxdErrorKind.Blocked, ex.Kind);

        gate.Reset();
        using (await gate.EnterAsync(CancellationToken.None)) { }

        gate.Report(HttpStatusCode.Forbidden, null, botCheck: false); // a plain 403 (private profile) must not stop everything
        using (await gate.EnterAsync(CancellationToken.None)) { }
    }
}
