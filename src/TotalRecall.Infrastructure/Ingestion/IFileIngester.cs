// src/TotalRecall.Infrastructure/Ingestion/IFileIngester.cs
//
// Plan 4 Task 4.9 — minimal seam extracted from FileIngester so KB ingest
// handlers in TotalRecall.Server (kb_ingest_file, kb_ingest_dir) can take a
// test double rather than the concrete class. FileIngester is sealed and
// owns a HierarchicalIndex + IngestValidator (which in turn hold a live
// SQLite connection, vector search, and embedder), so unit-testing the
// handlers against the real class would require a full infra stack. The
// interface exposes only the two entry points the handlers call.

namespace TotalRecall.Infrastructure.Ingestion;

/// <summary>
/// Seam for file/directory ingestion used by KB MCP handlers. Implemented by
/// <see cref="FileIngester"/> in production and by recording fakes in the
/// Server handler tests.
/// </summary>
public interface IFileIngester
{
    /// <summary>Ingest a single file. See <see cref="FileIngester.IngestFile"/>.</summary>
    IngestFileResult IngestFile(string filePath, string? collectionId = null);

    /// <summary>Ingest an entire directory. See <see cref="FileIngester.IngestDirectory"/>.</summary>
    IngestDirectoryResult IngestDirectory(string dirPath, string? glob = null);
}
