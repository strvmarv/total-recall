using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class ExchangeTextTests
{
    private static readonly DateTimeOffset Ts = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly ChunkHeaderFacts Facts = new("claude-code", "0123456789abcdef", "owner/repo", "interactive");

    private static IReadOnlyList<ChunkRow> Rows(IReadOnlyList<IngestChunk> chunks) =>
        chunks.Select((c, i) => new ChunkRow(
            i + 1, 1, "claude-code:0123456789abcdef", "owner/repo", "interactive", null, c.Ts, c.ChunkType, c.HasError,
            c.ExchangeStart, c.ExchangeEnd, c.Content)).ToList();

    private static ChunkRow Row(string content, string chunkType = "dialogue") =>
        new(1, 1, "claude-code:0123456789abcdef", "owner/repo", "interactive", null, null, chunkType, false, 4, 4, content);

    private static string Body(string content) => TranscriptSearchService.ChunkText(content);

    [Fact]
    public void Join_SinglePart_CutsHeaderOnly()
    {
        var row = Row("hdr line\n\nUser: hi\n\nAssistant: hello");

        Assert.Equal("User: hi\n\nAssistant: hello", ExchangeText.Join(new[] { row }));
    }

    [Fact]
    public void Join_MultiPart_RemovesHeadersAndTheRepeatedQuestion()
    {
        var unit = new ExchangeUnit(4, 0, Ts, false, "Why is the sky blue?", new[]
        {
            new UnitBlock(BlockKind.User, "User: Why is the sky blue?"),
            new UnitBlock(BlockKind.Assistant, "Assistant: " + string.Join(" ", Enumerable.Repeat("scatter", 700))),
        }, 1);
        var rows = Rows(TranscriptChunker.Chunk(unit, Facts));
        Assert.True(rows.Count >= 2);

        var prefix = TranscriptChunker.ContinuedLabel + "Why is the sky blue?" + "\n\n";
        var expected = Body(rows[0].Content);
        for (var i = 1; i < rows.Count; i++)
        {
            var body = Body(rows[i].Content);
            Assert.StartsWith(prefix, body, StringComparison.Ordinal);
            expected += "\n\n" + body[prefix.Length..];
        }

        var text = ExchangeText.Join(rows);

        Assert.Equal(expected, text);
        Assert.DoesNotContain(TranscriptChunker.ContinuedLabel, text, StringComparison.Ordinal);
        Assert.DoesNotContain("exchanges 4-4", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Join_CompactionSummary_StartingWithUserColon_KeepsLaterPartAsStored()
    {
        var unit = new ExchangeUnit(2, 0, Ts, true, null, new[]
        {
            new UnitBlock(BlockKind.Summary, "User: " + string.Join(" ", Enumerable.Repeat("alpha", 500))),
            new UnitBlock(BlockKind.Summary, TranscriptChunker.ContinuedLabel + "question?\n\n" + string.Join(" ", Enumerable.Repeat("beta", 500))),
        }, 0);
        var rows = Rows(TranscriptChunker.Chunk(unit, Facts));
        Assert.True(rows.Count >= 2);
        Assert.All(rows, r => Assert.Equal("compaction-summary", r.ChunkType));
        Assert.StartsWith("User: ", Body(rows[0].Content), StringComparison.Ordinal);

        var expected = string.Join("\n\n", rows.Select(r => Body(r.Content)));

        Assert.Equal(expected, ExchangeText.Join(rows));
        Assert.Contains(TranscriptChunker.ContinuedLabel, ExchangeText.Join(rows), StringComparison.Ordinal);
    }

    [Fact]
    public void Join_DialogueSecondPartLabelWithFirstPartNotUser_KeepsLaterPartAsStored()
    {
        var rows = new[]
        {
            Row("h\n\nAssistant: first"),
            Row("h\n\n" + TranscriptChunker.ContinuedLabel + "q\n\nAssistant: second"),
        };

        Assert.Equal("Assistant: first\n\n" + TranscriptChunker.ContinuedLabel + "q\n\nAssistant: second", ExchangeText.Join(rows));
    }

    [Fact]
    public void Join_ExchangeWithNoUserText_KeepsLaterParts()
    {
        var unit = new ExchangeUnit(4, 0, Ts, false, null, new[]
        {
            new UnitBlock(BlockKind.Assistant, "Assistant: " + string.Join(" ", Enumerable.Repeat("gamma", 700))),
        }, 0);
        var rows = Rows(TranscriptChunker.Chunk(unit, Facts));
        Assert.True(rows.Count >= 2);

        var expected = string.Join("\n\n", rows.Select(r => Body(r.Content)));

        Assert.Equal(expected, ExchangeText.Join(rows));
    }

    [Fact]
    public void Join_HeaderOnlyChunk_KeepsWholeContentAsChunkTextDoes()
    {
        var row = Row("only a header line, no blank line");

        Assert.Equal("only a header line, no blank line", Body(row.Content));
        Assert.Equal("only a header line, no blank line", ExchangeText.Join(new[] { row }));
    }

    [Fact]
    public void Join_EmptyList_Empty() => Assert.Equal("", ExchangeText.Join(Array.Empty<ChunkRow>()));

    [Fact]
    public void Tokens_RoundsUp()
    {
        Assert.Equal(0, ExchangeText.Tokens(""));
        Assert.Equal(1, ExchangeText.Tokens("a"));
        Assert.Equal(1, ExchangeText.Tokens("abcd"));
        Assert.Equal(2, ExchangeText.Tokens("abcde"));
        Assert.Equal(2_000, ExchangeText.Tokens(new string('x', 8_000)));
    }
}
