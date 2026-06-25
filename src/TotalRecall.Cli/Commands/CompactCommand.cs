// src/TotalRecall.Cli/Commands/CompactCommand.cs
//
// Plan 5 Task 5.9 — `total-recall compact`. Without `--run`, prints an
// explanation pointing users at the host-side compaction path and the
// CLI-side inspection commands (memory history / memory lineage).
// With `--run`, executes a heuristic hot→warm sweep via HotTierCompactor.

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

    public CompactCommand() { }

    // Explainer-only test seam.
    public CompactCommand(TextWriter output)
    {
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    // --run test seam.
    public CompactCommand(IStore store, TextWriter output,
        double warmThreshold, double decayConstantHours, long nowMs)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _out = output ?? throw new ArgumentNullException(nameof(output));
        _warmThreshold = warmThreshold;
        _decayConstantHours = decayConstantHours;
        _nowMs = nowMs;
    }

    public string Name => "compact";
    public string? Group => null;
    public string Description => "Compact the hot tier (--run for a heuristic sweep; default explains the host-managed path)";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        var output = _out ?? Console.Out;

        if (args.Length > 0 && args[0] == "--run")
        {
            return RunSweep(output);
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
                _warmThreshold, _decayConstantHours, compactionLog: null, reason: "manual_compact");
            output.WriteLine($"compacted: promoted={r.Promoted} carryForward={r.CarryForward}");
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
                compactionLog: null, reason: "manual_compact");
            output.WriteLine($"compacted: promoted={r.Promoted} carryForward={r.CarryForward}");
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
            "Compaction is driven by the host tool's LLM layer (e.g., Claude Code's",
            "compactor subagent) for high-quality summaries. Run '/total-recall:commands",
            "compact' in your host for LLM compaction, or 'total-recall compact --run'",
            "for a fast, deterministic decay-based hot→warm sweep (no LLM).",
            "",
            "To inspect compaction history from the CLI:",
            "  - total-recall memory history        (recent movements)",
            "  - total-recall memory lineage <id>   (ancestry tree)",
        };
        foreach (var line in lines) output.WriteLine(line);
    }
}
