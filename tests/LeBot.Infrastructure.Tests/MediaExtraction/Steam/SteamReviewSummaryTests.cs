using System.Text.Json;
using LeBot.Infrastructure.MediaExtraction.Steam;

namespace LeBot.Infrastructure.Tests.MediaExtraction.Steam;

public class SteamReviewSummaryTests
{
    [Fact]
    public void Parse_RatedApp_ReadsVerdictAndCounts()
    {
        var summary = SteamReviewSummary.Parse(SteamFixtures.Reviews());

        summary.Should().Be(new SteamReviewSummary("дуже схвальні", TotalPositive: 77039, TotalReviews: 85435));
    }

    [Theory]
    [InlineData(77039, 85435, 90)]
    [InlineData(199, 200, 99)]
    [InlineData(1, 3, 33)]
    [InlineData(3, 3, 100)]
    public void PositivePercent_RoundsDownSoOnlyAnUnanimousVerdictReadsAHundred(int positive, int total, int expected)
    {
        var summary = new SteamReviewSummary("verdict", positive, total);

        summary.PositivePercent.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{ "success": 1, "query_summary": { "review_score": 0, "review_score_desc": "Немає користувацьких рецензій", "total_positive": 0, "total_reviews": 0 } }""")]
    [InlineData("""{ "success": 1, "query_summary": { "review_score": 0, "review_score_desc": "3 user reviews", "total_positive": 3, "total_reviews": 3 } }""")]
    [InlineData("""{ "success": 1, "query_summary": { "review_score": 8, "review_score_desc": "", "total_positive": 9, "total_reviews": 10 } }""")]
    [InlineData("""{ "success": 2 }""")]
    [InlineData("null")]
    public void Parse_NoVerdictToShow_ReturnsNull(string json)
    {
        var summary = SteamReviewSummary.Parse(json);

        summary.Should().BeNull();
    }

    [Fact]
    public void Parse_BodyIsNotJson_Throws()
    {
        var act = () => SteamReviewSummary.Parse("<html></html>");

        act.Should().Throw<JsonException>();
    }
}
