// src/TotalRecall.Cli/Commands/Eval/SnapshotCommand.cs
//
// Plan 5 Task 5.3b — `total-recall eval snapshot <name>`. Loads the
// effective TotalRecallConfig, serializes it to a stable JSON string via
// ConfigJsonSerializer, and asks ConfigSnapshotStore to create (or dedup
// against the latest) a row. Prints the resulting id.

using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Cli.Commands.Eval;

/// <summary>
/// Test seam: given a name, returns (id, wasDedupedAgainstLatest).
/// </summary>
public delegate (string Id, bool Deduped) SnapshotExecutor(string name);

public sealed class SnapshotCommand : ICliCommand
{
    private readonly SnapshotExecutor? _executor;

    public SnapshotCommand() { _executor = null; }

    public SnapshotCommand(SnapshotExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public string Name => "snapshot";
    public string? Group => "eval";
    public string Description => "Create a named config snapshot (dedupes against the latest).";

    public Task<int> RunAsync(string[] args)
    {
        string? name = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"eval snapshot: unknown argument '{a}'");
                PrintUsage(Console.Error);
                return Task.FromResult(2);
            }
            if (name is null) { name = a; continue; }
            Console.Error.WriteLine("eval snapshot: accepts a single <name> positional argument");
            return Task.FromResult(2);
        }

        if (string.IsNullOrEmpty(name))
        {
            Console.Error.WriteLine("eval snapshot: <name> is required");
            PrintUsage(Console.Error);
            return Task.FromResult(2);
        }

        try
        {
            var executor = _executor ?? BuildProductionExecutor();
            var (id, deduped) = executor(name);
            if (deduped)
                Console.Out.WriteLine($"{id} (deduped against latest)");
            else
                Console.Out.WriteLine(id);
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"eval snapshot: failed: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static SnapshotExecutor BuildProductionExecutor()
    {
        return name =>
        {
            var loader = new ConfigLoader();
            var cfg = loader.LoadEffectiveConfig();
            var configJson = ConfigJsonSerializer.Serialize(cfg);

            var dbPath = ConfigLoader.GetDbPath();
            var conn = SqliteConnection.Open(dbPath);
            try
            {
                MigrationRunner.RunMigrations(conn);
                var store = new ConfigSnapshotStore(conn);
                var latest = store.GetLatest();
                var id = store.CreateSnapshot(configJson, name);
                var deduped = latest is not null && latest.Id == id;
                return (id, deduped);
            }
            finally
            {
                conn.Dispose();
            }
        };
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall eval snapshot <name>");
    }
}
