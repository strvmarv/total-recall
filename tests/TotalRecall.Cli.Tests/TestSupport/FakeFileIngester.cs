// tests/TotalRecall.Cli.Tests/TestSupport/FakeFileIngester.cs
//
// Plan 5 Task 5.7 — recording fake for IFileIngester. Lets kb refresh tests
// assert that the command dispatches to the correct entry point (file vs
// directory) with the expected path, without spinning up a real ONNX
// embedder or SQLite database.

using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Ingestion;

namespace TotalRecall.Cli.Tests.TestSupport;

internal sealed class FakeFileIngester : IFileIngester
{
    public List<(string Path, string? CollectionId)> FileCalls { get; } = new();
    public List<(string Path, string? Glob)> DirCalls { get; } = new();

    public IngestFileResult FileResult { get; set; } =
        new IngestFileResult("doc-1", 3, true, new ValidationResult(true, Array.Empty<ProbeResult>()));

    public IngestDirectoryResult DirResult { get; set; } =
        new IngestDirectoryResult("coll-1", 2, 5, Array.Empty<string>(), true, Array.Empty<string>());

    public IngestFileResult IngestFile(string filePath, string? collectionId = null, string? scope = null)
    {
        FileCalls.Add((filePath, collectionId));
        return FileResult;
    }

    public IngestDirectoryResult IngestDirectory(string dirPath, string? glob = null, string? scope = null)
    {
        DirCalls.Add((dirPath, glob));
        return DirResult;
    }
}
