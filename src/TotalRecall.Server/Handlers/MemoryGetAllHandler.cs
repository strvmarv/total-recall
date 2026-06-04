// src/TotalRecall.Server/Handlers/MemoryGetAllHandler.cs
//
// memory_get_all — convenience MCP tool that returns ALL entries matching
// filters (no pagination — full dump using a high limit). Sweeps both
// ContentType.Memory and ContentType.Knowledge for the requested tier
// and returns a flat list of entries. Capped at 10,000 per content type
// with a `truncated` flag when the cap is hit.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class MemoryGetAllHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "tier": {"type":"string","description":"Storage tier to dump (default: warm)"},
            "source_tool": {"type":"string","description":"Filter by source tool (metadata key)"},
            "tags": {"type":"array","items":{"type":"string"},"description":"Filter by tags (metadata key)"}
          }
        }
        """).RootElement.Clone();

    private readonly IStore _store;

    public MemoryGetAllHandler(IStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "memory_get_all";
    public string Description => "Full dump of all entries matching filters (no pagination)";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        Tier tier = Tier.Warm;
        string? sourceTool = null;
        IReadOnlyList<string>? tags = null;

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;

            var tierStr = ArgumentParsing.ReadOptionalString(args, "tier");
            if (tierStr is not null)
                tier = TierNames.ParseTier(tierStr)
                    ?? throw new ArgumentException($"invalid tier '{tierStr}' (expected hot, warm, or cold)");

            sourceTool = ArgumentParsing.ReadOptionalString(args, "source_tool");
            tags = ArgumentParsing.ReadTags(args);
        }

        ct.ThrowIfCancellationRequested();

        // C1: Always use List (never ListByMetadata). Filter in memory below.
        // Use a generous but bounded limit for the dump use case.
        const int maxGetAll = 10_000;
        var opts = new ListEntriesOpts { Limit = maxGetAll };

        var memoryEntries = _store.List(tier, ContentType.Memory, opts);
        var knowledgeEntries = _store.List(tier, ContentType.Knowledge, opts);

        // Capture raw counts before in-memory filtering so truncation
        // detection reflects the actual per-type cap, not the post-filter count.
        var truncated = memoryEntries.Count >= maxGetAll || knowledgeEntries.Count >= maxGetAll;

        // C1: In-memory filtering on Entry properties.
        // C2: OR semantics for tags (iterate individual tags, NOT comma-joined).
        if (sourceTool is not null || tags is { Count: > 0 })
        {
            memoryEntries = FilterEntries(memoryEntries, sourceTool, tags, ct);
            knowledgeEntries = FilterEntries(knowledgeEntries, sourceTool, tags, ct);
        }

        ct.ThrowIfCancellationRequested();

        var allDtos = new List<EntryDto>(memoryEntries.Count + knowledgeEntries.Count);
        foreach (var e in memoryEntries)
            allDtos.Add(EntryMapping.ToEntryDto(e));
        foreach (var e in knowledgeEntries)
            allDtos.Add(EntryMapping.ToEntryDto(e));

        var dto = new MemoryGetAllResultDto(
            Tier: TierNames.TierName(tier),
            Entries: allDtos.ToArray(),
            Count: allDtos.Count,
            Truncated: truncated,
            MaxResults: maxGetAll);

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.MemoryGetAllResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    /// <summary>
    /// C1 + C2 + I3: In-memory filter that operates on Entry properties
    /// (SourceTool and Tags are dedicated columns, not JSON metadata).
    /// Tags use OR semantics — an entry matches if it has ANY of the
    /// requested tags. Cancellation is checked at every entry.
    /// </summary>
    private static IReadOnlyList<Entry> FilterEntries(
        IReadOnlyList<Entry> entries,
        string? sourceTool,
        IReadOnlyList<string>? tags,
        CancellationToken ct)
    {
        var tagSet = tags is { Count: > 0 }
            ? new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase)
            : null;

        var result = new List<Entry>(entries.Count);
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested(); // I3

            if (sourceTool is not null &&
                !string.Equals(EntryMapping.SourceToolName(e.SourceTool),
                    sourceTool, StringComparison.OrdinalIgnoreCase))
                continue;

            if (tagSet is not null)
            {
                // C2: OR semantics — keep if entry has ANY of the requested tags.
                bool hasAnyTag = false;
                foreach (var tag in e.Tags)
                {
                    ct.ThrowIfCancellationRequested(); // I3
                    if (tagSet.Contains(tag))
                    {
                        hasAnyTag = true;
                        break;
                    }
                }
                if (!hasAnyTag) continue;
            }

            result.Add(e);
        }
        return result;
    }
}
