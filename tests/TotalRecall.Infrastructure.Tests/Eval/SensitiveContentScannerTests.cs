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
    public void Scan_DetectsConfiguredInternalTerm_CaseInsensitive_WithoutEchoingTheTerm()
    {
        var reasons = SensitiveContentScanner.Scan(
            "the CORTEX staging rotation procedure",
            new[] { "cortex", "talentbrew" });
        // Detection fires...
        Assert.Contains("internal term", reasons);
        // ...but the matched term value is NEVER echoed (it could be a real secret).
        Assert.DoesNotContain(reasons, r => r.Contains("cortex", System.StringComparison.OrdinalIgnoreCase));
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

    // ---- the two-argument form, pinned (milestone 1a-1 made the detectors selectable) -------------------------

    private static readonly string[] Terms = ["internalproduct"];

    public static TheoryData<string, string[]> PinnedTable => new()
    {
        { "mail me at jane.doe@example.com", ["email address"] },
        { "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", ["API token or secret"] },
        { "-----BEGIN RSA PRIVATE KEY-----", ["private key block"] },
        { "jane@example.com ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -----BEGIN PRIVATE KEY-----", ["email address", "API token or secret", "private key block"] },
        { "see InternalProduct docs", ["internal term"] },
        { "jane@example.com and internalproduct", ["email address", "internal term"] },
        { "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 internalproduct", ["API token or secret", "internal term"] },
        { "all: jane@example.com, AKIAIOSFODNN7EXAMPLE, -----BEGIN OPENSSH PRIVATE KEY-----, internalproduct", ["email address", "API token or secret", "private key block", "internal term"] },
        { "nothing to see here", [] },
        { "git@github.com looks like a mail address to the full scan", ["email address"] },
    };

    [Theory]
    [MemberData(nameof(PinnedTable))]
    public void Scan_TwoArguments_ReturnsExactlyTheReasonsInTheOrderItAlwaysDid(string text, string[] expected)
    {
        Assert.Equal(expected, SensitiveContentScanner.Scan(text, Terms));
        Assert.Equal(expected, SensitiveContentScanner.Scan(text, Terms, Detectors.All));
    }

    [Fact]
    public void Scan_WithSelectedDetectors_RunsOnlyThose()
    {
        const string all = "jane@example.com ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -----BEGIN PRIVATE KEY----- internalproduct";
        Assert.Equal(["email address", "internal term"], SensitiveContentScanner.Scan(all, Terms, Detectors.Email));
        Assert.Equal(["API token or secret", "internal term"], SensitiveContentScanner.Scan(all, Terms, Detectors.Token));
        Assert.Equal(["email address"], SensitiveContentScanner.Scan(all, null, Detectors.Email));
        Assert.Equal(["internal term"], SensitiveContentScanner.Scan(all, Terms, Detectors.None));
        Assert.Equal(["API token or secret", "private key block", "internal term"], SensitiveContentScanner.Scan(all, Terms, Detectors.Token | Detectors.PrivateKey));
        Assert.Equal(SensitiveContentScanner.Scan(all, null), SensitiveContentScanner.Scan(all, null, Detectors.All));
        Assert.Empty(SensitiveContentScanner.Scan(null, Terms, Detectors.All));
    }

    [Theory]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "API token or secret")]
    [InlineData("github_pat_11ABCDEF0123456789_abcdefABCDEF", "API token or secret")]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "API token or secret")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "private key block")]
    public void ScanSecrets_FindsTokensAndPrivateKeys(string text, string reason) =>
        Assert.Equal([reason], SensitiveContentScanner.ScanSecrets(text));

    [Theory]
    [InlineData("git@github.com")]
    [InlineData("noreply@x.com")]
    [InlineData("alice@example.com")]
    [InlineData("ordinary prose about internalproduct and tokens")]
    [InlineData("")]
    [InlineData(null)]
    public void ScanSecrets_DoesNotFlagAddressesProseOrNothing(string? text) =>
        Assert.Empty(SensitiveContentScanner.ScanSecrets(text));
}
