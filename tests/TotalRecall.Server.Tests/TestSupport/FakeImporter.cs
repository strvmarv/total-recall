// tests/TotalRecall.Server.Tests/TestSupport/FakeImporter.cs
//
// Plan 6 Task 6.0d — minimal IImporter test double for
// ImportHostHandlerTests. Duplicated from the CLI test suite's FakeImporter
// rather than cross-referenced: Server.Tests does not depend on
// TotalRecall.Cli.Tests (and Cli.Tests is a test project, so cross-ref is
// not possible anyway).

using TotalRecall.Infrastructure.Importers;

namespace TotalRecall.Server.Tests.TestSupport;

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
        Name = name ?? throw new System.ArgumentNullException(nameof(name));
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
