using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TotalRecall.Infrastructure.Eval;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>The session facts a chunk header carries (spec section 5 "Chunk text").</summary>
internal sealed record ChunkHeaderFacts(string Host, string HostSessionId, string ProjectKey, string Kind);

/// <summary>
/// Turns one unit into chunk rows (spec section 5 "Chunk text" and "Size"): a header line, a blank line and a body of at
/// most <see cref="BodyLimit"/> characters. Blocks are packed greedily; a block that fits a fresh part but not the part
/// it would join starts the next part, and a block longer than that is split on paragraph, then line, then whitespace
/// boundaries, as a last resort at <see cref="TextCut.SafeLength"/>. A chunk never spans units.
/// </summary>
internal static class TranscriptChunker
{
    public const int BodyLimit = 1_800;
    public const int RepeatLimit = 300;

    private const string Separator = "\n\n";
    private const string ContinuedLabel = "User (continued): ";

    public static IReadOnlyList<IngestChunk> Chunk(ExchangeUnit unit, ChunkHeaderFacts facts)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(facts);

        var continued = unit.UserText is null
            ? ""
            : ContinuedLabel + TextCut.Cut(TextCut.Fold(unit.UserText), RepeatLimit) + Separator;
        var freshCapacity = BodyLimit - continued.Length;

        var parts = new List<Part> { new("") };

        foreach (var block in unit.Blocks)
        {
            // Trailing whitespace is neither counted nor kept: it would push a last word into a part of its own.
            var text = block.Text.TrimEnd();
            if (text.Length == 0) continue;
            var isError = block.Kind == BlockKind.Error;

            var current = parts[^1];
            if (current.Length + current.SeparatorBefore + text.Length <= BodyLimit)
            {
                current.Add(text, isError);
                continue;
            }

            if (current.HasBlocks && text.Length <= freshCapacity)
            {
                var next = new Part(continued);
                next.Add(text, isError);
                parts.Add(next);
                continue;
            }

            var rest = text;
            while (rest.Length > 0)
            {
                current = parts[^1];
                var capacity = BodyLimit - current.Length - current.SeparatorBefore;
                if (capacity <= 0)
                {
                    parts.Add(new Part(continued));
                    continue;
                }

                var (piece, remainder, hardCut) = Take(rest, capacity);
                if (hardCut && current.HasBlocks)
                {
                    // A block boundary is available and ranks above the last resort: never cut through a label,
                    // a marker or a pair to squeeze a few characters into a part that already holds blocks.
                    parts.Add(new Part(continued));
                    continue;
                }

                if (piece.Length == 0)
                {
                    // Unreachable with the real limits (a marker is far shorter than a fresh part).
                    throw new InvalidOperationException("The chunker cannot cut a block inside an empty part.");
                }

                if (string.IsNullOrWhiteSpace(piece)) piece = "";
                if (string.IsNullOrWhiteSpace(remainder)) remainder = "";
                if (piece.Length == 0)
                {
                    rest = remainder;
                    continue;
                }

                current.Add(piece, isError);
                rest = remainder;
                if (rest.Length > 0) parts.Add(new Part(continued));
            }
        }

        var header = Header(unit, facts);
        var ts = unit.FirstTs is { } first ? TranscriptTime.Iso(first) : null;
        var chunkType = unit.IsSummary ? "compaction-summary" : "dialogue";
        var chunks = new List<IngestChunk>(parts.Count);
        foreach (var part in parts)
        {
            if (!part.HasBlocks) continue;
            chunks.Add(new IngestChunk(
                unit.Number,
                unit.Number,
                chunks.Count,
                chunkType,
                part.HasError,
                ts,
                SensitiveContentScanner.RedactionVersion,
                header + Separator + part.ToString()));
        }

        return chunks;
    }

    private static string Header(ExchangeUnit unit, ChunkHeaderFacts facts)
    {
        var id = facts.HostSessionId;
        if (id.Length > 8) id = id[..TextCut.SafeLength(id, 8)];
        var date = unit.FirstTs is { } ts
            ? ts.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "unknown";
        var number = unit.Number.ToString(CultureInfo.InvariantCulture);
        return $"{facts.Host} session {id} | project {facts.ProjectKey} | {date} | {facts.Kind} | "
            + $"{(unit.IsSummary ? "compaction-summary" : "dialogue")} | exchanges {number}-{number}";
    }

    /// <summary>
    /// The longest piece of <paramref name="text"/> of at most <paramref name="capacity"/> characters that ends at a
    /// paragraph, else a line, else a whitespace boundary, else (last resort) at the safe length; and what remains.
    /// The whitespace at the boundary belongs to neither.
    /// </summary>
    private static (string Piece, string Remainder, bool HardCut) Take(string text, int capacity)
    {
        if (text.Length <= capacity) return (text, "", false);

        var limit = TextCut.SafeLength(text, capacity);

        // Paragraph: a blank line, at index i <= limit, so that text[..i] fits.
        for (var i = Math.Min(limit, text.Length - 2); i >= 1; i--)
        {
            if (text[i] != '\n') continue;
            var next = i + 1;
            if (text[next] == '\r' && next + 1 < text.Length) next++;
            if (text[next] != '\n') continue;
            if (TryBreak(text, i, SkipNewlines(text, i), out var result)) return result;
        }

        // Line: a single line break at index i <= limit.
        for (var i = Math.Min(limit, text.Length - 1); i >= 1; i--)
        {
            if (text[i] == '\n' && TryBreak(text, i, i + 1, out var result)) return result;
        }

        // Whitespace: the last whitespace character at index i <= limit, and the run it belongs to.
        for (var i = Math.Min(limit, text.Length - 1); i >= 1; i--)
        {
            if (char.IsWhiteSpace(text[i]) && TryBreak(text, i, SkipWhitespace(text, i), out var result)) return result;
        }

        // The last resort: the safe length itself, which can be 0 (the piece is then empty and the caller opens a part).
        return (text[..limit], text[limit..], true);
    }

    private static bool TryBreak(string text, int end, int restStart, out (string Piece, string Remainder, bool HardCut) result)
    {
        var piece = text[..end].TrimEnd();
        if (piece.Length == 0 || restStart >= text.Length)
        {
            result = default;
            return false;
        }

        result = (piece, text[restStart..], false);
        return true;
    }

    private static int SkipNewlines(string text, int from)
    {
        while (from < text.Length && (text[from] == '\n' || text[from] == '\r')) from++;
        return from;
    }

    private static int SkipWhitespace(string text, int from)
    {
        while (from < text.Length && char.IsWhiteSpace(text[from])) from++;
        return from;
    }

    private sealed class Part
    {
        private readonly StringBuilder _body;

        public Part(string prefix) => _body = new StringBuilder(prefix);

        public bool HasBlocks { get; private set; }
        public bool HasError { get; private set; }
        public int Length => _body.Length;
        public int SeparatorBefore => HasBlocks ? Separator.Length : 0;

        public void Add(string text, bool isError)
        {
            if (HasBlocks) _body.Append(Separator);
            _body.Append(text);
            HasBlocks = true;
            if (isError) HasError = true;
        }

        public override string ToString() => _body.ToString();
    }
}
