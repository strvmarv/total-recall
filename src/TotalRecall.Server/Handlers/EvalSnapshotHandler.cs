// src/TotalRecall.Server/Handlers/EvalSnapshotHandler.cs
//
// Plan 6 Task 6.0c — ports `total-recall eval snapshot <name>` to MCP.
// Creates a named config snapshot via ConfigSnapshotStore (which dedupes
// against the latest row when the JSON is byte-identical).
//
// Args: { name (required string) }

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

/// <summary>Test seam: given a name, returns (id, dedupedAgainstLatest).</summary>
public delegate (string Id, bool Deduped) EvalSnapshotExecutor(string name);

public sealed class EvalSnapshotHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "name": {"type":"string","description":"Snapshot name"}
          },
          "required": ["name"]
        }
        """).RootElement.Clone();

    private readonly EvalSnapshotExecutor? _executor;

    public EvalSnapshotHandler() { _executor = null; }

    public EvalSnapshotHandler(EvalSnapshotExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public string Name => "eval_snapshot";
    public string Description => "Create a named config snapshot (dedupes against the latest)";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("eval_snapshot requires arguments object");
        var args = arguments.Value;
        if (!args.TryGetProperty("name", out var nEl) || nEl.ValueKind != JsonValueKind.String)
            throw new ArgumentException("name is required");
        var name = nEl.GetString();
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("name must be a non-empty string");

        ct.ThrowIfCancellationRequested();

        var executor = _executor ?? BuildProductionExecutor();
        var (id, deduped) = executor(name);

        var dto = new EvalSnapshotResultDto(Id: id, Name: name, Deduped: deduped);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.EvalSnapshotResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static EvalSnapshotExecutor BuildProductionExecutor()
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
}
