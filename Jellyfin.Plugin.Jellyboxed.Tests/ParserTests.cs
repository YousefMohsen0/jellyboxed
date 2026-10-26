using System.Linq;
using Jellyfin.Plugin.Jellyboxed.Letterboxd;
using Xunit;

namespace Jellyfin.Plugin.Jellyboxed.Tests;

public class ParserTests
{
    private const string FriendsHtml = @"<html><body><table class=""member-table""><tbody>
      <tr><td><a class=""avatar"" href=""/ann/""><img src=""https://a.ltrbxd.com/resized/avatar/ann.jpg""></a><a class=""name"" href=""/ann/"">Ann &amp; Co</a></td>
          <td class=""col-rating""><span class=""rating rated-9""></span></td><td class=""col-review""><a href=""/ann/film/dune/"">Read</a></td></tr>
      <tr><td><a class=""avatar""><img src=""https://a.ltrbxd.com/x.jpg""></a><a class=""name"" href=""/bob/"">Bob</a></td>
          <td class=""col-rating""><span class=""rating rated-5""></span></td><td class=""col-review""></td></tr>
      <tr><td><a class=""name"">Cy</a></td><td class=""col-rating""></td></tr>
    </tbody></table></body></html>";

    [Fact]
    public void Friends_AreParsedFromTheMemberTable()
    {
        var rows = LetterboxdParser.ParseFriends(FriendsHtml);

        Assert.Equal(3, rows.Count);
        Assert.Equal("Ann & Co", rows[0].Friend.Name);
        Assert.Equal("★★★★½", rows[0].Friend.Rating);
        Assert.Equal("★★½", rows[1].Friend.Rating);
        Assert.Equal(string.Empty, rows[2].Friend.Rating);
        Assert.Equal("/ann/film/dune/", rows[0].ReviewHref);
        Assert.Null(rows[1].ReviewHref);
    }

    [Fact]
    public void ReviewBody_DropsTheHiddenDuplicate()
    {
        var text = LetterboxdParser.ParseReviewBody(@"<div class=""js-review-body""><p>Great <b>film</b>.</p><span class=""hidden"">TRUNCATED</span></div>");
        Assert.Equal("Great film.", text);
    }

    [Fact]
    public void GridSlugs_AreDeduplicated()
    {
        var slugs = LetterboxdParser.ParseGridSlugs(@"<ul><li data-item-slug=""dune-2021""></li><li data-item-slug=""heat""></li><li data-item-slug=""heat""></li></ul>");
        Assert.Equal(2, slugs.Count);
        Assert.Contains("heat", slugs);
    }

    [Fact]
    public void TmdbId_IsFoundInLinkOrBodyAttribute()
    {
        Assert.Equal("438631", LetterboxdParser.ParseTmdbId("<a href=\"https://www.themoviedb.org/movie/438631/\">TMDB</a>"));
        Assert.Equal("99", LetterboxdParser.ParseTmdbId("<body data-tmdb-id=\"99\"></body>"));
    }

    [Fact]
    public void FilmId_FallsBackToPosterUidAndInlineJson()
    {
        var a = LetterboxdParser.ParseFilmIds("<div data-postered-identifier='{\"lid\":\"abc\",\"uid\":\"film:4242\"}'></div>", null, "heat");
        Assert.Equal("4242", a.FilmId);
        var b = LetterboxdParser.ParseFilmIds("<script>var x = {\"filmId\": 99};</script>", null, "heat");
        Assert.Equal("99", b.FilmId);
        var c = LetterboxdParser.ParseFilmIds("<html>nothing here</html>", null, "heat");
        Assert.Null(c.FilmId);
    }

    [Fact]
    public void FilmIds_ComeFromPosterJsonAndFilmIdAttribute()
    {
        var ids = LetterboxdParser.ParseFilmIds("<div data-postered-identifier='{\"lid\":\"abc\"}'></div><div data-film-id=\"77\"></div>", null, "heat");
        Assert.Equal("abc", ids.ProductionId);
        Assert.Equal("77", ids.FilmId);
    }

    [Fact]
    public void Rss_ReadsWatchedDateAndRewatch()
    {
        const string xml = """
        <rss xmlns:letterboxd="https://letterboxd.com" xmlns:tmdb="https://themoviedb.org"><channel>
          <item><link>https://letterboxd.com/me/film/heat/1/</link><letterboxd:watchedDate>2026-10-20</letterboxd:watchedDate><letterboxd:rewatch>Yes</letterboxd:rewatch><tmdb:movieId>949</tmdb:movieId></item>
          <item><link>https://letterboxd.com/me/film/dune-2021/</link><letterboxd:watchedDate>not-a-date</letterboxd:watchedDate><letterboxd:rewatch>No</letterboxd:rewatch></item>
        </channel></rss>
        """;
        var entries = LetterboxdParser.ParseRss(xml);
        Assert.Equal("2026-10-20", entries[0].WatchedDate);
        Assert.True(entries[0].Rewatch);
        Assert.Null(entries[1].WatchedDate);
        Assert.False(entries[1].Rewatch);
    }

