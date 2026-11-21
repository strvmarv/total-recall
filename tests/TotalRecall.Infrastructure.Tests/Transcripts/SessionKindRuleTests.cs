using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class SessionKindRuleTests
{
    public static TheoryData<string> OpeningPrompts() => new()
    {
        "Review this change for security problems in the diff",
        "You previously flagged three issues",
        "Scope: the parser only",
        "You are reviewing a plan",
        "Read the full review brief at docs/x.md",
        "Round 3 of the review",
        "round 12: findings",
        "   \n\tYou are reviewing the spec",
        "review this change for security",
        "SCOPE: everything",
    };

    [Theory]
    [InlineData("sdk-py")]
    [InlineData("SDK-ts")]
    [InlineData("sdk")]
    public void SdkEntrypoint_Automated(string entrypoint)
    {
        Assert.Equal("automated", SessionKindRule.Decide(entrypoint, 0, 5, "hello there", null));
        Assert.Equal("automated", SessionKindRule.Decide(entrypoint, 3, 2, "hello there", "interactive"));
    }

    [Theory]
    [MemberData(nameof(OpeningPrompts))]
    public void CliEntrypoint_OneTurnOpening_Automated(string text)
    {
        Assert.Equal("automated", SessionKindRule.Decide("cli", 0, 1, text, null));
        Assert.Equal("automated", SessionKindRule.Decide(null, 0, 1, text, null));
    }

    [Fact]
    public void OneTurn_PlainPrompt_Interactive()
    {
        Assert.Equal("interactive", SessionKindRule.Decide("cli", 0, 1, "fix the build please", null));
        Assert.Equal("interactive", SessionKindRule.Decide("cli", 0, 1, "Round the corners of the box", null));
        Assert.Equal("interactive", SessionKindRule.Decide("cli", 0, 1, null, null));
    }

    [Fact]
    public void OneTurn1501Chars_Automated() =>
        Assert.Equal("automated", SessionKindRule.Decide("cli", 0, 1, new string('a', 1501), null));

    [Fact]
    public void OneTurn1500Chars_Interactive() =>
        Assert.Equal("interactive", SessionKindRule.Decide("cli", 0, 1, new string('a', 1500), null));

    [Fact]
    public void TwoTurns_Interactive()
    {
        Assert.Equal("interactive", SessionKindRule.Decide("cli", 0, 2, "plain", null));
        Assert.Equal("interactive", SessionKindRule.Decide("cli", 0, 2, "Scope: the parser only", null));
        Assert.Equal("interactive", SessionKindRule.Decide("cli", 1, 1, "Scope: the parser only", null));
        Assert.Equal("interactive", SessionKindRule.Decide("cli", 0, 2, new string('a', 5000), "automated"));
    }

    [Fact]
    public void NoTurns_Interactive() =>
        Assert.Equal("interactive", SessionKindRule.Decide("cli", 0, 0, null, null));

    [Theory]
    [InlineData("automated", "automated")]
    [InlineData("interactive", "interactive")]
    [InlineData(null, "interactive")]
    public void OneSettledTurn_KeepsStoredKind(string? stored, string expected)
    {
        // The pass holds no turn of its own, so its first-user text (even a long or opening one) is not the session's first turn.
        Assert.Equal(expected, SessionKindRule.Decide("cli", 1, 0, "Scope: x", stored));
        Assert.Equal(expected, SessionKindRule.Decide("cli", 1, 0, null, stored));
    }

    [Fact]
    public void OneSettledTurn_SdkEntrypointStillWins() =>
        Assert.Equal("automated", SessionKindRule.Decide("sdk-py", 1, 0, null, "interactive"));

    [Fact]
    public void Openings_ListsTheKnownPrefixes()
    {
        Assert.Contains("Review this change for security", SessionKindRule.Openings);
        Assert.Contains("Scope:", SessionKindRule.Openings);
        Assert.Equal(1_500, SessionKindRule.LongPromptLimit);
    }
}
