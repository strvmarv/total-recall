// src/TotalRecall.Cli/Commands/SessionEndHintCommand.cs
//
// `total-recall session-end-hint --host <claude-code|copilot-cli|cursor>`.
// SessionEnd hook backend. Counts uncompacted hot-tier entries and, when at
// or above tiers.hot.compaction_hint_threshold, emits a USER-FACING
// systemMessage nudging the user to run compaction next session.
//
// SessionEnd output may NOT contain hookSpecificOutput/additionalContext —
// only top-level control fields. systemMessage is valid and user-facing.
// FAIL-SAFE: any error prints "{}" and exits 0.

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Json;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands;

public sealed class SessionEndHintCommand : ICliCommand
{
    private readonly IStore? _store;
    private readonly TextWriter? _out;
    private readonly int? _threshold;

    public SessionEndHintCommand() { }

    public SessionEndHintCommand(IStore store, TextWriter output, int threshold)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _out = output ?? throw new ArgumentNullException(nameof(output));
        _threshold = threshold;
    }

    public string Name => "session-end-hint";
    public string? Group => null;
    public string Description => "SessionEnd hook: emit a user-facing compaction nudge when the hot tier is full";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        var output = _out ?? Console.Out;
        try
        {
            var host = ParseHost(args);
            var threshold = _threshold ?? LoadThreshold();
            var hotCount = CountHot();

            if (hotCount >= threshold && host == "claude-code")
            {
                var msg = $"total-recall: {hotCount} uncompacted hot-tier memory entries. "
                    + "Run /total-recall:commands compact next session to summarize them.";
                var sb = new StringBuilder();
                sb.Append("{\"systemMessage\":");
                JsonWriter.AppendString(sb, msg);
                sb.Append('}');
                output.WriteLine(sb.ToString());
                return 0;
            }

            output.WriteLine("{}");
            return 0;
        }
        catch
        {
            try { output.WriteLine("{}"); } catch { }
            return 0;
        }
    }

    private static string ParseHost(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--host") return args[i + 1];
        return "claude-code";
    }

    private int LoadThreshold()
    {
        var cfg = new ConfigLoader().LoadEffectiveConfig();
        return cfg.Tiers.Hot.CompactionHintThreshold;
    }

    private int CountHot()
    {
        if (_store is not null) return _store.Count(Tier.Hot, ContentType.Memory);

        var dbPath = ConfigLoader.GetDbPath();
        MsSqliteConnection? owned = null;
        try
        {
            owned = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(owned);
            return new SqliteStore(owned).Count(Tier.Hot, ContentType.Memory);
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
