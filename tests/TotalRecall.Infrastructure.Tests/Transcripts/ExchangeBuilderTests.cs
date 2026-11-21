using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class ExchangeBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static long _offset;

    private static TranscriptRecord Rec(RecordKind kind, int? seconds, string? text = null, string? tool = null, long? start = null)
    {
        var s = start ?? (_offset += 100);
        return new TranscriptRecord(kind, s, s + 50, seconds is null ? null : At(seconds.Value), text, tool);
    }

    private static TranscriptRecord U(int? t, string text, long? start = null) => Rec(RecordKind.UserText, t, text, null, start);
    private static TranscriptRecord A(int? t, string text, long? start = null) => Rec(RecordKind.AssistantText, t, text, null, start);
    private static TranscriptRecord S(int? t, string? detail, string tool = "Bash") => Rec(RecordKind.ToolStub, t, detail, tool);
    private static TranscriptRecord E(int? t, string? text) => Rec(RecordKind.ToolError, t, text);
    private static TranscriptRecord B(int? t) => Rec(RecordKind.CompactionBoundary, t);
    private static TranscriptRecord Sum(int? t, string text) => Rec(RecordKind.CompactionSummary, t, text);

    private static BuildResult Build(DateTimeOffset? cutoff, int first, params TranscriptRecord[] records) =>
        ExchangeBuilder.Build(records, cutoff, first);

    private static BuildResult Build(params TranscriptRecord[] records) => Build(null, 0, records);

    private static string Words(int length)
    {
        var sb = new StringBuilder(length + 8);
        while (sb.Length < length) sb.Append("alpha beta ");
        return sb.ToString(0, length);
    }

    private static string Padded(int secretStart, string secret)
    {
        // A run of words up to secretStart - 1, a space, then the secret, so it starts at index secretStart.
        var pad = Words(secretStart - 1).TrimEnd();
        pad = pad.PadRight(secretStart - 1, 'q');
        return pad + " " + secret + " tail";
    }

    private static void AssertNoWindow(string stored, string secret)
    {
        foreach (var w in LeakScan.Windows(secret))
            Assert.DoesNotContain(w, stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserThenAssistantStubError_IsOneUnit()
    {
        var r = Build(U(1, "q"), A(2, "a"), S(3, "ls"), E(4, "boom"));
        var unit = Assert.Single(r.Units);
        Assert.Equal(0, unit.Number);
        Assert.False(unit.IsSummary);
        Assert.Equal("q", unit.UserText);
        Assert.Equal(1, unit.UserTurns);
        Assert.Equal(
            new[]
            {
                new UnitBlock(BlockKind.User, "User: q"),
                new UnitBlock(BlockKind.Assistant, "Assistant: a"),
                new UnitBlock(BlockKind.Stub, "[Bash: ls]"),
                new UnitBlock(BlockKind.Error, "[error: boom]"),
            },
            unit.Blocks);
    }

    [Fact]
    public void ConsecutiveUserTexts_EachStartAUnit()
    {
        var r = Build(U(1, "one"), U(2, "two"), A(3, "a"));
        Assert.Equal(2, r.Units.Count);
        Assert.Equal(new[] { BlockKind.User }, r.Units[0].Blocks.Select(b => b.Kind));
        Assert.Equal(new[] { BlockKind.User, BlockKind.Assistant }, r.Units[1].Blocks.Select(b => b.Kind));
        Assert.Equal(2, r.UserTurns);
    }

    [Fact]
    public void BoundaryClosesUnit_NextAssistantStartsAssistantOnlyUnit()
    {
        var r = Build(U(1, "q"), A(2, "a"), B(3), A(4, "after"));
        Assert.Equal(2, r.Units.Count);
        var second = r.Units[1];
        Assert.Null(second.UserText);
        Assert.Equal(0, second.UserTurns);
        Assert.Equal(new[] { new UnitBlock(BlockKind.Assistant, "Assistant: after") }, second.Blocks);
        Assert.Equal(1, second.Number);
    }

    [Fact]
    public void Summary_IsItsOwnUnit_AndClosesTheOpenOne()
    {
        var r = Build(U(1, "q"), A(2, "a"), Sum(3, "the summary"), A(4, "next"));
        Assert.Equal(3, r.Units.Count);
        Assert.False(r.Units[0].IsSummary);
        var s = r.Units[1];
        Assert.True(s.IsSummary);
        Assert.Null(s.UserText);
        Assert.Equal(0, s.UserTurns);
        Assert.Equal(new[] { new UnitBlock(BlockKind.Summary, "the summary") }, s.Blocks);
        Assert.False(r.Units[2].IsSummary);
        Assert.Equal(new[] { new UnitBlock(BlockKind.Assistant, "Assistant: next") }, r.Units[2].Blocks);
    }

    [Fact]
    public void ErrorWithNothingOpen_StartsAssistantOnlyUnit()
    {
        var r = Build(E(1, "late error"));
        var unit = Assert.Single(r.Units);
        Assert.Null(unit.UserText);
        Assert.Equal(new[] { new UnitBlock(BlockKind.Error, "[error: late error]") }, unit.Blocks);
    }

    [Fact]
    public void ErrorWithUnitOpen_JoinsIt()
    {
        var r = Build(U(1, "q"), E(2, "bad"));
        var unit = Assert.Single(r.Units);
        Assert.Equal(new[] { BlockKind.User, BlockKind.Error }, unit.Blocks.Select(b => b.Kind));
    }

    [Fact]
    public void BashInputStub_JoinsOpenUnit_IsNotAUserTurn()
    {
        var r = Build(U(1, "q"), S(2, "ls -l"));
        var unit = Assert.Single(r.Units);
        Assert.Equal(1, unit.UserTurns);
        Assert.Equal(1, r.UserTurns);
        Assert.Equal("[Bash: ls -l]", unit.Blocks[1].Text);

        var alone = Build(S(1, "ls -l"));
        Assert.Equal(0, alone.UserTurns);
        Assert.Null(Assert.Single(alone.Units).UserText);
    }

    [Fact]
    public void Numbers_StartAtFirstUnitNumber_AndAreConsecutive()
    {
        var r = Build(null, 7, U(1, "a"), U(2, "b"), Sum(3, "s"), A(4, "c"));
        Assert.Equal(new[] { 7, 8, 9, 10 }, r.Units.Select(u => u.Number));
    }

    [Fact]
    public void Unit_StartOffsetIsFirstRecord_FirstTsIsFirstWithTs()
    {
        var r = Build(
            U(null, "q", start: 500),
            A(null, "a", start: 600),
            S(9, "ls"),
            A(3, "b"));
        var unit = Assert.Single(r.Units);
        Assert.Equal(500, unit.StartOffset);
        Assert.Equal(At(9), unit.FirstTs);

        var none = Build(U(null, "q"));
        Assert.Null(Assert.Single(none.Units).FirstTs);
    }

    [Fact]
    public void AssistantOnlyUnit_StartOffsetIsItsFirstRecord()
    {
        var r = Build(A(null, "a", start: 700), S(2, "ls"), E(3, "boom"));
        Assert.Equal(700, Assert.Single(r.Units).StartOffset);

        var afterBoundary = Build(U(1, "q", start: 100), B(2), A(3, "after", start: 900));
        Assert.Equal(new long[] { 100, 900 }, afterBoundary.Units.Select(u => u.StartOffset));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  \n\t \r\n")]
    public void EmptyOrWhitespaceText_IsNoContent(string blank)
    {
        // A blank user text neither starts a unit nor closes the open one; a blank answer or summary adds nothing.
        var r = Build(U(1, "q"), U(2, blank), A(3, blank), Sum(4, blank), A(5, "a"));
        var unit = Assert.Single(r.Units);
        Assert.Equal(
            new[] { new UnitBlock(BlockKind.User, "User: q"), new UnitBlock(BlockKind.Assistant, "Assistant: a") },
            unit.Blocks);
        Assert.Equal(1, r.UserTurns);

        var alone = Build(U(1, blank), A(2, blank), Sum(3, blank));
        Assert.Empty(alone.Units);
        Assert.Equal(0, alone.UserTurns);
        Assert.Null(alone.FirstUserText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Stub_WithoutToolName_IsNamedTool(string? tool)
    {
        var r = Build(Rec(RecordKind.ToolStub, 1, "x", tool));
        Assert.Equal("[Tool: x]", Assert.Single(Assert.Single(r.Units).Blocks).Text);
        Assert.Equal("[Tool]", Build(Rec(RecordKind.ToolStub, 1, null, tool)).Units[0].Blocks[0].Text);
    }

    [Fact]
    public void Stub_LongToolName_IsCutTo64()
    {
        var tool = new string('n', 200);
        var text = Assert.Single(Assert.Single(Build(S(1, "ls", tool)).Units).Blocks).Text;
        Assert.Equal("[" + new string('n', 64) + "...: ls]", text);
    }

    [Fact]
    public void Stub_ToolNameHoldingAToken_IsRedacted()
    {
        var secret = new FakeSecrets(6).Get("github-token");
        var text = Assert.Single(Assert.Single(Build(S(1, "ls", "mcp__" + secret)).Units).Blocks).Text;
        Assert.Equal("[mcp__[redacted:token]: ls]", text);
        AssertNoWindow(text, secret);
    }

    [Fact]
    public void Stub_ToolNameCutNeverLandsInsideAMarker()
    {
        // The marker would start at index 60 and run past 64: the cut goes before it.
        var secret = new FakeSecrets(7).Get("github-token");
        var text = Assert.Single(Assert.Single(Build(S(1, null, new string('n', 59) + " " + secret)).Units).Blocks).Text;
        Assert.Equal("[" + new string('n', 59) + " ...]", text);
        AssertNoWindow(text, secret);
    }

    [Fact]
    public void Cutoff_RecordAtCutoffDropped_AfterKept()
    {
        var r = Build(At(10), 0, U(9, "before"), U(10, "at"), U(11, "after"));
        var unit = Assert.Single(r.Units);
        Assert.Equal("after", unit.UserText);
    }

    [Fact]
    public void Cutoff_NullTsDroppedOnlyWhileCutoffExists()
    {
        var with = Build(At(10), 0, U(null, "no ts"), U(11, "kept"));
        Assert.Equal("kept", Assert.Single(with.Units).UserText);

        var without = Build(null, 0, U(null, "no ts"), U(11, "kept"));
        Assert.Equal(2, without.Units.Count);
        Assert.Equal("no ts", without.Units[0].UserText);
    }

    [Fact]
    public void StraddlingExchange_KeepsTheAnswerAsAssistantOnlyUnit()
    {
        var r = Build(At(10), 0, U(5, "question"), A(15, "answer"), S(16, "ls"));
        var unit = Assert.Single(r.Units);
        Assert.Null(unit.UserText);
        Assert.Equal(0, unit.UserTurns);
        Assert.Equal(new[] { BlockKind.Assistant, BlockKind.Stub }, unit.Blocks.Select(b => b.Kind));
        Assert.Equal(0, r.UserTurns);
        Assert.Null(r.FirstUserText);
    }

    [Fact]
    public void NoKeptRecords_NoUnits()
    {
        Assert.Empty(Build().Units);
        var dropped = Build(At(100), 3, U(1, "a"), A(2, "b"));
        Assert.Empty(dropped.Units);
        Assert.Null(dropped.FirstTs);
        Assert.Null(dropped.LastTs);
        Assert.Equal(0, dropped.UserTurns);
        Assert.Empty(Build(B(1), B(2)).Units);
    }

    [Fact]
    public void UserTurns_CountsUserTextOnly()
    {
        var r = Build(U(1, "a"), A(2, "x"), S(3, "ls"), E(4, "e"), U(5, "b"), Sum(6, "s"), A(7, "y"));
        Assert.Equal(2, r.UserTurns);
        Assert.Equal(2, r.Units.Sum(u => u.UserTurns));
    }

    [Fact]
    public void Stub_RedactThenFoldThenCut160()
    {
        var aws = new FakeSecrets(1).Get("aws-key");
        var raw = "echo start\n  echo two\n" + aws + "\n" + Words(300);
        var r = Build(S(1, raw));
        var text = Assert.Single(Assert.Single(r.Units).Blocks).Text;
        Assert.StartsWith("[Bash: ", text);
        Assert.EndsWith("...]", text);
        var detail = text["[Bash: ".Length..^1];
        // Folded first, so the cut takes 160 characters of the folded text: 160 plus the ellipsis.
        Assert.Equal(163, detail.Length);
        Assert.Contains("[redacted:token]", detail);
        Assert.DoesNotContain('\n', text);
        Assert.Contains("echo start echo two ", detail);
        AssertNoWindow(text, aws);
    }

    [Fact]
    public void Stub_ShortDetail_NoEllipsis_AndWrapperNotCounted()
    {
        var detail = Words(160).TrimEnd();
        detail = detail.PadRight(160, 'z');
        var text = Build(S(1, detail)).Units[0].Blocks[0].Text;
        Assert.Equal("[Bash: " + detail + "]", text);
    }

    [Fact]
    public void Stub_EmptyDetail_IsBareName()
    {
        Assert.Equal("[Read]", Build(S(1, null, "Read")).Units[0].Blocks[0].Text);
        Assert.Equal("[Read]", Build(S(1, "", "Read")).Units[0].Blocks[0].Text);
    }

    [Fact]
    public void Stub_TokenStraddling160_LeavesNoPrefix()
    {
        var secret = new FakeSecrets(2).Get("github-token");
        var raw = Padded(150, secret);
        Assert.Equal(150, raw.IndexOf(secret, StringComparison.Ordinal));
        var text = Build(S(1, raw)).Units[0].Blocks[0].Text;
        Assert.EndsWith("...]", text);
        Assert.DoesNotContain("redacted", text);
        AssertNoWindow(text, secret);
        Assert.DoesNotContain(secret[..4], text, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_Cut500WithEllipsis_NewlinesKept()
    {
        var raw = "first line\nsecond line\n" + Words(900);
        var text = Build(E(1, raw)).Units[0].Blocks[0].Text;
        Assert.StartsWith("[error: first line\nsecond line\n", text);
        Assert.EndsWith("...]", text);
        var body = text["[error: ".Length..^1];
        Assert.Equal(503, body.Length);
    }

    [Fact]
    public void Error_TokenStraddling500_LeavesNoPrefix()
    {
        var secret = new FakeSecrets(3).Get("github-token");
        var raw = Padded(495, secret);
        Assert.Equal(495, raw.IndexOf(secret, StringComparison.Ordinal));
        var text = Build(E(1, raw)).Units[0].Blocks[0].Text;
        Assert.EndsWith("...]", text);
        Assert.DoesNotContain("redacted", text);
        AssertNoWindow(text, secret);
        Assert.DoesNotContain(secret[..4], text, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_Empty_IsBareError()
    {
        Assert.Equal("[error]", Build(E(1, null)).Units[0].Blocks[0].Text);
        Assert.Equal("[error]", Build(E(1, "")).Units[0].Blocks[0].Text);
    }

    [Fact]
    public void Error_NotCappedAt100000_ButCutAt500()
    {
        var text = Build(E(1, Words(100_010))).Units[0].Blocks[0].Text;
        Assert.Equal("[error: ".Length + 503 + 1, text.Length);
        Assert.DoesNotContain("more characters not stored", text);
    }

    [Fact]
    public void Text_Cap100000_AppendsRemainder()
    {
        var text = Words(100_010);
        var block = Build(A(1, text)).Units[0].Blocks[0].Text;
        Assert.StartsWith("Assistant: ", block);
        Assert.EndsWith("[... 10 more characters not stored]", block);
        Assert.Equal("Assistant: ".Length + 100_000 + " [... 10 more characters not stored]".Length, block.Length);

        var user = Build(U(1, text)).Units[0];
        Assert.EndsWith("[... 10 more characters not stored]", user.UserText);
        var summary = Build(Sum(1, text)).Units[0].Blocks[0].Text;
        Assert.EndsWith("[... 10 more characters not stored]", summary);
    }

    [Fact]
    public void Text_SecretAtCapBoundary_RedactedBeforeCap()
    {
        var secret = new FakeSecrets(4).Get("github-token");
        var raw = Padded(99_990, secret);
        Assert.Equal(99_990, raw.IndexOf(secret, StringComparison.Ordinal));
        var block = Build(A(1, raw)).Units[0].Blocks[0].Text;
        Assert.Contains("more characters not stored]", block);
        Assert.DoesNotContain("[redacted", block);
        AssertNoWindow(block, secret);
        Assert.DoesNotContain(secret[..4], block, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstUserText_IsRedacted()
    {
        var secret = new FakeSecrets(5).Get("github-token");
        var r = Build(A(1, "preamble"), U(2, "my token " + secret + " here"), U(3, "second"));
        Assert.NotNull(r.FirstUserText);
        Assert.Contains("[redacted:token]", r.FirstUserText);
        AssertNoWindow(r.FirstUserText!, secret);
        Assert.Equal(r.Units[1].UserText, r.FirstUserText);
    }

    [Fact]
    public void FirstTs_IsFirstInFileOrder_LastTs_IsLargest()
    {
        var r = Build(U(50, "a"), A(10, "b"), A(90, "c"), A(20, "d"));
        Assert.Equal(At(50), r.FirstTs);
        Assert.Equal(At(90), r.LastTs);

        var skipsNull = Build(U(null, "a"), A(7, "b"));
        Assert.Equal(At(7), skipsNull.FirstTs);
    }
}
