// src/TotalRecall.Server/Handlers/MemoryPromoteHandler.cs
//
// Plan 6 Task 6.0a — MCP wrapper for the same 4-step promotion sequence
// that `total-recall memory promote` already runs (PromoteCommand.cs).
// The business logic lives in Infrastructure.Memory.MoveHelpers so both
// the CLI and the Server drive a single implementation (closes Plan 5
// carry-forward #8).
//
// Args: { id (required), tier? (default "hot", must be hot|warm),
//         type? (memory|knowledge, defaults to source type) }.
// Response: MemoryMoveResultDto with from_* / to_* tier + content_type.
//
// Handler throws ArgumentException on bad args / missing entry / bad
// direction; the ErrorTranslator at the McpServer boundary maps those to
// structured error tool responses. We deliberately do NOT catch anything
// here (Plan 4 convention).

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class MemoryPromoteHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id":   {"type":"string","description":"Entry ID"},
            "tier": {"type":"string","enum":["hot","warm"],"description":"Target tier (default: hot)"},
            "type": {"type":"string","enum":["memory","knowledge"],"description":"Target content type (default: source type)"}
          },
          "required": ["id"]
        }
        """).RootElement.Clone();

    private readonly ISqliteStore _store;
    private readonly IVectorSearch _vec;
    private readonly IEmbedder _embedder;

    public MemoryPromoteHandler(ISqliteStore store, IVectorSearch vec, IEmbedder embedder)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    public string Name => "memory_promote";
    public string Description => "Promote a memory or knowledge entry to a warmer tier (re-embeds)";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_promote requires arguments", nameof(arguments));
        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_promote arguments must be a JSON object", nameof(arguments));

        var id = ReadRequiredString(args, "id");
        if (id.Length == 0)
            throw new ArgumentException("id must be a non-empty string");

        var tierStr = ReadOptionalString(args, "tier") ?? "hot";
        var toTier = TierNames.ParseTier(tierStr)
            ?? throw new ArgumentException($"invalid tier '{tierStr}' (expected hot|warm)");
        if (toTier.IsCold)
            throw new ArgumentException("cannot promote to cold (use memory_demote instead)");

        ContentType? requestedType = null;
        var typeStr = ReadOptionalString(args, "type");
        if (typeStr is not null)
        {
            requestedType = TierNames.ParseContentType(typeStr)
                ?? throw new ArgumentException($"invalid type '{typeStr}' (expected memory|knowledge)");
        }

        ct.ThrowIfCancellationRequested();

        var located = MoveHelpers.Locate(_store, id)
            ?? throw new ArgumentException($"entry {id} not found");

        var (fromTier, fromType, entry) = located;
        var targetType = requestedType ?? fromType;

        if (TierNames.WarmthRank(toTier) <= TierNames.WarmthRank(fromTier))
            throw new ArgumentException(
                $"cannot promote {TierNames.TierName(fromTier)} -> {TierNames.TierName(toTier)} (target must be warmer)");

        MoveHelpers.MoveAndReEmbed(_store, _vec, _embedder, entry, fromTier, fromType, toTier, targetType);

        var dto = new MemoryMoveResultDto(
            Id: id,
            FromTier: TierNames.TierName(fromTier),
            FromContentType: TierNames.ContentTypeName(fromType),
            ToTier: TierNames.TierName(toTier),
            ToContentType: TierNames.ContentTypeName(targetType),
            Success: true);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.MemoryMoveResultDto);

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
        if (!args.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Null) return null;
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        return prop.GetString();
    }
}
