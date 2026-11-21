using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class TranscriptChunkerTests
{
    private const string ContinuedLabel = "User (continued): ";
    private static readonly DateTimeOffset Ts = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly ChunkHeaderFacts Facts = new("claude-code", "0123456789abcdef", "owner/repo", "interactive");
    private static readonly string[] Markers =
    {
        "[redacted:token]", "[redacted:private-key]", "[redacted:bearer]", "[redacted:url-credentials]",
        "[redacted:secret-assignment]", "[redacted:unprocessable]",
    };

    private static UnitBlock Asst(string text) => new(BlockKind.Assistant, "Assistant: " + text);
    // A block with no label, so a long word in it has no whitespace to split at.
    private static UnitBlock Plain(string text, BlockKind kind = BlockKind.Summary) => new(kind, text);
    private static UnitBlock Err(string text) => new(BlockKind.Error, "[error: " + text + "]");

    private static ExchangeUnit Unit(int number, string? userText, params UnitBlock[] blocks) =>
        new(number, 0, Ts, false, userText, blocks, userText is null ? 0 : 1);

    private static ExchangeUnit AssistantOnly(params UnitBlock[] blocks) => Unit(4, null, blocks);

    private static string Body(IngestChunk chunk)
    {
        var split = chunk.Content.IndexOf("\n\n", StringComparison.Ordinal);
        return chunk.Content[(split + 2)..];
    }

    private static string Header(IngestChunk chunk) => chunk.Content[..chunk.Content.IndexOf("\n\n", StringComparison.Ordinal)];

    [Fact]
    public void SmallUnit_OnePart_ExactContent()
    {
        var unit = new ExchangeUnit(4, 0, Ts, false, "Fix the bug", new[]
        {
            new UnitBlock(BlockKind.User, "User: Fix the bug"),
            Asst("Done."),
            new UnitBlock(BlockKind.Stub, "[Bash: git status]"),
            Err("boom"),
        }, 1);

        var chunks = TranscriptChunker.Chunk(unit, Facts);

        var chunk = Assert.Single(chunks);
        Assert.Equal(
            "claude-code session 01234567 | project owner/repo | 2026-11-19 | interactive | dialogue | exchanges 4-4"
            + "\n\nUser: Fix the bug\n\nAssistant: Done.\n\n[Bash: git status]\n\n[error: boom]",
            chunk.Content);
        Assert.Equal(4, chunk.ExchangeStart);
        Assert.Equal(4, chunk.ExchangeEnd);
        Assert.Equal(0, chunk.Part);
        Assert.Equal("dialogue", chunk.ChunkType);
        Assert.True(chunk.HasError);
        Assert.Equal(TranscriptTime.Iso(Ts), chunk.Ts);
        Assert.Equal(SensitiveContentScanner.RedactionVersion, chunk.RedactionVersion);
        Assert.Equal(1, chunk.RedactionVersion);
    }

    [Fact]
    public void SummaryUnit_NoLabels_TypeCompactionSummary()
    {
        var unit = new ExchangeUnit(2, 0, Ts, true, null,
            new[] { new UnitBlock(BlockKind.Summary, "First paragraph.\n\nSecond paragraph.") }, 0);

        var chunk = Assert.Single(TranscriptChunker.Chunk(unit, Facts));

        Assert.Equal("compaction-summary", chunk.ChunkType);
        Assert.False(chunk.HasError);
        Assert.Equal(
            "claude-code session 01234567 | project owner/repo | 2026-11-19 | interactive | compaction-summary | exchanges 2-2"
            + "\n\nFirst paragraph.\n\nSecond paragraph.",
            chunk.Content);
    }

    [Fact]
    public void UnknownDate_HeaderSaysUnknown_TsNull()
    {
        var unit = new ExchangeUnit(0, 0, null, false, null, new[] { Asst("hi") }, 0);

        var chunk = Assert.Single(TranscriptChunker.Chunk(unit, Facts));

        Assert.Equal("claude-code session 01234567 | project owner/repo | unknown | interactive | dialogue | exchanges 0-0", Header(chunk));
        Assert.Null(chunk.Ts);
    }

    [Fact]
    public void Date_IsTheUtcDateOfTheFirstTimestamp()
    {
        var local = new DateTimeOffset(2026, 11, 19, 23, 30, 0, TimeSpan.FromHours(-5));
        var unit = new ExchangeUnit(0, 0, local, false, null, new[] { Asst("hi") }, 0);

        var chunk = Assert.Single(TranscriptChunker.Chunk(unit, Facts));

        Assert.Contains(" | 2026-11-20 | ", Header(chunk));
        Assert.Equal(TranscriptTime.Iso(local), chunk.Ts);
        Assert.StartsWith("2026-11-20T04:30:00", chunk.Ts);
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("12345678", "12345678")]
    [InlineData("123456789", "12345678")]
    public void ShortId_UsedWhole(string id, string shown)
    {
        var chunk = Assert.Single(TranscriptChunker.Chunk(AssistantOnly(Asst("hi")), Facts with { HostSessionId = id }));

        Assert.StartsWith("claude-code session " + shown + " | project ", chunk.Content);
    }

    [Fact]
    public void BodyExactly1800_IsOnePart()
    {
        var exact = new string('a', 1800);
        var over = exact + "b";

        var one = TranscriptChunker.Chunk(AssistantOnly(Plain(exact)), Facts);
        var two = TranscriptChunker.Chunk(AssistantOnly(Plain(over)), Facts);

        Assert.Equal(1800, Body(Assert.Single(one)).Length);
        Assert.Equal(2, two.Count);
        Assert.Equal(1800, Body(two[0]).Length);
        Assert.Equal("b", Body(two[1]));
    }

    [Fact]
    public void LongUnit_SplitsAtBlockBoundaries_EveryBodyAtMost1800()
    {
        var blocks = Enumerable.Range(1, 5).Select(i => Asst(new string((char)('a' + i), 689))).ToArray();
        var unit = Unit(7, "Q", new[] { new UnitBlock(BlockKind.User, "User: Q") }.Concat(blocks).ToArray());

        var chunks = TranscriptChunker.Chunk(unit, Facts);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("User: Q\n\n" + blocks[0].Text + "\n\n" + blocks[1].Text, Body(chunks[0]));
        Assert.Equal(ContinuedLabel + "Q\n\n" + blocks[2].Text + "\n\n" + blocks[3].Text, Body(chunks[1]));
        Assert.Equal(ContinuedLabel + "Q\n\n" + blocks[4].Text, Body(chunks[2]));
        Assert.All(chunks, c => Assert.True(Body(c).Length <= TranscriptChunker.BodyLimit));
    }

    [Fact]
    public void LaterParts_StartWithUserContinued_CutTo300()
    {
        var user = "alpha\nbeta\n" + new string('z', 400) + "\n\n" + string.Join(" ", Enumerable.Repeat("word", 700));
        var unit = Unit(1, user, new UnitBlock(BlockKind.User, "User: " + user));

        var chunks = TranscriptChunker.Chunk(unit, Facts);

        Assert.True(chunks.Count >= 3);
        Assert.StartsWith("User: alpha\nbeta\n", Body(chunks[0]));
        var expectedLine = ContinuedLabel + "alpha beta " + new string('z', 289) + "...";
        Assert.Equal(ContinuedLabel.Length + 303, expectedLine.Length);
        foreach (var later in chunks.Skip(1))
        {
            var body = Body(later);
            Assert.StartsWith(expectedLine + "\n\n", body);
            Assert.DoesNotContain('\n', body[..expectedLine.Length]);
            Assert.True(body.Length <= TranscriptChunker.BodyLimit);
            Assert.True(body.Length > expectedLine.Length + 2);
        }
    }

    [Fact]
    public void AssistantOnlyUnit_RepeatsNothing()
    {
        var unit = AssistantOnly(Plain(new string('a', 2500)), Plain(new string('b', 2500)));

        var chunks = TranscriptChunker.Chunk(unit, Facts);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.DoesNotContain("continued", c.Content));
        Assert.Equal(new string('a', 1800), Body(chunks[0]));
    }

    public static TheoryData<string, string[]> BoundaryRows()
    {
        var a = new string('a', 100) + "\n" + new string('a', 100) + " " + new string('a', 100);
        var b1 = new string('b', 60) + "\n" + new string('b', 1639);
        var b2 = new string('b', 50) + " " + new string('b', 1650);
        var lineA = new string('a', 100) + " " + new string('a', 100);
        var wsA = new string('a', 989);
        var data = new TheoryData<string, string[]>
        {
            // paragraph wins over a later line break and a later space
            { "Assistant: " + a + "\n\n" + b1, new[] { "Assistant: " + a, b1 } },
            // line break wins over a later space
            { "Assistant: " + lineA + "\n" + b2, new[] { "Assistant: " + lineA, b2 } },
            // whitespace wins over a hard cut at the limit
            { "Assistant: " + wsA + " " + new string('b', 50) + " " + new string('c', 1650),
              new[] { "Assistant: " + wsA + " " + new string('b', 50), new string('c', 1650) } },
            // a single long word is cut at the limit
            { new string('a', 2500), new[] { new string('a', 1800), new string('a', 700) } },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(BoundaryRows))]
    public void LongBlock_SplitsOnParagraphThenLineThenWhitespaceThenHard(string block, string[] expectedBodies)
    {
        var chunks = TranscriptChunker.Chunk(AssistantOnly(new UnitBlock(BlockKind.Assistant, block)), Facts);

        Assert.Equal(expectedBodies, chunks.Select(Body).ToArray());
        Assert.All(chunks, c => Assert.True(Body(c).Length <= TranscriptChunker.BodyLimit));
    }

    [Fact]
    public void LongBlock_UsesCapacityLeftInPart()
    {
        var first = Plain(new string('p', 1000));
        var second = Plain(new string('q', 3000));       // longer than a fresh part

        var chunks = TranscriptChunker.Chunk(AssistantOnly(first, second), Facts);

        // 1,800 - 1,000 - 2 (the blank line) = 798 characters of the second block fit in the first part.
        Assert.Equal(3, chunks.Count);
        Assert.Equal(first.Text + "\n\n" + new string('q', 798), Body(chunks[0]));
        Assert.Equal(1800, Body(chunks[0]).Length);
        Assert.Equal(new string('q', 1800), Body(chunks[1]));
        Assert.Equal(new string('q', 3000 - 798 - 1800), Body(chunks[2]));
    }

    [Fact]
    public void LongBlock_AfterContinuedLine_CountsTheLineInTheCapacity()
    {
        var unit = Unit(3, "Q", Plain(new string('a', 1500)), Plain(new string('b', 5000)));

        var chunks = TranscriptChunker.Chunk(unit, Facts);

        Assert.All(chunks, c => Assert.True(Body(c).Length <= TranscriptChunker.BodyLimit));
        Assert.All(chunks.Skip(1), c => Assert.StartsWith(ContinuedLabel + "Q\n\n", Body(c)));
        Assert.Equal(1800, Body(chunks[1]).Length);
    }

    [Fact]
    public void Chunk_HardSplit_NeverInsideMarkerOrSurrogate()
    {
        const string pair = "\U0001F600";
        var marker = new Regex(@"\[redacted:[a-z-]+\]");

        for (var pad = 0; pad < 60; pad++)
        {
            var word = new StringBuilder(new string('x', pad));
            var i = 0;
            while (word.Length < 5000)
            {
                word.Append(i % 3 == 0 ? Markers[i % Markers.Length] : pair);
                i++;
            }

            var chunks = TranscriptChunker.Chunk(AssistantOnly(Plain(word.ToString())), Facts);

            Assert.True(chunks.Count >= 3);
            foreach (var chunk in chunks)
            {
                var body = Body(chunk);
                Assert.True(body.Length <= TranscriptChunker.BodyLimit);
                Assert.False(char.IsHighSurrogate(body[^1]), $"pad {pad}: part ends on a high surrogate");
                Assert.False(char.IsLowSurrogate(body[0]), $"pad {pad}: part starts on a low surrogate");
                Assert.Equal(body.Split("[redacted:").Length - 1, marker.Matches(body).Count);
                Assert.False(body.StartsWith("token]", StringComparison.Ordinal) || body.EndsWith("[", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void HasError_OnlyOnPartsWithAnErrorPiece()
    {
        var spread = TranscriptChunker.Chunk(
            AssistantOnly(Asst(new string('a', 1739)), Asst(new string('b', 1739)), Err(new string('e', 100))), Facts);

        Assert.Equal(3, spread.Count);
        Assert.Equal(new[] { false, false, true }, spread.Select(c => c.HasError).ToArray());

        var pieces = TranscriptChunker.Chunk(
            AssistantOnly(Plain(new string('e', 4000), BlockKind.Error), Plain(new string('a', 1800), BlockKind.Assistant)), Facts);

        Assert.Equal(new[] { true, true, true, false }, pieces.Select(c => c.HasError).ToArray());
    }

    [Fact]
    public void Parts_Numbered0ToN_SameExchangeNumbers_EachWithHeader()
    {
        var unit = Unit(9, "Q", Plain(new string('a', 1700)), Plain(new string('b', 1700)), Plain(new string('c', 1700)));

        var chunks = TranscriptChunker.Chunk(unit, Facts);

        Assert.True(chunks.Count >= 3);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Part));
        Assert.All(chunks, c =>
        {
            Assert.Equal(9, c.ExchangeStart);
            Assert.Equal(9, c.ExchangeEnd);
            Assert.Equal("dialogue", c.ChunkType);
            Assert.Equal(TranscriptTime.Iso(Ts), c.Ts);
            Assert.StartsWith(
                "claude-code session 01234567 | project owner/repo | 2026-11-19 | interactive | dialogue | exchanges 9-9\n\n",
                c.Content);
        });
    }

    private static string Squash(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static string NoLabels(IReadOnlyList<IngestChunk> chunks, bool hasUser)
    {
        var sb = new StringBuilder();
        foreach (var chunk in chunks)
        {
            var body = Body(chunk);
            if (hasUser && chunk.Part > 0)
            {
                Assert.StartsWith(ContinuedLabel, body);
                body = body[(body.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
            }

            sb.Append(body);
        }

        return Squash(sb.ToString());
    }

    private static string RandomText(Random rng)
    {
        var sb = new StringBuilder();
        var target = rng.Next(1, 7000);
        while (sb.Length < target)
        {
            switch (rng.Next(12))
            {
                case 0: sb.Append("\n\n"); break;
                case 1: sb.Append('\n'); break;
                case 2: sb.Append("\r\n"); break;
                case 3: sb.Append(Markers[rng.Next(Markers.Length)]); break;
                case 4: sb.Append("\U0001F600"); break;
                case 5: sb.Append(new string('w', rng.Next(1, 2500))); break;
                case 6: sb.Append("  \t "); break;
                default:
                    sb.Append(new string((char)('a' + rng.Next(26)), rng.Next(1, 25))).Append(' ');
                    break;
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void NoTextLost()
    {
        var rng = new Random(20261121);
        for (var round = 0; round < 400; round++)
        {
            var user = rng.Next(3) == 0 ? null : RandomText(rng);
            var blocks = new List<UnitBlock>();
            if (user is not null) blocks.Add(new UnitBlock(BlockKind.User, "User: " + user));
            for (var n = rng.Next(0, 5); n > 0; n--)
            {
                var kind = rng.Next(3);
                var text = RandomText(rng);
                blocks.Add(kind == 0 ? Err(text) : kind == 1 ? new UnitBlock(BlockKind.Stub, "[Bash: " + text + "]") : Asst(text));
            }

            if (blocks.Count == 0) blocks.Add(Asst("x"));
            var unit = new ExchangeUnit(round, 0, Ts, false, user, blocks, user is null ? 0 : 1);

            var chunks = TranscriptChunker.Chunk(unit, Facts);

            var expected = Squash(string.Join("\n\n", blocks.Select(b => b.Text)));
            Assert.Equal(expected, NoLabels(chunks, user is not null));
            Assert.All(chunks, c => Assert.True(Body(c).Length <= TranscriptChunker.BodyLimit, $"round {round}"));
            Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Part));
        }
    }
}