    [Fact]
    public void DiaryRows_ReadSlugDateAndRewatch()
    {
        const string html = """
        <table><tr class="diary-entry-row viewing-poster-container"><td class="td-day diary-day center"><a href="/me/films/diary/for/2026/10/20/">20</a></td>
        <td class="td-film-details"><div class="film-poster" data-film-slug="heat"></div></td><td class="td-rewatch center icon-status-off"></td></tr>
        <tr class="diary-entry-row viewing-poster-container"><td class="td-day"><a href="/me/films/diary/for/2025/01/02/">2</a></td>
        <td class="td-film-details"><div class="film-poster" data-film-slug="heat"></div></td><td class="td-rewatch center"></td></tr></table>
        """;
        var rows = LetterboxdParser.ParseDiaryRows(html);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new DiaryRow("heat", "2026-10-20", false), rows[0]);
        Assert.Equal(new DiaryRow("heat", "2025-01-02", true), rows[1]);
    }

    [Fact]
    public void NoteViewing_CountsNewDatesOnly()
    {
        var state = new Jellyfin.Plugin.Jellyboxed.Data.AccountState();
        Assert.True(state.NoteViewing("heat", "2026-01-01", false));
        Assert.False(state.NoteViewing("heat", "2026-01-01", false));
        Assert.True(state.NoteViewing("heat", "2026-03-01", true));
        Assert.False(state.NoteViewing("heat", "2025-12-01", false));
        var info = state.Diary["heat"];
        Assert.Equal(2, info.Count);
        Assert.True(info.Rewatch);
        Assert.Equal("2026-03-01", info.Last);
        Assert.Contains("heat", state.Watched);
    }

    [Fact]
    public void FindLikeUrls_PicksTheRightDirection()
    {
        const string html = "<a href=\"/film/dune/like/\"></a><a href=\"/film/dune/unlike/\"></a><a href=\"/me/likes/films/\"></a><span data-url=\"https://letterboxd.com/s/film:1/like\"></span>";
        var like = LetterboxdParser.FindLikeUrls(html, true);
        Assert.Contains("/film/dune/like/", like);
        Assert.Contains("https://letterboxd.com/s/film:1/like", like);
        Assert.DoesNotContain("/film/dune/unlike/", like);
        Assert.DoesNotContain("/me/likes/films/", like);
        Assert.Equal(new[] { "/film/dune/unlike/" }, LetterboxdParser.FindLikeUrls(html, false));
    }

    [Fact]
    public void OwnRating_AndLovedMarkers()
    {
        Assert.Equal(3.5, LetterboxdParser.ParseOwnRating("<span class=\"rating rated-7\"></span>"));
        Assert.True(LetterboxdParser.ParseLoved("<span data-is-liked=\"true\"></span>"));
        Assert.False(LetterboxdParser.ParseLoved("<span>nothing</span>"));
    }

    [Fact]
    public void Rss_YieldsSlugRatingLikeAndTmdbId()
    {
        const string rss = @"<?xml version=""1.0""?><rss xmlns:letterboxd=""https://letterboxd.com"" xmlns:tmdb=""https://themoviedb.org""><channel>
          <item><link>https://letterboxd.com/me/film/dune-2021/</link><letterboxd:filmTitle>Dune</letterboxd:filmTitle><letterboxd:memberRating>4.0</letterboxd:memberRating><letterboxd:memberLike>Yes</letterboxd:memberLike><tmdb:movieId>438631</tmdb:movieId></item>
          <item><link>https://letterboxd.com/me/film/heat/1/</link><letterboxd:memberLike>No</letterboxd:memberLike><tmdb:movieId>949</tmdb:movieId></item>
          <item><link>https://letterboxd.com/me/list/foo/</link></item></channel></rss>";

        var entries = LetterboxdParser.ParseRss(rss);

        Assert.Equal(2, entries.Count);
        Assert.Equal("dune-2021", entries[0].Slug);
        Assert.Equal(4.0, entries[0].Rating);
        Assert.True(entries[0].Liked);
        Assert.Equal("438631", entries[0].TmdbId);
        Assert.Equal("heat", entries[1].Slug);
        Assert.Null(entries[1].Rating);
    }

    [Theory]
    [InlineData("https://letterboxd.com/film/dune-2021/", "dune-2021")]
    [InlineData("/me/film/heat/1/", "heat")]
    [InlineData("/tmdb/1/", null)]
    public void SlugFromFilmUrl(string url, string? expected) => Assert.Equal(expected, LetterboxdParser.SlugFromFilmUrl(url));

    [Fact]
    public void StarText_RendersHalfStars()
    {
        Assert.Equal("★★★½", LetterboxdParser.StarText(3.5));
        Assert.Equal(string.Empty, LetterboxdParser.StarText(0));
    }

    [Fact]
    public void HtmlLite_SelectsWithDescendantAndAttributeSelectors()
    {
        var doc = HtmlParser.Parse("<div class=\"a\"><p><span data-x=\"1\" class=\"b c\">hi</span></p></div><span class=\"b\">no</span>");
        Assert.Single(doc.Select("div.a span.b"));
        Assert.Single(doc.Select("[data-x=1]"));
        Assert.Equal(2, doc.Select(".b").Count());
        Assert.Equal("hi", doc.SelectOne("div > p > span")?.InnerText);
    }
}
