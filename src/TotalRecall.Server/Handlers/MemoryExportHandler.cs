// src/TotalRecall.Server/Handlers/MemoryExportHandler.cs
//
// Plan 6 Task 6.0a — ports `memory export` (ExportCommand.cs) to MCP.
// Sweeps the 6 (tier, type) tables, applies optional tier/type filters,
// and returns a JSON envelope { version, exported_at, entries[] } that
// memory_import can consume. The CLI version also supports writing to a
// file via --out; the handler returns the payload inline only (callers
// can persist it themselves).
//
// Args: { tiers?: string[] | "hot,warm,cold", types?: string[] | "memory,knowledge" }
//
// Filter parsing accepts either form the CLI does (comma-separated
// string) AND the natural JSON array form, so clients don't have to
// reason about CLI-shaped args.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class MemoryExportHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "tiers": {
              "description":"Optional tier filter (array or comma-separated string)",
              "oneOf":[
                {"type":"array","items":{"type":"string","enum":["hot","warm","cold"]}},
                {"type":"string"}
              ]
            },
            "types": {
              "description":"Optional content-type filter (array or comma-separated string)",
              "oneOf":[
                {"type":"array","items":{"type":"string","enum":["memory","knowledge"]}},
                {"type":"string"}
              ]
            }
          }
        }
        """).RootElement.Clone();

    private readonly IStore _store;

    public MemoryExportHandler(IStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "memory_export";
    public string Description => "Export memory/knowledge entries as a JSON envelope";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        HashSet<Tier>? tierFilter = null;
        HashSet<ContentType>? typeFilter = null;

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;
            if (args.TryGetProperty("tiers", out var tEl))
            {
                tierFilter = new HashSet<Tier>();
                foreach (var token in ReadStringList(tEl, "tiers"))
                {
                    var parsed = TierNames.ParseTier(token)
                        ?? throw new ArgumentException($"invalid tier '{token}' (expected hot|warm|cold)");
                    tierFilter.Add(parsed);
                }
            }
            if (args.TryGetProperty("types", out var cEl))
            {
                typeFilter = new HashSet<ContentType>();
                foreach (var token in ReadStringList(cEl, "types"))
                {
                    var parsed = TierNames.ParseContentType(token)
                        ?? throw new ArgumentException($"invalid type '{token}' (expected memory|knowledge)");
                    typeFilter.Add(parsed);
                }
            }
        }

        ct.ThrowIfCancellationRequested();

        var collected = new List<ExportEntryDto>();
        foreach (var pair in TierNames.AllTablePairs)
        {
            if (tierFilter is not null && !tierFilter.Contains(pair.Tier)) continue;
            if (typeFilter is not null && !typeFilter.Contains(pair.Type)) continue;
            var rows = _store.List(pair.Tier, pair.Type, null);
            foreach (var e in rows)
            {
                collected.Add(new ExportEntryDto(
                    Id: e.Id,
                    Content: e.Content,
                    Summary: EntryMapping.OptString(e.Summary),
                    Source: EntryMapping.OptString(e.Source),
                    SourceTool: EntryMapping.SourceToolName(e.SourceTool),
                    Project: EntryMapping.OptString(e.Project),
                    Tags: ListModule.ToArray(e.Tags),
                    CreatedAt: e.CreatedAt,
                    UpdatedAt: e.UpdatedAt,
                    LastAccessedAt: e.LastAccessedAt,
                    AccessCount: e.AccessCount,
                    DecayScore: e.DecayScore,
                    ParentId: EntryMapping.OptString(e.ParentId),
                    CollectionId: EntryMapping.OptString(e.CollectionId),
                    Metadata: string.IsNullOrEmpty(e.MetadataJson) ? "{}" : e.MetadataJson,
                    Tier: TierNames.TierName(pair.Tier),
                    ContentType: TierNames.ContentTypeName(pair.Type)));
            }
        }

        var dto = new MemoryExportResultDto(
            Version: 1,
            ExportedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Entries: collected.ToArray());
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.MemoryExportResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static IEnumerable<string> ReadStringList(JsonElement el, string name)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                yield break;
            case JsonValueKind.String:
                foreach (var token in (el.GetString() ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return token;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        throw new ArgumentException($"{name} array items must be strings");
                    var s = item.GetString();
                    if (!string.IsNullOrEmpty(s)) yield return s;
                }
                break;
            default:
                throw new ArgumentException($"{name} must be a string or array of strings");
        }
    }
}
