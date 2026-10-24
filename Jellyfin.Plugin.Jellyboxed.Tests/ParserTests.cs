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
    public void FilmIds_ComeFromPosterJsonAndFilmIdAttribute()
    {
        var ids = LetterboxdParser.ParseFilmIds("<div data-postered-identifier='{\"lid\":\"abc\"}'></div><div data-film-id=\"77\"></div>", null, "heat");
        Assert.Equal("abc", ids.ProductionId);
        Assert.Equal("77", ids.FilmId);
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
