// src/TotalRecall.Server/Handlers/MemoryRecentHandler.cs
//
// memory_recent — list memories newest-first by a selectable timestamp,
// merged across tiers, with optional tier/type/project/scope filters.
// Thin adapter over Infrastructure.Memory.RecentQuery.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class MemoryRecentHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "limit": {"type":"number","description":"Max entries to return (1..200, default 20)"},
            "tier": {"type":"string","enum":["hot","warm","cold"],"description":"Restrict to one tier; default all three merged"},
            "type": {"type":"string","description":"Filter by entry type: correction|preference|decision|surfaced|imported|compacted|ingested"},
            "project": {"type":"string","description":"Filter by exact project name"},
            "order": {"type":"string","enum":["created","updated","accessed"],"description":"Sort field (default created)"},
            "scopes": {"type":"array","items":{"type":"string"},"description":"Scope filter; defaults to the server's default scope"}
          }
        }
        """).RootElement.Clone();

    private readonly IStore _store;
    private readonly string? _scopeDefault;

    public MemoryRecentHandler(IStore store, string? scopeDefault)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scopeDefault = scopeDefault;
    }

    public string Name => "memory_recent";
    public string Description => "List recent memories newest-first by timestamp, merged across tiers";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        int limit = 20;
        Tier? tierFilter = null;
        EntryType? typeFilter = null;
        string? project = null;
        string order = "created";
        IReadOnlyList<string>? scopes = null;

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;

            limit = ArgumentParsing.ReadOptionalInt(args, "limit", 1, 200) ?? 20;

            var tierStr = ArgumentParsing.ReadOptionalString(args, "tier");
            if (tierStr is not null)
                tierFilter = TierNames.ParseTier(tierStr)
                    ?? throw new ArgumentException($"invalid tier '{tierStr}' (expected hot, warm, or cold)");

            var typeStr = ArgumentParsing.ReadOptionalString(args, "type");
            if (typeStr is not null)
                typeFilter = TierNames.ParseEntryType(typeStr)
                    ?? throw new ArgumentException(
                        $"invalid type '{typeStr}' (expected correction, preference, decision, surfaced, imported, compacted, or ingested)");

            project = ArgumentParsing.ReadOptionalString(args, "project");

            var orderStr = ArgumentParsing.ReadOptionalString(args, "order");
            if (orderStr is not null)
            {
                if (orderStr is not ("created" or "updated" or "accessed"))
                    throw new ArgumentException($"invalid order '{orderStr}' (expected created, updated, or accessed)");
                order = orderStr;
            }

            scopes = ArgumentParsing.ReadStringArray(args, "scopes");
        }

        ct.ThrowIfCancellationRequested();

        if (scopes is null or { Count: 0 })
            scopes = _scopeDefault is null ? null : new[] { _scopeDefault };
        var effectiveScopes = scopes;

        var rows = RecentQuery.Run(_store, new RecentOptions(
            Limit: limit,
            Tier: tierFilter,
            Type: typeFilter,
            Project: project,
            Order: order,
            Scopes: effectiveScopes));

        var entries = rows.Select(r => new RecentEntryDto(
            Id: r.Entry.Id,
            Tier: TierNames.TierName(r.Tier),
            EntryType: TierNames.EntryTypeName(r.Entry.EntryType),
            Project: EntryMapping.OptString(r.Entry.Project),
            CreatedAt: r.Entry.CreatedAt,
            UpdatedAt: r.Entry.UpdatedAt,
            LastAccessedAt: r.Entry.LastAccessedAt,
            Preview: PreviewText.Collapse(r.Entry.Content, PreviewText.DefaultMaxLength))).ToArray();

        var dto = new MemoryRecentResultDto(
            Entries: entries,
            Count: entries.Length,
            Order: order,
            Tier: tierFilter is { } tf ? TierNames.TierName(tf) : null,
            Type: typeFilter is { } et ? TierNames.EntryTypeName(et) : null,
            Project: project);

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.MemoryRecentResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }
}
