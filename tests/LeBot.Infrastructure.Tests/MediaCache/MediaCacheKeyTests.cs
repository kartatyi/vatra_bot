using LeBot.Infrastructure.MediaCache;

namespace LeBot.Infrastructure.Tests.MediaCache;

public class MediaCacheKeyTests
{
    [Theory]
    // Trailing slash and www: the same post, two ways of writing it.
    [InlineData("https://www.tiktok.com/@user/video/123", "https://tiktok.com/@user/video/123/")]
    // Scheme never changes which video comes back.
    [InlineData("http://tiktok.com/@user/video/123", "https://tiktok.com/@user/video/123")]
    // Host case is meaningless; path case is preserved but identical here.
    [InlineData("https://TikTok.com/@user/video/123", "https://tiktok.com/@user/video/123")]
    // The share sheet's fingerprints — the reason two people posting one clip must still collide.
    [InlineData("https://tiktok.com/@user/video/123?_r=1&_t=abc", "https://tiktok.com/@user/video/123")]
    [InlineData("https://instagram.com/p/abc/?igshid=XYZ", "https://instagram.com/p/abc/")]
    [InlineData("https://x.com/u/status/9?s=20&t=kk", "https://x.com/u/status/9")]
    [InlineData("https://youtube.com/watch?v=abc&utm_source=news", "https://youtube.com/watch?v=abc")]
    // Order of the surviving params must not matter.
    [InlineData("https://example.com/w?a=1&b=2", "https://example.com/w?b=2&a=1")]
    // Fragments are client-side only.
    [InlineData("https://example.com/w?a=1#comments", "https://example.com/w?a=1")]
    public void For_UrlsThatPointAtTheSameContent_ShareAKey(string first, string second)
    {
        var left = MediaCacheKey.For(new Uri(first));
        var right = MediaCacheKey.For(new Uri(second));

        left.DirectoryName.Should().Be(right.DirectoryName);
        left.NormalizedUrl.Should().Be(right.NormalizedUrl);
    }

    [Theory]
    [InlineData("https://tiktok.com/@user/video/123", "https://tiktok.com/@user/video/124")]
    // ?v= selects the video — dropping it would repost the wrong one.
    [InlineData("https://youtube.com/watch?v=abc", "https://youtube.com/watch?v=xyz")]
    // Platform IDs are case-sensitive.
    [InlineData("https://instagram.com/p/AbC/", "https://instagram.com/p/abc/")]
    [InlineData("https://instagram.com/p/abc/", "https://threads.com/p/abc/")]
    public void For_UrlsThatPointAtDifferentContent_GetDifferentKeys(string first, string second)
    {
        var left = MediaCacheKey.For(new Uri(first));
        var right = MediaCacheKey.For(new Uri(second));

        left.DirectoryName.Should().NotBe(right.DirectoryName);
    }

    [Fact]
    public void For_DirectoryName_IsFilesystemSafe()
    {
        var key = MediaCacheKey.For(new Uri("https://example.com/a b/../%20?q=<>"));

        key.DirectoryName.Should().MatchRegex("^[0-9a-f]{32}$");
    }
}
