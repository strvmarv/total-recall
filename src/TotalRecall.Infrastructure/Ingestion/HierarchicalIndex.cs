using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Data.Sqlite;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Ingestion;

/// <summary>
/// Options for <see cref="HierarchicalIndex.CreateCollection"/>. Mirrors the
/// <c>CreateCollectionOpts</c> shape from <c>src-ts/ingestion/hierarchical-index.ts</c>.
/// </summary>
public sealed record CreateCollectionOpts(string Name, string SourcePath);

/// <summary>
/// A single chunk to be added under a document. <c>HeadingPath</c>,
/// <c>Name</c>, and <c>Kind</c> are persisted in the chunk row's metadata
/// JSON; pass <c>null</c> for any field that does not apply.
/// </summary>
public sealed record ChunkInput(
    string Content,
    IReadOnlyList<string>? HeadingPath = null,
    string? Name = null,
    string? Kind = null);

/// <summary>
/// Options for <see cref="HierarchicalIndex.AddDocumentToCollection"/>.
/// </summary>
public sealed record AddDocumentOpts(
    string CollectionId,
    string SourcePath,
    IReadOnlyList<ChunkInput> Chunks);

/// <summary>
/// Resolved collection entry with the collection name extracted from
/// metadata. Returned by <see cref="HierarchicalIndex.GetCollection"/> and
/// <see cref="HierarchicalIndex.ListCollections"/> so consumers don't have to
/// re-parse the JSON.
/// </summary>
public sealed record CollectionEntry(Entry Entry, string Name);

/// <summary>
/// Builds collection -> document -> chunk trees in the cold/knowledge tier.
/// Ports <c>src-ts/ingestion/hierarchical-index.ts</c>. The tree relationship
/// uses <c>parent_id</c> (chunk -> document) and <c>collection_id</c>
/// (chunk/document -> collection); the metadata JSON carries a
/// <c>type</c> discriminator (<c>collection</c> | <c>document</c> | <c>chunk</c>).
///
/// All operations write to <c>cold_knowledge</c> exclusively.
/// </summary>
public sealed class HierarchicalIndex
{
    private const string Table = "cold_knowledge";

    private readonly ISqliteStore _store;
    private readonly IEmbedder _embedder;
    private readonly IVectorSearch _vectorSearch;
    private readonly MsSqliteConnection _conn;

    public HierarchicalIndex(
        ISqliteStore store,
        IEmbedder embedder,
        IVectorSearch vectorSearch,
        MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(conn);
        _store = store;
        _embedder = embedder;
        _vectorSearch = vectorSearch;
        _conn = conn;
    }

    // --- writes -----------------------------------------------------------

    /// <summary>
    /// Insert a new collection row in <c>cold_knowledge</c>. Content is
    /// <c>"Collection: {name}"</c> and metadata carries
    /// <c>{ type: "collection", name, source_path }</c>. Returns the new id.
    /// </summary>
    public string CreateCollection(CreateCollectionOpts opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var content = $"Collection: {opts.Name}";
        var metadataJson = EncodeCollectionMetadata(opts.Name, opts.SourcePath);

        var id = _store.Insert(
            Tier.Cold,
            ContentType.Knowledge,
            new InsertEntryOpts(
                Content: content,
                Source: opts.SourcePath,
                MetadataJson: metadataJson));

        var embedding = _embedder.Embed(content);
        _vectorSearch.InsertEmbedding(
            Tier.Cold,
            ContentType.Knowledge,
            id,
            embedding);

        return id;
    }

    /// <summary>
    /// Insert a new document row plus one chunk row per chunk. The document
    /// content is the first 500 chars of all chunks joined with <c>\n\n</c>.
    /// Returns the document id.
    /// </summary>
    public string AddDocumentToCollection(AddDocumentOpts opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(opts.Chunks);

        // Join all chunk content with \n\n, then take the first 500 chars as
        // the document's "preview" content (matches TS slice(0, 500)).
        var joined = string.Join("\n\n", AsContents(opts.Chunks));
        var docContent = joined.Length > 500
            ? joined.Substring(0, 500)
            : joined;

        var docMetadata = EncodeDocumentMetadata(opts.SourcePath, opts.Chunks.Count);

        var docId = _store.Insert(
            Tier.Cold,
            ContentType.Knowledge,
            new InsertEntryOpts(
                Content: docContent,
                Source: opts.SourcePath,
                CollectionId: opts.CollectionId,
                MetadataJson: docMetadata));

        var docEmbedding = _embedder.Embed(docContent);
        _vectorSearch.InsertEmbedding(
            Tier.Cold,
            ContentType.Knowledge,
            docId,
            docEmbedding);

        foreach (var chunk in opts.Chunks)
        {
            var chunkMetadata = EncodeChunkMetadata(chunk.HeadingPath, chunk.Name, chunk.Kind);
            var chunkId = _store.Insert(
                Tier.Cold,
                ContentType.Knowledge,
                new InsertEntryOpts(
                    Content: chunk.Content,
                    Source: opts.SourcePath,
                    ParentId: docId,
                    CollectionId: opts.CollectionId,
                    MetadataJson: chunkMetadata));

            var chunkEmbedding = _embedder.Embed(chunk.Content);
            _vectorSearch.InsertEmbedding(
                Tier.Cold,
                ContentType.Knowledge,
                chunkId,
                chunkEmbedding);
        }

        return docId;
    }

