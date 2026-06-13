// src/TotalRecall.Cli/Commands/PinnedFloorCommand.cs
//
// `total-recall pinned-floor --host <claude-code|copilot-cli|cursor>`.
// Per-turn UserPromptSubmit hook backend. Reads the host hook payload from
// stdin, decides via the adaptive throttle, and on an "inject" turn renders the
// pinned block live from the DB and emits host-correct additionalContext JSON.
// FAIL-SAFE: any error prints "{}" and exits 0 — a UserPromptSubmit hook must
// never block or reject the user's prompt.

using System;
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

    public PinnedFloorCommand() { }

    public PinnedFloorCommand(
        IStore store, string stateDir, TextReader input, TextWriter output,
        FloorThresholds thresholds, Func<string, long?> transcriptSizer)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _stateDir = stateDir ?? throw new ArgumentNullException(nameof(stateDir));
        _in = input ?? throw new ArgumentNullException(nameof(input));
        _out = output ?? throw new ArgumentNullException(nameof(output));
        _thresholds = thresholds;
        _sizer = transcriptSizer ?? throw new ArgumentNullException(nameof(transcriptSizer));
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
            var (sessionId, transcriptPath) = ParsePayload(input);

            var stateDir = _stateDir ?? PinnedFloorState.DefaultStateDir();
            var thresholds = _thresholds ?? LoadThresholds();

            PinnedFloorState.Prune(stateDir, maxAgeDays: 3, nowUtc: DateTimeOffset.UtcNow);

            var state = PinnedFloorState.Load(stateDir, sessionId);
            long? bytes = transcriptPath is null ? null : SizeOf(transcriptPath);
            var (verdict, next) = PinnedFloorDecider.Decide(state, new FloorSignal(bytes), thresholds);

            PinnedFloorState.Save(stateDir, next);

            if (verdict == FloorVerdict.Inject)
            {
                var block = RenderBlock();
                if (!string.IsNullOrEmpty(block))
                {
                    var ctx = ReminderPreamble + "\n\n" + block;
                    output.WriteLine(EnvelopeForHost(host, ctx));
                    return 0;
                }
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

    private static (string SessionId, string? TranscriptPath) ParsePayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ("unknown-session", null);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var session = GetString(r, "session_id") ?? GetString(r, "sessionId") ?? "unknown-session";
        var transcript = GetString(r, "transcript_path") ?? GetString(r, "transcriptPath");
        return (session, transcript);
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

    private string RenderBlock()
    {
        if (_store is not null)
            return RenderFrom(_store);

        var dbPath = ConfigLoader.GetDbPath();
        MsSqliteConnection? owned = null;
        try
        {
            owned = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(owned);
            var store = new SqliteStore(owned);
            return RenderFrom(store);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static string RenderFrom(IStore store)
    {
        var mem = store.List(Tier.Pinned, ContentType.Memory);
        var know = store.List(Tier.Pinned, ContentType.Knowledge);
        var (block, _) = PinnedBlockRenderer.Render(mem, know);
        return block;
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
