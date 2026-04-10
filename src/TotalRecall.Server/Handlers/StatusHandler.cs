// src/TotalRecall.Server/Handlers/StatusHandler.cs
//
// Plan 4 Task 4.11 — ports the structured portion of the `status` branch
// of src-ts/tools/system-tools.ts to the .NET Server. The TS handler does
// considerably more than what Plan 4 needs (activity telemetry, last
// compaction, last session age); those sections are either stubbed or
// intentionally omitted per the task scope and marked below.
//
// Design notes:
//
//   - Ctor dependencies are (IStore, ISessionLifecycle, StatusOptions).
//     StatusOptions carries DbPath, EmbeddingModel, EmbeddingDimensions —
//     values that live outside the store/lifecycle seams and would
//     otherwise have to be pulled from the process host configuration.
//
//   - tierSizes is 6 IStore.Count calls, one per (tier, type).
//
//   - knowledgeBase enumerates cold_knowledge rows whose metadata.type ==
//     "collection" via IStore.ListByMetadata (option b — collections
//     are already marked with that discriminator by HierarchicalIndex.cs,
//     see EncodeCollectionMetadata). totalChunks = Count(Cold, Knowledge) -
//     collections.Count, matching the TS semantics that treats every
//     non-collection cold_knowledge row as a chunk.
//
//   - Collection name extraction uses System.Text.Json JsonDocument on the
//     raw metadata blob. Not AOT-hostile: JsonDocument.Parse(string) does
//     not pull reflection-based serializers.
//
//   - db.sizeBytes is read via FileInfo.Length when the file exists, null
//     otherwise. Does NOT throw on missing file.
//
//   - activity is stubbed (TODO Plan 5+) — retrieval/outcome telemetry is
//     not persisted in the .NET store as of Plan 4.
//
//   - lastCompaction is stubbed to null (TODO Plan 5+) — the compaction
//     log read seam lands with the full compaction pipeline in Plan 5+.
//
//   - lastSessionAge from TS lives in the session_start domain and is
//     intentionally NOT surfaced here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// Options supplied by the server host to <see cref="StatusHandler"/>.
/// These are immutable process-level values that the handler echoes back
/// in the status response (db path/size, embedding model/dimensions).
/// </summary>
public sealed record StatusOptions(
    string DbPath,
    string EmbeddingModel,
    int EmbeddingDimensions);

/// <summary>
/// MCP handler for the <c>status</c> tool. Returns a structured view of
/// the tier sizes, knowledge-base collections, database location/size,
/// embedding configuration, and (stubbed) activity/compaction metadata.
/// </summary>
public sealed class StatusHandler : IToolHandler
{
    // Mirror of src-ts/tools/system-tools.ts — status takes no inputs.
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{},"required":[]}
        """).RootElement.Clone();

    // The metadata.type discriminator that HierarchicalIndex writes for
    // collection rows. See Ingestion/HierarchicalIndex.cs:EncodeCollectionMetadata.
    private static readonly IReadOnlyDictionary<string, string> _collectionFilter =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "collection" };

    private readonly IStore _store;
    private readonly ISessionLifecycle _sessionLifecycle;
    private readonly StatusOptions _options;

    public StatusHandler(
        IStore store,
        ISessionLifecycle sessionLifecycle,
        StatusOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessionLifecycle = sessionLifecycle
            ?? throw new ArgumentNullException(nameof(sessionLifecycle));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Name => "status";

    public string Description => "Get the status of the total-recall memory system";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        _ = arguments; // status takes no inputs.

        ct.ThrowIfCancellationRequested();

        var tierSizes = new TierSizesDto(
            HotMemories: _store.Count(Tier.Hot, ContentType.Memory),
            HotKnowledge: _store.Count(Tier.Hot, ContentType.Knowledge),
            WarmMemories: _store.Count(Tier.Warm, ContentType.Memory),
            WarmKnowledge: _store.Count(Tier.Warm, ContentType.Knowledge),
            ColdMemories: _store.Count(Tier.Cold, ContentType.Memory),
            ColdKnowledge: _store.Count(Tier.Cold, ContentType.Knowledge));

        ct.ThrowIfCancellationRequested();

        // Option (b): enumerate KB collections via metadata filter. The
        // HierarchicalIndex writes {"type":"collection", ...} into the
        // cold_knowledge row metadata, so ListByMetadata with that single
        // filter is sufficient.
        var collectionRows = _store.ListByMetadata(
            Tier.Cold,
            ContentType.Knowledge,
            _collectionFilter);

        var collections = new KbCollectionSummaryDto[collectionRows.Count];
        for (var i = 0; i < collectionRows.Count; i++)
        {
            var entry = collectionRows[i];
            collections[i] = new KbCollectionSummaryDto(
                Id: entry.Id,
                Name: ExtractCollectionName(entry.MetadataJson));
        }

        // totalChunks = all cold_knowledge rows minus the collection rows.
        // Document + chunk rows both count as "chunks" from the TS status
        // perspective (system-tools.ts).
        var coldKnowledge = tierSizes.ColdKnowledge;
        var totalChunks = coldKnowledge - collectionRows.Count;
        if (totalChunks < 0) totalChunks = 0;
        var kb = new KbStatusDto(Collections: collections, TotalChunks: totalChunks);

        long? sizeBytes = null;
        try
        {
            var fi = new FileInfo(_options.DbPath);
            if (fi.Exists) sizeBytes = fi.Length;
        }
        catch (Exception ex) when (
            ex is IOException
            || ex is UnauthorizedAccessException
            || ex is System.Security.SecurityException
            || ex is ArgumentException
            || ex is PathTooLongException
            || ex is NotSupportedException)
        {
            // Size is best-effort. Leave null on any filesystem hiccup.
            sizeBytes = null;
        }

        var db = new DbStatusDto(
            Path: _options.DbPath,
            SizeBytes: sizeBytes,
            SessionId: _sessionLifecycle.SessionId);

        var embedding = new EmbeddingStatusDto(
            Model: _options.EmbeddingModel,
            Dimensions: _options.EmbeddingDimensions);

        // TODO(Plan 5+): retrieval/outcome telemetry is not persisted in
        // the .NET store yet. Stubbed to zeros/null so the wire shape is
        // stable for host consumers.
        var activity = new ActivityStatusDto(
            Retrievals7d: 0,
            AvgTopScore7d: null,
            PositiveOutcomes7d: 0,
            NegativeOutcomes7d: 0);

        // TODO(Plan 5+): compaction log read seam lands with the real
        // compaction pipeline. null = "no compactions recorded".
        LastCompactionDto? lastCompaction = null;

        var dto = new StatusResultDto(
            TierSizes: tierSizes,
            KnowledgeBase: kb,
            Db: db,
            Embedding: embedding,
            Activity: activity,
            LastCompaction: lastCompaction);

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.StatusResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    /// <summary>
    /// Extracts the <c>name</c> field from a collection metadata JSON blob.
    /// Returns null if the blob is missing, not an object, lacks a string
    /// <c>name</c> property, or fails to parse. Lenient by design: a
    /// malformed metadata row should not take down the status call.
    /// </summary>
    private static string? ExtractCollectionName(string metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("name", out var nameProp)) return null;
            return nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
