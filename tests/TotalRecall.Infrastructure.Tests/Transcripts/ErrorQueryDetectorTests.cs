using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class ErrorQueryDetectorTests
{
    [Theory]
    [InlineData("Parse error: no such table")]
    [InlineData("ENOENT")]
    [InlineData("SQLITE_BUSY")]
    [InlineData("exit 137")]
    [InlineData("process finished with exit code 1")]
    [InlineData("HTTP 500")]
    [InlineData("status 404 on the callback")]
    [InlineData("NullPointerException")]
    [InlineData("System.IO.IOException")]
    [InlineData("requests.ConnectionError")]
    [InlineData("Traceback (most recent call last)")]
    [InlineData("the build failed again")]
    [InlineData("fatal: not a git repository")]
    [InlineData("kernel panic on boot")]
    // Documented: the keyword rule is substring-based and says nothing about intent, so a topical query counts.
    [InlineData("error handling strategy discussion")]
    public void LooksLikeError_True(string query) => Assert.True(ErrorQueryDetector.LooksLikeError(query));

    [Theory]
    [InlineData("how do we ship the staging site")]
    [InlineData("Entity")]
    [InlineData("ABC")]
    [InlineData("exit strategy for the migration")]
    [InlineData("status of the release")]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeError_False(string query) => Assert.False(ErrorQueryDetector.LooksLikeError(query));
}
