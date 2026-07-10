// src/TotalRecall.Cli/Commands/CompactCommand.cs
//
// Plan 5 Task 5.9 — `total-recall compact`. Without `--run`, prints an
// explanation pointing users at the host-side compaction path and the
// CLI-side inspection commands (memory history / memory lineage).
// With `--run`, executes a heuristic hot→warm sweep via HotTierCompactor.
//
// Task 8 (tier model v2, compaction fast/deep split): `--run` (and the
// no-args explainer) describe the FAST path — the deterministic decay
// sweep below, which is now the default because it never chokes on
// large memories (HotTierCompactor skips sticky rows and oversized
// content). `--deep` does NOT run an in-CLI LLM call; it just prints
// guidance that deep, LLM-judged compaction is host-orchestrated via the
// `compactor` subagent / `session_end`.

using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands;

public sealed class CompactCommand : ICliCommand
{
    private readonly TextWriter? _out;
    private readonly IStore? _store;
    private readonly double _warmThreshold;
    private readonly double _decayConstantHours;
    private readonly long? _nowMs;
    private readonly int _maxContentChars = int.MaxValue;

    public CompactCommand() { }

    // Explainer-only test seam.
    public CompactCommand(TextWriter output)
    {
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    // --run test seam.
    public CompactCommand(IStore store, TextWriter output,
        double warmThreshold, double decayConstantHours, long nowMs,
        int maxContentChars = int.MaxValue)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _out = output ?? throw new ArgumentNullException(nameof(output));
        _warmThreshold = warmThreshold;
        _decayConstantHours = decayConstantHours;
        _nowMs = nowMs;
        _maxContentChars = maxContentChars;
    }

    public string Name => "compact";
    public string? Group => null;
    public string Description =>
        "Compact the hot tier (--run for the fast heuristic sweep, the default path; " +
        "--deep for guidance on host-orchestrated LLM compaction)";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        var output = _out ?? Console.Out;

        if (args.Length > 0 && args[0] == "--run")
        {
            return RunSweep(output);
        }
        if (args.Length > 0 && args[0] == "--deep")
        {
            PrintDeepGuidance(output);
            return 0;
        }
        if (args.Length > 0)
        {
            Console.Error.WriteLine($"compact: unknown argument '{args[0]}'");
            return 2;
        }
        PrintExplainer(output);
        return 0;
    }

    private int RunSweep(TextWriter output)
    {
        var nowMs = _nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (_store is not null)
        {
            var r = HotTierCompactor.Compact(_store, "cli-compact", nowMs,
                _warmThreshold, _decayConstantHours, compactionLog: null, reason: "manual_compact",
                maxContentChars: _maxContentChars);
            output.WriteLine($"compacted: compacted={r.Compacted} carryForward={r.CarryForward}");
            return 0;
        }

        var cfg = new ConfigLoader().LoadEffectiveConfig();
        var dbPath = ConfigLoader.GetDbPath();
        MsSqliteConnection? owned = null;
        try
        {
            owned = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(owned);
            // SqliteStore(connection) does not own the connection (_ownsConnection
            // == false), so its Dispose is a no-op; disposing `owned` here releases
            // the only owned resource. Matches PinnedFloorCommand.RenderBlock.
            var store = new SqliteStore(owned);
            var r = HotTierCompactor.Compact(store, "cli-compact", nowMs,
                cfg.Compaction.WarmThreshold, cfg.Compaction.DecayHalfLifeHours,
                compactionLog: null, reason: "manual_compact",
                maxContentChars: cfg.Tiers.Hot.MaxContentChars);
            output.WriteLine($"compacted: compacted={r.Compacted} carryForward={r.CarryForward}");
            return 0;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static void PrintExplainer(TextWriter output)
    {
        var lines = new[]
        {
            "'total-recall compact --run' performs a FAST, deterministic decay-based",
            "hot→warm sweep (no LLM) — this is the default compaction path. It never",
            "compacts sticky-hot (pinned) rows and skips rows over the hot char cap",
            "instead of choking on them.",
            "",
            "For DEEP, LLM-judged compaction (grouping, summarization, drift checks),",
            "run 'total-recall compact --deep' for guidance, or use the host tool's",
            "compactor subagent / session_end path (e.g. '/total-recall:commands compact'",
            "in Claude Code).",
            "",
            "To inspect compaction history from the CLI:",
            "  - total-recall memory history        (recent movements)",
            "  - total-recall memory lineage <id>   (ancestry tree)",
        };
        foreach (var line in lines) output.WriteLine(line);
    }

    private static void PrintDeepGuidance(TextWriter output)
    {
        var lines = new[]
        {
            "Deep compaction is host-orchestrated — this CLI does not make LLM calls.",
            "It groups related hot entries, generates summaries, and checks semantic",
            "drift before compacting, via:",
            "  - the 'compactor' subagent (see agents/compactor.md), or",
            "  - the session_end MCP tool, which the host invokes at session end.",
            "",
            "In Claude Code, run '/total-recall:commands compact' to trigger it.",
            "",
            "For a fast, no-LLM sweep right now, run 'total-recall compact --run'.",
        };
        foreach (var line in lines) output.WriteLine(line);
    }
}
