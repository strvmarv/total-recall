// tests/TotalRecall.Cli.Tests/TestSupport/FakeImporter.cs
//
// Plan 5 Task 5.9 — minimal IImporter test double for
// ImportHostCommandTests. Seedable Name/Detect/Scan/ImportMemories/
// ImportKnowledge with per-method call counters so tests can assert the
// command skipped correctly on --source filtering / !Detect.

using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Importers;

namespace TotalRecall.Cli.Tests.TestSupport;

internal sealed class FakeImporter : IImporter
{
    public string Name { get; }
    public bool DetectResult { get; set; }
    public ImporterScanResult ScanResult { get; set; } =
        new ImporterScanResult(MemoryFiles: 0, KnowledgeFiles: 0, SessionFiles: 0);
    public ImportResult MemoriesResult { get; set; } = ImportResult.Empty;
    public ImportResult KnowledgeResult { get; set; } = ImportResult.Empty;

    public int DetectCalls { get; private set; }
    public int ScanCalls { get; private set; }
    public int ImportMemoriesCalls { get; private set; }
    public int ImportKnowledgeCalls { get; private set; }

    public FakeImporter(string name, bool detect = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        DetectResult = detect;
    }

    public bool Detect()
    {
        DetectCalls++;
        return DetectResult;
    }

    public ImporterScanResult Scan()
    {
        ScanCalls++;
        return ScanResult;
    }

    public ImportResult ImportMemories(string? project = null)
    {
        ImportMemoriesCalls++;
        return MemoriesResult;
    }

    public ImportResult ImportKnowledge()
    {
        ImportKnowledgeCalls++;
        return KnowledgeResult;
    }
}
