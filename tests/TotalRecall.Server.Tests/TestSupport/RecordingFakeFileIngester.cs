// Recording IFileIngester fake for Plan 4 Task 4.9 KB handler tests. Captures
// the arguments kb_ingest_file / kb_ingest_dir pass in so tests can assert
// pass-through behaviour, and returns a configurable result so the JSON
// response shape can be verified end-to-end without touching real chunks,
// indices, or SQLite.

using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Ingestion;

namespace TotalRecall.Server.Tests.TestSupport;

public sealed class RecordingFakeFileIngester : IFileIngester
{
    public sealed record IngestFileCall(string FilePath, string? CollectionId);
    public sealed record IngestDirCall(string DirPath, string? Glob);

    public List<IngestFileCall> FileCalls { get; } = new();
    public List<IngestDirCall> DirCalls { get; } = new();

    public IngestFileResult NextFileResult { get; set; } =
        new IngestFileResult(
            DocumentId: "doc-1",
            ChunkCount: 1,
            ValidationPassed: true,
            Validation: new ValidationResult(true, new[]
            {
                new ProbeResult(0, 0.9, true),
            }));

    public IngestDirectoryResult NextDirResult { get; set; } =
        new IngestDirectoryResult(
            CollectionId: "coll-1",
            DocumentCount: 1,
            TotalChunks: 1,
            Errors: Array.Empty<string>(),
            ValidationPassed: true,
            ValidationFailures: Array.Empty<string>());

    /// <summary>When set, IngestFile throws this exception instead of returning the stub result.</summary>
    public Exception? FileThrow { get; set; }

    public IngestFileResult IngestFile(string filePath, string? collectionId = null)
    {
        FileCalls.Add(new IngestFileCall(filePath, collectionId));
        if (FileThrow is not null) throw FileThrow;
        return NextFileResult;
    }

    public IngestDirectoryResult IngestDirectory(string dirPath, string? glob = null)
    {
        DirCalls.Add(new IngestDirCall(dirPath, glob));
        return NextDirResult;
    }
}
