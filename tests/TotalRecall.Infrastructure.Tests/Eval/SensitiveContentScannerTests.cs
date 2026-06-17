using System.Linq;
using TotalRecall.Infrastructure.Eval;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Eval;

/// <summary>
/// SensitiveContentScanner guards the eval_grow accept path: real captured
/// query/content must not be silently appended to the public retrieval.jsonl
/// if it carries secrets, PII, or caller-configured internal terms.
/// </summary>
public sealed class SensitiveContentScannerTests
{
    [Fact]
    public void Scan_CleanText_ReturnsNoReasons()
    {
        var reasons = SensitiveContentScanner.Scan("what npm client to install packages", null);
        Assert.Empty(reasons);
    }

    [Theory]
    [InlineData("contact me at jane.doe@example.com please")]
    [InlineData("token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [InlineData("aws key AKIAIOSFODNN7EXAMPLE rotated")]
    [InlineData("use github_pat_11ABCDEF0123456789_abcdefABCDEF for mirrors")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    public void Scan_DetectsGenericSecretsAndPii(string text)
    {
        var reasons = SensitiveContentScanner.Scan(text, null);
        Assert.NotEmpty(reasons);
    }

    [Fact]
    public void Scan_DetectsConfiguredInternalTerm_CaseInsensitive()
    {
        var reasons = SensitiveContentScanner.Scan(
            "the CORTEX staging rotation procedure",
            new[] { "cortex", "talentbrew" });
        Assert.Contains(reasons, r => r.Contains("cortex", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scan_DoesNotEchoSecretValue()
    {
        // Reasons are category labels — they must never leak the matched secret.
        var reasons = SensitiveContentScanner.Scan(
            "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", null);
        Assert.NotEmpty(reasons);
        Assert.DoesNotContain(reasons, r => r.Contains("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ"));
    }

    [Fact]
    public void Scan_NullOrEmpty_ReturnsNoReasons()
    {
        Assert.Empty(SensitiveContentScanner.Scan(null, new[] { "cortex" }));
        Assert.Empty(SensitiveContentScanner.Scan("", new[] { "cortex" }));
    }

    [Fact]
    public void Scan_EmptyConfiguredTerm_IsIgnored()
    {
        // A blank/whitespace configured term must not match everything.
        var reasons = SensitiveContentScanner.Scan("perfectly clean text", new[] { "", "   " });
        Assert.Empty(reasons);
    }
}
