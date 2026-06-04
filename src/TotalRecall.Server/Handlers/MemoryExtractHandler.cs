// src/TotalRecall.Server/Handlers/MemoryExtractHandler.cs
//
// Phase 3 idea 2e — MCP handler for the memory_extract tool. The HOST LLM
// does the extraction work (facts/decisions/corrections from conversation
// text); this tool only validates and persists. Facts land in the hot tier
// with a per-fact-type EntryType mapping (fact/action_item map to
// Surfaced). Dedup matches memory_store: identical content in the target
// tier is skipped and counted. All facts are validated BEFORE any write so
// a bad item mid-array cannot leave a partial batch behind.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class MemoryExtractHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "source": {"type":"string","description":"Source identifier (e.g. 'conversation_compact')"},
            "facts": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "type":    {"type":"string","enum":["decision","fact","preference","correction","action_item"]},
                  "content": {"type":"string"},
                  "tags":    {"type":"array","items":{"type":"string"}}
                },
                "required": ["type","content"]
              }
            }
          },
          "required": ["source","facts"]
        }
        """).RootElement.Clone();

    private const int MaxContentLength = 100_000; // matches memory_store

    private readonly IStore _store;
    private readonly IEmbedder _embedder;
    private readonly string? _scopeDefault;

    public MemoryExtractHandler(IStore store, IEmbedder embedder, string? scopeDefault = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _scopeDefault = scopeDefault;
    }

    public string Name => "memory_extract";

    public string Description =>
        "Store facts, decisions, preferences, corrections, and action items extracted from text by the host LLM. This tool only persists; extraction is the host's job.";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_extract requires a JSON object argument", nameof(arguments));

        var args = arguments.Value;

        var source = ArgumentParsing.ReadRequiredString(args, "source");
        if (source.Length == 0)
            throw new ArgumentException("source must be a non-empty string");

        if (!args.TryGetProperty("facts", out var factsEl)
            || factsEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("facts is required and must be an array");

        // Validate every fact up front so a bad item mid-array doesn't
        // leave a partial write behind.
        var parsed = new List<(string FactType, string Content, IReadOnlyList<string>? Tags)>();
        foreach (var factEl in factsEl.EnumerateArray())
        {
            if (factEl.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("each fact must be a JSON object");

            if (!factEl.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String)
                throw new ArgumentException("fact.type is required and must be a string");
            var factType = typeEl.GetString()!;
            if (MapEntryType(factType) is null)
                throw new ArgumentException(
                    $"Invalid fact type: {factType}. Must be decision, fact, preference, correction, or action_item");

            if (!factEl.TryGetProperty("content", out var contentEl)
                || contentEl.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(contentEl.GetString()))
                throw new ArgumentException("fact.content is required and must be a non-empty string");
            var content = contentEl.GetString()!;
            if (content.Length > MaxContentLength)
                throw new ArgumentException(
                    $"fact.content exceeds maximum length of {MaxContentLength} characters");

            IReadOnlyList<string>? tags = null;
            if (factEl.TryGetProperty("tags", out var tagsEl)
                && tagsEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var t in tagsEl.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("fact.tags must be an array of strings");
                    list.Add(t.GetString()!);
                }
                tags = list;
            }

            parsed.Add((factType, content, tags));
        }

        var entries = new List<MemoryExtractEntryDto>();
        var duplicates = 0;

        foreach (var (factType, content, tags) in parsed)
        {
            ct.ThrowIfCancellationRequested();

            var existing = _store.FindByContent(Tier.Hot, ContentType.Memory, content);
            if (existing is not null)
            {
                duplicates++;
                continue;
            }

            var entryType = MapEntryType(factType)!;
            var entryTypeStr = SessionLifecycle.EntryTypeToString(entryType);
            var metadataJson = $"{{\"entry_type\":\"{entryTypeStr}\"}}";
            var vector = _embedder.Embed(content);
            var id = _store.InsertWithEmbedding(
                Tier.Hot,
                ContentType.Memory,
                new InsertEntryOpts(
                    Content: content,
                    Source: source,
                    Tags: tags,
                    MetadataJson: metadataJson,
                    Scope: _scopeDefault,
                    EntryType: entryType),
                vector);

            entries.Add(new MemoryExtractEntryDto(id, factType, content));
        }

        var dto = new MemoryExtractResultDto
        {
            Stored = entries.Count,
            Entries = entries.ToArray(),
            DuplicatesSkipped = duplicates,
        };
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.MemoryExtractResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    /// <summary>
    /// Design-resolved mapping: decision/preference/correction map to their
    /// EntryType; fact + action_item map to Surfaced. Returns null for
    /// unknown types so the caller can throw a clean ArgumentException.
    /// </summary>
    internal static EntryType? MapEntryType(string factType) => factType switch
    {
        "decision" => EntryType.Decision,
        "preference" => EntryType.Preference,
        "correction" => EntryType.Correction,
        "fact" => EntryType.Surfaced,
        "action_item" => EntryType.Surfaced,
        _ => null,
    };
}
