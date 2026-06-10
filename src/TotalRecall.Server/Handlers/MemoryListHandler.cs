// src/TotalRecall.Server/Handlers/MemoryListHandler.cs
//
// memory_list — paginated listing of memory entries with optional filters
// (tier, content_type, tags, project, source_tool, limit, offset).
// Thin adapter over IStore.List / IStore.ListByMetadata.

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

public sealed class MemoryListHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "tier":         {"type":"string","enum":["hot","warm","cold","pinned"],"description":"Restrict to one tier; default all"},
            "content_type": {"type":"string","enum":["memory","knowledge"],"description":"Restrict to one content type; default both"},
            "tags":         {"type":"array","items":{"type":"string"},"description":"Filter by tags stored in metadata"},
            "project":      {"type":"string","description":"Filter by exact project name"},
            "source_tool":  {"type":"string","description":"Filter by source tool (metadata)"},
            "limit":        {"type":"integer","description":"Max entries to return (default 50, max 200)"},
            "offset":       {"type":"integer","description":"Number of entries to skip (default 0)"}
          }
        }
        """).RootElement.Clone();

    private readonly IStore _store;

    public MemoryListHandler(IStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "memory_list";
    public string Description => "Paginated listing of memory entries with optional filters";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        Tier? tierFilter = null;
        ContentType? contentTypeFilter = null;
        IReadOnlyList<string>? tagsFilter = null;
        string? project = null;
        string? sourceTool = null;
        int limit = 50;
        int offset = 0;

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;

            var tierStr = ArgumentParsing.ReadOptionalString(args, "tier");
            if (tierStr is not null)
                tierFilter = TierNames.ParseTier(tierStr)
                    ?? throw new ArgumentException($"invalid tier '{tierStr}' (expected hot, warm, cold, or pinned)");

            var ctStr = ArgumentParsing.ReadOptionalString(args, "content_type");
            if (ctStr is not null)
                contentTypeFilter = TierNames.ParseContentType(ctStr)
                    ?? throw new ArgumentException($"invalid content_type '{ctStr}' (expected memory or knowledge)");

            tagsFilter = ArgumentParsing.ReadTags(args);

            project = ArgumentParsing.ReadOptionalString(args, "project");

            sourceTool = ArgumentParsing.ReadOptionalString(args, "source_tool");

            limit = ArgumentParsing.ReadOptionalInt(args, "limit", 1, 200) ?? 50;
            offset = ArgumentParsing.ReadOptionalInt(args, "offset", 0, int.MaxValue) ?? 0;
        }

        ct.ThrowIfCancellationRequested();

        // I1: Pass a per-table limit so we don't fetch everything from every table.
        var perTableLimit = limit + offset;

        var allEntries = new List<(Tier Tier, ContentType Type, Entry Entry)>();

        foreach (var pair in TierNames.AllTablePairs)
        {
            // Apply tier / content_type filters.
            if (tierFilter is not null && pair.Tier != tierFilter) continue;
            if (contentTypeFilter is not null && pair.Type != contentTypeFilter) continue;

            ct.ThrowIfCancellationRequested();

            var tier = pair.Tier;
            var type = pair.Type;

            // C1: Always use List (never ListByMetadata). Filter in memory below.
            var rows = _store.List(tier, type, new ListEntriesOpts
            {
                Project = project,
                OrderBy = "created_at DESC",
                Limit = perTableLimit
            });
            foreach (var e in rows)
                allEntries.Add((tier, type, e));
        }

        ct.ThrowIfCancellationRequested();

        // C1: In-memory filtering on Entry properties (SourceTool and Tags are dedicated columns,
        // not JSON metadata). ListByMetadata queries json_extract on metadata which is always
        // empty for .NET-inserted entries.
        if (sourceTool is not null)
        {
            allEntries.RemoveAll(x =>
                !string.Equals(EntryMapping.SourceToolName(x.Entry.SourceTool),
                    sourceTool, StringComparison.OrdinalIgnoreCase));
        }

        if (tagsFilter is { Count: > 0 })
        {
            // C2: OR semantics — iterate individual tags and keep entries that match any.
            var tagSet = new HashSet<string>(tagsFilter, StringComparer.OrdinalIgnoreCase);
            allEntries.RemoveAll(x =>
            {
                // I3: Cancellation check inside tag-matching loop.
                ct.ThrowIfCancellationRequested();
                foreach (var tag in x.Entry.Tags)
                {
                    if (tagSet.Contains(tag)) return false; // keep
                }
                return true; // remove — matches none of the requested tags
            });
        }

        ct.ThrowIfCancellationRequested();

        // Sort combined results by created_at DESC (newest first).
        allEntries.Sort((a, b) => b.Entry.CreatedAt.CompareTo(a.Entry.CreatedAt));

        var total = allEntries.Count;

        // Apply pagination.
        var page = allEntries
            .Skip(offset)
            .Take(limit)
            .Select(x => new MemoryListEntryDto(
                Id: x.Entry.Id,
                Tier: TierNames.TierName(x.Tier),
                ContentType: TierNames.ContentTypeName(x.Type),
                Content: x.Entry.Content,
                Summary: EntryMapping.OptString(x.Entry.Summary),
                SourceTool: EntryMapping.SourceToolName(x.Entry.SourceTool),
                Project: EntryMapping.OptString(x.Entry.Project),
                Tags: ListModule.ToArray(x.Entry.Tags),
                CreatedAt: x.Entry.CreatedAt,
                UpdatedAt: x.Entry.UpdatedAt,
                Scope: x.Entry.Scope))
            .ToArray();

        var result = new MemoryListResultDto(
            Entries: page,
            Count: page.Length,
            Total: total,
            Limit: limit,
            Offset: offset);

        var jsonText = JsonSerializer.Serialize(result, JsonContext.Default.MemoryListResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }
}