    private static IEnumerable<string> AsContents(IReadOnlyList<ChunkInput> chunks)
    {
        for (var i = 0; i < chunks.Count; i++)
            yield return chunks[i].Content;
    }

    // --- reads ------------------------------------------------------------

    /// <summary>
    /// Fetch a collection row by id. Returns <c>null</c> if the row does not
    /// exist OR if its metadata <c>type</c> is not <c>"collection"</c> — the
    /// id space is shared across collections, documents, and chunks.
    /// </summary>
    public CollectionEntry? GetCollection(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var entry = SqliteStore.RowToEntry(reader);
        var name = ExtractCollectionName(entry.MetadataJson);
        return name is null ? null : new CollectionEntry(entry, name);
    }

    /// <summary>
    /// List all rows in <c>cold_knowledge</c> whose metadata <c>type</c> is
    /// <c>"collection"</c>. Uses SQLite's <c>json_extract</c> filter, the
    /// same one <see cref="ISqliteStore.ListByMetadata"/> uses internally.
    /// </summary>
    public IReadOnlyList<CollectionEntry> ListCollections()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            $"SELECT * FROM {Table} " +
            "WHERE json_extract(metadata, '$.type') = 'collection'";
        var results = new List<CollectionEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = SqliteStore.RowToEntry(reader);
            var name = ExtractCollectionName(entry.MetadataJson) ?? "";
            results.Add(new CollectionEntry(entry, name));
        }
        return results;
    }

    /// <summary>
    /// Return all rows whose <c>parent_id</c> equals <paramref name="docId"/>.
    /// In practice these are all chunk rows because chunks are the only level
    /// of the cold_knowledge tree that uses <c>parent_id</c>.
    /// </summary>
    public IReadOnlyList<Entry> GetDocumentChunks(string docId)
    {
        ArgumentNullException.ThrowIfNull(docId);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Table} WHERE parent_id = $pid";
        cmd.Parameters.AddWithValue("$pid", docId);
        var results = new List<Entry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(SqliteStore.RowToEntry(reader));
        return results;
    }

    // --- metadata JSON encoding ------------------------------------------
    //
    // Hand-rolled to match the project pattern (see Telemetry/JsonStringWriter
    // and Storage/TagsJson). Three small fixed shapes; no reflection,
    // AOT-clean by construction.

    internal static string EncodeCollectionMetadata(string name, string sourcePath)
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"collection\",\"name\":");
        JsonStringWriter.AppendEscaped(sb, name);
        sb.Append(",\"source_path\":");
        JsonStringWriter.AppendEscaped(sb, sourcePath);
        sb.Append('}');
        return sb.ToString();
    }

    internal static string EncodeDocumentMetadata(string sourcePath, int chunkCount)
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"document\",\"source_path\":");
        JsonStringWriter.AppendEscaped(sb, sourcePath);
        sb.Append(",\"chunk_count\":");
        sb.Append(chunkCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append('}');
        return sb.ToString();
    }

    internal static string EncodeChunkMetadata(
        IReadOnlyList<string>? headingPath,
        string? name,
        string? kind)
    {
        // Match TS object literal: keys present in source order, undefined
        // values omitted entirely (TS JSON.stringify drops undefined).
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"chunk\"");
        if (headingPath is not null)
        {
            sb.Append(",\"heading_path\":");
            sb.Append(JsonStringWriter.EncodeStringArray(headingPath));
        }
        if (name is not null)
        {
            sb.Append(",\"name\":");
            JsonStringWriter.AppendEscaped(sb, name);
        }
        if (kind is not null)
        {
            sb.Append(",\"kind\":");
            JsonStringWriter.AppendEscaped(sb, kind);
        }
        sb.Append('}');
        return sb.ToString();
    }

    // --- metadata JSON decoding (collection name extraction) -------------
    //
    // For GetCollection / ListCollections we only need two things from the
    // metadata JSON: the type discriminator and (if it's a collection) the
    // name. A tiny scanner over the well-known shape avoids pulling in
    // System.Text.Json and any associated reflection/source-gen plumbing.

    /// <summary>
    /// Returns the <c>name</c> field of a collection metadata JSON, or
    /// <c>null</c> if the metadata's <c>type</c> is not <c>"collection"</c>
    /// or the <c>name</c> field is missing.
    /// </summary>
    internal static string? ExtractCollectionName(string metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;

        // We don't need a full JSON parser — just a string-literal extractor
        // for two known keys. The values were written by EncodeCollectionMetadata
        // and so honour standard JSON escapes.
        var type = ExtractStringField(metadataJson, "type");
        if (type != "collection") return null;
        return ExtractStringField(metadataJson, "name");
    }

    private static string? ExtractStringField(string json, string key)
    {
        // Locate the literal "{key}":" sequence. We don't tolerate keys that
        // appear inside other string values; the metadata shapes we read here
        // never contain a "name" or "type" substring inside a value because
        // EncodeCollectionMetadata only writes the name and source_path
        // fields, and the keys are unique.
        var needle = "\"" + key + "\":";
        var idx = json.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) return null;
        var i = idx + needle.Length;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return null;
        i++; // consume opening quote
        var sb = new StringBuilder();
        while (i < json.Length)
        {
            var c = json[i];
            if (c == '"') return sb.ToString();
            if (c == '\\' && i + 1 < json.Length)
            {
                var esc = json[i + 1];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 5 >= json.Length) return null;
                        var hex = json.Substring(i + 2, 4);
                        if (!int.TryParse(
                                hex,
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var code))
                            return null;
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: return null;
                }
                i += 2;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return null;
    }
}
