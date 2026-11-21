using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Eval;

namespace TotalRecall.Infrastructure.Transcripts;

internal enum BlockKind { User, Assistant, Stub, Error, Summary }

/// <summary>One rendered block of a unit: <c>User: ...</c>, <c>Assistant: ...</c>, <c>[Bash: ...]</c>, <c>[error: ...]</c> or the summary text.</summary>
internal sealed record UnitBlock(BlockKind Kind, string Text);

/// <summary>
/// One numbered unit (spec section 5 "Units"). <see cref="UserText"/> is the redacted, capped user text of a unit that
/// starts with one; <see cref="UserTurns"/> is 1 for such a unit and 0 otherwise.
/// </summary>
internal sealed record ExchangeUnit(
    int Number,
    long StartOffset,
    DateTimeOffset? FirstTs,
    bool IsSummary,
    string? UserText,
    IReadOnlyList<UnitBlock> Blocks,
    int UserTurns);

internal sealed record BuildResult(
    IReadOnlyList<ExchangeUnit> Units,
    int UserTurns,
    string? FirstUserText,
    DateTimeOffset? FirstTs,
    DateTimeOffset? LastTs);

/// <summary>
/// Folds transcript records, in file order, into numbered units (spec section 5). Redaction runs on the full text before
/// any cut, so a secret that would straddle a limit is already a marker when the limit applies (spec section 4).
/// </summary>
internal static class ExchangeBuilder
{
    public const int StubLimit = 160;
    public const int ErrorLimit = 500;
    public const int TextCap = 100_000;

    public static BuildResult Build(IEnumerable<TranscriptRecord> records, DateTimeOffset? cutoff, int firstUnitNumber)
    {
        ArgumentNullException.ThrowIfNull(records);

        var units = new List<ExchangeUnit>();
        var userTurns = 0;
        string? firstUserText = null;
        DateTimeOffset? firstTs = null;
        DateTimeOffset? lastTs = null;
        var number = firstUnitNumber;
        Open? open = null;

        void Close()
        {
            if (open is null) return;
            units.Add(new ExchangeUnit(number++, open.StartOffset, open.FirstTs, open.IsSummary, open.UserText, open.Blocks, open.UserTurns));
            userTurns += open.UserTurns;
            firstUserText ??= open.UserText;
            open = null;
        }

        foreach (var record in records)
        {
            if (cutoff is { } c && (record.Ts is not { } rts || rts <= c)) continue;

            if (record.Ts is { } ts)
            {
                firstTs ??= ts;
                if (lastTs is null || ts > lastTs) lastTs = ts;
            }

            switch (record.Kind)
            {
                case RecordKind.CompactionBoundary:
                    Close();
                    break;

                case RecordKind.CompactionSummary:
                {
                    var text = Prose(record.Text);
                    if (text.Length == 0) break;
                    Close();
                    open = new Open(record.StartOffset, record.Ts, isSummary: true, userText: null, userTurns: 0);
                    open.Blocks.Add(new UnitBlock(BlockKind.Summary, text));
                    Close();
                    break;
                }

                case RecordKind.UserText:
                {
                    var text = Prose(record.Text);
                    if (text.Length == 0) break;
                    Close();
                    open = new Open(record.StartOffset, record.Ts, isSummary: false, userText: text, userTurns: 1);
                    open.Blocks.Add(new UnitBlock(BlockKind.User, "User: " + text));
                    break;
                }

                case RecordKind.AssistantText:
                {
                    var text = Prose(record.Text);
                    if (text.Length == 0) break;
                    Append(ref open, record, new UnitBlock(BlockKind.Assistant, "Assistant: " + text));
                    break;
                }

                case RecordKind.ToolStub:
                    Append(ref open, record, new UnitBlock(BlockKind.Stub, Stub(record.ToolName, record.Text)));
                    break;

                case RecordKind.ToolError:
                    Append(ref open, record, new UnitBlock(BlockKind.Error, Error(record.Text)));
                    break;
            }
        }

        Close();
        return new BuildResult(units, userTurns, firstUserText, firstTs, lastTs);
    }

    private static void Append(ref Open? open, TranscriptRecord record, UnitBlock block)
    {
        open ??= new Open(record.StartOffset, record.Ts, isSummary: false, userText: null, userTurns: 0);
        open.FirstTs ??= record.Ts;
        open.Blocks.Add(block);
    }

    private static string Prose(string? raw) => TextCut.Cap(SensitiveContentScanner.Redact(raw).Text, TextCap);

    private static string Stub(string? toolName, string? raw)
    {
        var name = string.IsNullOrEmpty(toolName) ? "Tool" : toolName;
        var detail = TextCut.Cut(TextCut.Fold(SensitiveContentScanner.Redact(raw).Text), StubLimit);
        return detail.Length == 0 ? "[" + name + "]" : "[" + name + ": " + detail + "]";
    }

    private static string Error(string? raw)
    {
        var text = TextCut.Cut(SensitiveContentScanner.Redact(raw).Text, ErrorLimit);
        return text.Length == 0 ? "[error]" : "[error: " + text + "]";
    }

    private sealed class Open(long startOffset, DateTimeOffset? firstTs, bool isSummary, string? userText, int userTurns)
    {
        public long StartOffset { get; } = startOffset;
        public DateTimeOffset? FirstTs { get; set; } = firstTs;
        public bool IsSummary { get; } = isSummary;
        public string? UserText { get; } = userText;
        public int UserTurns { get; } = userTurns;
        public List<UnitBlock> Blocks { get; } = new();
    }
}
