using System;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Eval;

/// <summary>
/// The secret check git sync runs before it publishes a memory (spec D9): <see cref="SensitiveContentScanner.ScanSecrets(string?)"/>
/// finds everything the transcript archive's redactor finds as a token or a private key, keeps today's own patterns, and
/// fails closed on a match timeout. Every token and key comes from <see cref="FakeSecrets"/>.
/// </summary>
public sealed class SensitiveContentScannerSecretsTests
{
    private static readonly FakeSecrets Fakes = new(11);

    private const string TokenReason = "API token or secret";
    private const string PrivateKeyReason = "private key block";
    private const string TimedOut = "scan timed out";

    private static string BeginLine(string label) =>
        new string('-', 5) + "BEGIN " + label + " " + "PRIVATE" + " KEY" + new string('-', 5);

    [Theory]
    [InlineData("anthropic-key")]
    [InlineData("openai-project-key")]
    [InlineData("github-token")]
    [InlineData("github-pat")]
    [InlineData("aws-key")]
    public void ScanSecrets_TokenShapes_AreATokenHit(string name)
    {
        var reasons = SensitiveContentScanner.ScanSecrets("use " + Fakes.Get(name) + " for the call");
        Assert.Equal(new[] { TokenReason }, reasons);
    }

    [Fact]
    public void ScanSecrets_PgpPrivateKeyBlock_IsAPrivateKeyHit()
    {
        var reasons = SensitiveContentScanner.ScanSecrets("before\n" + Fakes.Get("pgp-private-key") + "\nafter");
        Assert.Equal(new[] { PrivateKeyReason }, reasons);
    }

    [Fact]
    public void ScanSecrets_RsaPrivateKey_IsAPrivateKeyHit()
    {
        var reasons = SensitiveContentScanner.ScanSecrets("before\n" + Fakes.Get("private-key") + "\nafter");
        Assert.Equal(new[] { PrivateKeyReason }, reasons);
    }

    [Fact]
    public void ScanSecrets_OpenSshBeginLine_IsAPrivateKeyHit()
    {
        var reasons = SensitiveContentScanner.ScanSecrets(BeginLine("OPENSSH"));
        Assert.Equal(new[] { PrivateKeyReason }, reasons);
    }

    [Fact]
    public void ScanSecrets_LegacySkWithoutLeftBoundary_IsStillAHit()
    {
        // The one accepted false-positive class (D9): Redact keeps this text, the sync check holds it back.
        var reasons = SensitiveContentScanner.ScanSecrets("mask-abcdefghijklmnopqrst");
        Assert.Equal(new[] { TokenReason }, reasons);
    }

    [Theory]
    [InlineData("git@github.com")]
    [InlineData("noreply@example.com")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Prefer small commits and run the focused tests before pushing the branch.")]
    public void ScanSecrets_CleanText_HasNoReason(string? text)
    {
        Assert.Empty(SensitiveContentScanner.ScanSecrets(text));
    }

    [Fact]
    public void ScanSecrets_OnTimeout_FailsClosed()
    {
        var reasons = SensitiveContentScanner.ScanSecrets(SecretScanNearMisses.Text(), TimeSpan.FromTicks(1));
        Assert.Equal(new[] { TimedOut }, reasons);
    }

    [Fact]
    public void Scan_OnTimeout_FailsClosed()
    {
        var reasons = SensitiveContentScanner.Scan(
            SecretScanNearMisses.Text(), null, Detectors.Token | Detectors.PrivateKey, TimeSpan.FromTicks(1));
        Assert.Contains(TimedOut, reasons);
    }

    [Fact]
    public void Scan_EmailNearMiss_OnTimeout_FailsClosed()
    {
        // a mebibyte of local-part characters with no "@": the email pattern is tried at every position and runs to the end
        var nearMiss = new string('a', 1024 * 1024);
        var reasons = SensitiveContentScanner.Scan(nearMiss, null, Detectors.Email, TimeSpan.FromTicks(1));
        Assert.Equal(new[] { TimedOut }, reasons);
    }

    [Fact]
    public void ScanSecrets_NearMissesWithTheDefaultBound_Completes()
    {
        var reasons = SensitiveContentScanner.ScanSecrets(SecretScanNearMisses.Text());
        Assert.NotNull(reasons);
    }
}
