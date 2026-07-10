// src/TotalRecall.Cli/Commands/PinnedFloorCommand.cs
//
// `total-recall pinned-floor --host <claude-code|copilot-cli|cursor>`.
// Per-turn UserPromptSubmit hook backend. Reads the host hook payload from
// stdin, decides via the adaptive throttle, and on an "inject" turn renders the
// pinned block live from the DB and emits host-correct additionalContext JSON.
// FAIL-SAFE: any error prints "{}" and exits 0 — a UserPromptSubmit hook must
// never block or reject the user's prompt.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Json;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands;

public sealed class PinnedFloorCommand : ICliCommand
{
    public const string ReminderPreamble =
        "(Reminder) The following pinned directives remain in effect for this session:";

    private readonly IStore? _store;
    private readonly string? _stateDir;
    private readonly TextReader? _in;
    private readonly TextWriter? _out;
    private readonly FloorThresholds? _thresholds;
    private readonly Func<string, long?>? _sizer;
    private readonly bool? _projectScoping;
    private readonly ProjectResolver? _projectResolver;

    public PinnedFloorCommand() { }

    public PinnedFloorCommand(
        IStore store, string stateDir, TextReader input, TextWriter output,
        FloorThresholds thresholds, Func<string, long?> transcriptSizer,
        bool? projectScoping = null,
        ProjectResolver? projectResolver = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _stateDir = stateDir ?? throw new ArgumentNullException(nameof(stateDir));
        _in = input ?? throw new ArgumentNullException(nameof(input));
        _out = output ?? throw new ArgumentNullException(nameof(output));
        _thresholds = thresholds;
        _sizer = transcriptSizer ?? throw new ArgumentNullException(nameof(transcriptSizer));
        _projectScoping = projectScoping;
        _projectResolver = projectResolver;
    }

    public string Name => "pinned-floor";
    public string? Group => null;
    public string Description => "UserPromptSubmit hook: re-assert pinned directives on an adaptive throttle";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        var output = _out ?? Console.Out;
        try
        {
            var host = ParseHost(args);
            var input = (_in ?? Console.In).ReadToEnd();
            var (sessionId, transcriptPath, cwd) = ParsePayload(input);

            var stateDir = _stateDir ?? PinnedFloorState.DefaultStateDir();
            var thresholds = _thresholds ?? LoadThresholds();

            PinnedFloorState.Prune(stateDir, maxAgeDays: 3, nowUtc: DateTimeOffset.UtcNow);

            var state = PinnedFloorState.Load(stateDir, sessionId);
            long? bytes = transcriptPath is null ? null : SizeOf(transcriptPath);
            var (verdict, next) = PinnedFloorDecider.Decide(state, new FloorSignal(bytes), thresholds);

            var threshold = LoadCompactionThreshold();
            var hotCount = CountHot();
            var nudge = CompactionNudge.TryTake(stateDir, sessionId, hotCount, threshold);

            if (verdict == FloorVerdict.Inject)
            {
                var block = RenderBlock(cwd);
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(block)) parts.Add(ReminderPreamble + "\n\n" + block);
                if (nudge is not null) parts.Add(nudge);

                if (parts.Count > 0)
                {
                    PinnedFloorState.Save(stateDir, next);
                    output.WriteLine(EnvelopeForHost(host, string.Join("\n\n", parts)));
                    return 0;
                }
            }
            else if (nudge is not null)
            {
                // Skip turn, but the once-per-session nudge is due: emit it alone.
                PinnedFloorState.Save(stateDir, next);
                output.WriteLine(EnvelopeForHost(host, nudge));
                return 0;
            }

            PinnedFloorState.Save(stateDir, next);
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

    private static (string SessionId, string? TranscriptPath, string? Cwd) ParsePayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ("unknown-session", null, null);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var session = GetString(r, "session_id") ?? GetString(r, "sessionId") ?? "unknown-session";
        var transcript = GetString(r, "transcript_path") ?? GetString(r, "transcriptPath");
        var cwd = GetString(r, "cwd") ?? GetString(r, "workingDirectory");
        return (session, transcript, cwd);
    }

    private static string? GetString(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private long? SizeOf(string path)
    {
        if (_sizer is not null) return _sizer(path);
        try { return File.Exists(path) ? new FileInfo(path).Length : (long?)null; }
        catch { return null; }
    }

    private FloorThresholds LoadThresholds()
    {
        var cfg = new ConfigLoader().LoadEffectiveConfig();
        var pinned = cfg.Tiers.Pinned;
        if (Microsoft.FSharp.Core.FSharpOption<Core.Config.PinnedTierConfig>.get_IsSome(pinned))
        {
            var p = pinned.Value;
            return new FloorThresholds(p.FloorEnabled, p.FloorEveryNTurns, p.FloorGrowthTokens);
        }
        return new FloorThresholds(true, 6, 6000);
    }

    private string RenderBlock(string? cwd)
    {
        var project = (_projectResolver ?? new ProjectResolver())
            .Resolve(cwd ?? Environment.CurrentDirectory);
        var opts = PinnedScope.OptsFor(project, _projectScoping ?? LoadProjectScoping());

        if (_store is not null)
            return RenderFrom(_store, opts);

        var dbPath = ConfigLoader.GetDbPath();
        MsSqliteConnection? owned = null;
        try
        {
            owned = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(owned);
            return RenderFrom(new SqliteStore(owned), opts);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static string RenderFrom(IStore store, ListEntriesOpts? opts)
    {
        var mem = store.List(Tier.Pinned, ContentType.Memory, opts);
        var know = store.List(Tier.Pinned, ContentType.Knowledge, opts);
        var (block, _) = PinnedBlockRenderer.Render(mem, know);
        return block;
    }

    private bool LoadProjectScoping()
    {
        var cfg = new ConfigLoader().LoadEffectiveConfig();
        var pinned = cfg.Tiers.Pinned;
        return !Microsoft.FSharp.Core.FSharpOption<Core.Config.PinnedTierConfig>.get_IsSome(pinned)
            || pinned.Value.ProjectScoping;
    }

    private int LoadCompactionThreshold()
    {
        try { return new ConfigLoader().LoadEffectiveConfig().Tiers.Hot.CompactionHintThreshold; }
        catch { return 5; }
    }

    private int CountHot()
    {
        // I1 (tier model v2): total hot occupancy — INCLUDES sticky.
        try
        {
            if (_store is not null) return _store.Count(Tier.Hot, ContentType.Memory);
            var dbPath = ConfigLoader.GetDbPath();
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            return new SqliteStore(conn).Count(Tier.Hot, ContentType.Memory);
        }
        catch { return 0; }
    }

    private static string EnvelopeForHost(string host, string additionalContext)
    {
        var ctx = new StringBuilder();
        JsonWriter.AppendString(ctx, additionalContext);
        var c = ctx.ToString();

        return host switch
        {
            "claude-code" =>
                "{\"hookSpecificOutput\":{\"hookEventName\":\"UserPromptSubmit\",\"additionalContext\":" + c + "}}",
            "copilot-cli" =>
                "{\"additionalContext\":" + c + "}",
            _ => "{}",
        };
    }
}
