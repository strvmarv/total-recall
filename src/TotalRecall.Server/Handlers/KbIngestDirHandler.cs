// src/TotalRecall.Server/Handlers/KbIngestDirHandler.cs
//
// Plan 4 Task 4.9 — ports the `kb_ingest_dir` branch of
// src-ts/tools/kb-tools.ts (lines 111-118) to the .NET Server. The handler
// validates the `path` and optional `glob` arguments, calls
// IFileIngester.IngestDirectory, and serializes the result as JSON.
//
// Note: the TS input schema declares a `collection` field, but TS's
// ingestDirectory implementation at kb-tools.ts:111-117 does not pass it
// through to its ingester either. We mirror the TS schema for wire parity
// but ignore the field.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Ingestion;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the <c>kb_ingest_dir</c> tool. Wraps
/// <see cref="IFileIngester.IngestDirectory"/> with MCP argument validation
/// and JSON serialization.
/// </summary>
public sealed class KbIngestDirHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "path":       {"type":"string","description":"Path to the directory to ingest"},
            "glob":       {"type":"string","description":"Optional glob pattern to filter files"},
            "collection": {"type":"string","description":"Optional collection name override"},
            "scope":      {"type":"string","description":"Scope for ingested entries (e.g. user:paul, team:eng, service:bot). Uses configured default if omitted."}
          },
          "required": ["path"]
        }
        """).RootElement.Clone();

    private readonly IFileIngester _fileIngester;

    public KbIngestDirHandler(IFileIngester fileIngester)
    {
        _fileIngester = fileIngester ?? throw new ArgumentNullException(nameof(fileIngester));
    }

    public string Name => "kb_ingest_dir";

    public string Description => "Ingest a directory of files into the knowledge base";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("kb_ingest_dir requires arguments", nameof(arguments));

        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("kb_ingest_dir arguments must be a JSON object", nameof(arguments));

        var path = ReadRequiredString(args, "path");
        if (path.Length == 0)
            throw new ArgumentException("path must be a non-empty string");
        if (!Directory.Exists(path))
            throw new ArgumentException($"path does not exist or is not a directory: {path}");

        var glob = ReadOptionalString(args, "glob");
        var scope = args.TryGetProperty("scope", out var scopeEl) && scopeEl.ValueKind == JsonValueKind.String
            ? scopeEl.GetString()
            : null;
        // TODO(Plan 5+): the schema declares a `collection` field for parity
        // with TS, but the TS implementation at src-ts/tools/kb-tools.ts:111-117
        // does not thread it through ingestDirectory either, so we ignore it
        // here. Revisit when the TS reference starts honouring it.

        ct.ThrowIfCancellationRequested();

        var result = _fileIngester.IngestDirectory(path, glob, scope);

        var dto = new IngestDirectoryResultDto(
            CollectionId: result.CollectionId,
            DocumentCount: result.DocumentCount,
            TotalChunks: result.TotalChunks,
            Errors: result.Errors.ToArray(),
            ValidationPassed: result.ValidationPassed,
            ValidationFailures: result.ValidationFailures.ToArray());

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.IngestDirectoryResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static string ReadRequiredString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop))
            throw new ArgumentException($"{name} is required");
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        return prop.GetString() ?? throw new ArgumentException($"{name} must be a string");
    }

    private static string? ReadOptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        var s = prop.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
