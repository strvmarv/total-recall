// src/TotalRecall.Server/Handlers/KbIngestFileHandler.cs
//
// Plan 4 Task 4.9 — ports the `kb_ingest_file` branch of
// src-ts/tools/kb-tools.ts (lines 102-109) to the .NET Server. The handler
// validates the `path` argument, calls IFileIngester.IngestFile(path,
// collection), and serializes the result as JSON. File existence is checked
// up front so callers get a clean ArgumentException instead of an
// IOException from ReadAllText deep inside the ingester.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Ingestion;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the <c>kb_ingest_file</c> tool. Wraps
/// <see cref="IFileIngester.IngestFile"/> with MCP argument validation and
/// JSON serialization.
/// </summary>
public sealed class KbIngestFileHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "path":       {"type":"string","description":"Path to the file to ingest"},
            "collection": {"type":"string","description":"Optional collection ID to add to"},
            "scope":      {"type":"string","description":"Scope for ingested entries (e.g. user:paul, team:eng, service:bot). Uses configured default if omitted."}
          },
          "required": ["path"]
        }
        """).RootElement.Clone();

    private readonly IFileIngester _fileIngester;
    private readonly string? _scopeDefault;

    public KbIngestFileHandler(IFileIngester fileIngester, string? scopeDefault = null)
    {
        _fileIngester = fileIngester ?? throw new ArgumentNullException(nameof(fileIngester));
        _scopeDefault = scopeDefault;
    }

    public string Name => "kb_ingest_file";

    public string Description => "Ingest a single file into the knowledge base";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("kb_ingest_file requires arguments", nameof(arguments));

        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("kb_ingest_file arguments must be a JSON object", nameof(arguments));

        var path = ReadRequiredString(args, "path");
        if (path.Length == 0)
            throw new ArgumentException("path must be a non-empty string");
        if (!File.Exists(path))
            throw new ArgumentException($"path does not exist: {path}");

        var collection = ReadOptionalString(args, "collection");
        var scope = args.TryGetProperty("scope", out var scopeEl) && scopeEl.ValueKind == JsonValueKind.String
            ? scopeEl.GetString()
            : _scopeDefault;

        ct.ThrowIfCancellationRequested();

        var result = _fileIngester.IngestFile(path, collection, scope);

        var probes = new ProbeResultDto[result.Validation.Probes.Count];
        for (var i = 0; i < probes.Length; i++)
        {
            var p = result.Validation.Probes[i];
            probes[i] = new ProbeResultDto(p.ChunkIndex, p.Score, p.Passed);
        }

        var dto = new IngestFileResultDto(
            DocumentId: result.DocumentId,
            ChunkCount: result.ChunkCount,
            ValidationPassed: result.ValidationPassed,
            Validation: new ValidationResultDto(result.Validation.Passed, probes));

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.IngestFileResultDto);

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
