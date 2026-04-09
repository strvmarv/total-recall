using System;
using System.Collections.Generic;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// CRUD seam over the 6 tier-aware content tables. Mirrors the surface
/// exported by <c>src-ts/db/entries.ts</c>. Plan 4's handlers fake against
/// this interface; production wiring uses <see cref="SqliteStore"/>.
/// </summary>
public interface ISqliteStore
{
    /// <summary>Insert a new entry, returning the generated id.</summary>
    string Insert(Tier tier, ContentType type, InsertEntryOpts opts);

    /// <summary>Fetch an entry by id, or <c>null</c> if not found.</summary>
    Entry? Get(Tier tier, ContentType type, string id);

    /// <summary>
    /// Return the SQLite integer <c>rowid</c> of the content row for a
    /// given id, or <c>null</c> if no such row exists. Exists so callers
    /// can resolve the rowid of an entry before deleting it, and then
    /// pass that rowid to <see cref="IVectorSearch.DeleteEmbedding"/> —
    /// eliminating the footgun where the old id-based delete silently
    /// leaked the vec row once the content row was gone. See
    /// <c>MemoryDeleteHandlerTests.Delete_CallsVecDeleteBeforeStoreDelete</c>.
    /// </summary>
    long? GetRowid(Tier tier, ContentType type, string id);

    /// <summary>Update selected fields on an existing entry.</summary>
    void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts);

    /// <summary>Delete an entry by id. No-op if the row does not exist.</summary>
    void Delete(Tier tier, ContentType type, string id);

    /// <summary>List entries, optionally filtered and ordered.</summary>
    IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null);

    /// <summary>Count rows in the table for a (tier, type) pair.</summary>
    int Count(Tier tier, ContentType type);

    /// <summary>
    /// Count distinct non-null <c>collection_id</c> values across the
    /// <c>cold_knowledge</c> table. Mirrors the TS query in
    /// <c>src-ts/tools/session-tools.ts</c>'s tier-summary block, which only
    /// inspects cold_knowledge (collections live in cold by convention).
    /// </summary>
    int CountKnowledgeCollections();

    /// <summary>
    /// List entries whose <c>metadata</c> JSON has matching key/value pairs.
    /// Keys MUST match <c>^[a-zA-Z_][a-zA-Z0-9_]*$</c>; filter MUST be non-empty.
    /// </summary>
    IReadOnlyList<Entry> ListByMetadata(
        Tier tier,
        ContentType type,
        IReadOnlyDictionary<string, string> metadataFilter,
        ListEntriesOpts? opts = null);

    /// <summary>
    /// Transactionally copy an entry to a new (tier, type) and delete the
    /// source row. Throws if the source row does not exist.
    /// </summary>
    void Move(
        Tier fromTier,
        ContentType fromType,
        Tier toTier,
        ContentType toType,
        string id);
}

/// <summary>
/// Options for <see cref="ISqliteStore.Insert"/>. Mirrors <c>InsertEntryOpts</c>
/// in <c>src-ts/db/entries.ts</c>.
/// </summary>
public sealed record InsertEntryOpts(
    string Content,
    string? Summary = null,
    string? Source = null,
    SourceTool? SourceTool = null,
    string? Project = null,
    IReadOnlyList<string>? Tags = null,
    string? ParentId = null,
    string? CollectionId = null,
    string? MetadataJson = null);

/// <summary>
/// Options for <see cref="ISqliteStore.Update"/>. Each non-null field becomes
/// a SET clause. <see cref="Touch"/> bumps <c>access_count</c> and
/// <c>last_accessed_at</c>. Mirrors <c>UpdateEntryOpts</c> in TS.
/// </summary>
public sealed record UpdateEntryOpts
{
    public string? Content { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Project { get; init; }
    public double? DecayScore { get; init; }
    public string? MetadataJson { get; init; }
    public bool Touch { get; init; }
    /// <summary>
    /// When true, <see cref="Summary"/>=null is written as SQL NULL instead of
    /// being ignored. Mirrors the TS distinction between "not in opts" and
    /// "explicit null". Same applies to <see cref="Project"/>.
    /// </summary>
    public bool ClearSummary { get; init; }
    public bool ClearProject { get; init; }
}

/// <summary>
/// Options for <see cref="ISqliteStore.List"/> and related queries.
/// </summary>
public sealed record ListEntriesOpts
{
    public string? Project { get; init; }
    public bool IncludeGlobal { get; init; }
    /// <summary>
    /// Whitelisted as <c>"&lt;col&gt; &lt;dir&gt;"</c>. Column must be one of
    /// <c>created_at</c>, <c>updated_at</c>, <c>last_accessed_at</c>,
    /// <c>access_count</c>, <c>decay_score</c>, <c>content</c>. Direction
    /// defaults to DESC. Invalid columns throw <see cref="ArgumentException"/>.
    /// </summary>
    public string? OrderBy { get; init; }
    public int? Limit { get; init; }
}
