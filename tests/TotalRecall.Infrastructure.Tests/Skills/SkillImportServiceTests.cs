using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Skills;

public class SkillImportServiceTests
{
    [Fact]
    public async Task ImportAsync_HappyPath_ReturnsCortexSummaryUnmodified()
    {
        var scanner = new FakeScanner(skills: new[] { BuildSkill("foo") });
        var client = new FakeClient(
            importResponse: new[] { Summary("claude-code", imported: 1) });
        var svc = new SkillImportService(scanner, client);

        var result = await svc.ImportAsync(projectPath: null, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(1, result[0].Imported);
        Assert.Empty(result[0].Errors);
        Assert.Equal("claude-code", client.LastAdapter);
    }

    [Fact]
    public async Task ImportAsync_MergesScanErrorsIntoFirstSummary()
    {
        var scanner = new FakeScanner(
            skills: new[] { BuildSkill("foo") },
            errors: new[] { new ScanError("/path/bad.md", "malformed yaml") });
        var client = new FakeClient(
            importResponse: new[] { Summary("claude-code", imported: 1) });
        var svc = new SkillImportService(scanner, client);

        var result = await svc.ImportAsync(projectPath: null, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Errors);
        Assert.Contains("/path/bad.md", result[0].Errors[0]);
        Assert.Contains("malformed yaml", result[0].Errors[0]);
    }

    [Fact]
    public async Task ImportAsync_CortexUnreachable_ReturnsSyntheticErrorRow()
    {
        var scanner = new FakeScanner(skills: new[] { BuildSkill("foo"), BuildSkill("bar") });
        var client = new FakeClient(
            importException: new CortexUnreachableException("connection refused"));
        var svc = new SkillImportService(scanner, client);

        var result = await svc.ImportAsync(projectPath: null, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("claude-code", result[0].Adapter);
        Assert.Equal(2, result[0].Scanned);
        Assert.Equal(0, result[0].Imported);
        Assert.Single(result[0].Errors);
        Assert.Contains("cortex_unreachable", result[0].Errors[0]);
        Assert.Contains("connection refused", result[0].Errors[0]);
    }

    // -- helpers & fakes ------------------------------------------------

    private static ImportedSkill BuildSkill(string name) => new(
        Name: name, Description: null, Content: "body",
        FrontmatterJson: "{}", Files: Array.Empty<ImportedSkillFile>(),
        SourcePath: $"/virt/{name}.md", SuggestedScope: "user",
        SuggestedScopeId: "user:u1", SuggestedTags: Array.Empty<string>());

    private static SkillImportSummaryDto Summary(string adapter, int imported = 0) =>
        new(adapter, Scanned: imported, Imported: imported,
            Updated: 0, Unchanged: 0, Orphaned: 0, Errors: Array.Empty<string>());

    private sealed class FakeScanner(
        IReadOnlyList<ImportedSkill>? skills = null,
        IReadOnlyList<ScanError>? errors = null) : IClaudeCodeSkillScanner
    {
        public Task<ClaudeCodeScanResult> ScanAsync(string? projectPath, CancellationToken ct)
            => Task.FromResult(new ClaudeCodeScanResult(
                skills ?? Array.Empty<ImportedSkill>(),
                errors ?? Array.Empty<ScanError>()));
    }

    private sealed class FakeClient : ISkillClient
    {
        private readonly SkillImportSummaryDto[]? _importResponse;
        private readonly Exception? _importException;
        public string? LastAdapter { get; private set; }
        public int LastSkillCount { get; private set; }

        public FakeClient(
            SkillImportSummaryDto[]? importResponse = null,
            Exception? importException = null)
        {
            _importResponse = importResponse;
            _importException = importException;
        }

        public Task<SkillImportSummaryDto[]> ImportAsync(
            string adapter, IReadOnlyList<ImportedSkill> skills, CancellationToken ct)
        {
            LastAdapter = adapter;
            LastSkillCount = skills.Count;
            if (_importException is not null) throw _importException;
            return Task.FromResult(_importResponse ?? Array.Empty<SkillImportSummaryDto>());
        }

        // Unused ISkillClient methods — throw NotImplementedException. Tests
        // only exercise the Import path on this fake.
        public Task<SkillSearchHitDto[]> SearchAsync(string query, string? scope, IReadOnlyList<string>? tags, int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillBundleDto?> GetByIdAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillBundleDto?> GetByNaturalKeyAsync(string name, string scope, string scopeId, CancellationToken ct) => throw new NotImplementedException();
        public Task<SkillListResponseDto> ListAsync(string? scope, IReadOnlyList<string>? tags, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeCustomDirsScanner(
        IReadOnlyList<ImportedSkill>? skills = null,
        IReadOnlyList<ScanError>? errors = null) : ICustomDirsSkillScanner
    {
        public Task<ClaudeCodeScanResult> ScanAsync(CancellationToken ct)
            => Task.FromResult(new ClaudeCodeScanResult(
                skills ?? Array.Empty<ImportedSkill>(),
                errors ?? Array.Empty<ScanError>()));
    }

    [Fact]
    public async Task ImportAsync_CustomDirsScanner_MergesSkillsBeforePush()
    {
        var scanner = new FakeScanner(skills: new[] { BuildSkill("from-claude") });
        var customScanner = new FakeCustomDirsScanner(skills: new[] { BuildSkill("from-custom") });
        var client = new FakeClient(
            importResponse: new[] { Summary("claude-code", imported: 2) });
        var svc = new SkillImportService(scanner, client, customScanner);

        var result = await svc.ImportAsync(projectPath: null, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(2, result[0].Imported);
        // Both skills were sent in one batch
        Assert.Equal(2, client.LastSkillCount);
    }

    [Fact]
    public async Task ImportAsync_CustomDirsScannerErrors_MergedIntoSummary()
    {
        var scanner = new FakeScanner(skills: Array.Empty<ImportedSkill>());
        var customScanner = new FakeCustomDirsScanner(
            skills: Array.Empty<ImportedSkill>(),
            errors: new[] { new ScanError("/custom/bad.md", "parse error") });
        var client = new FakeClient(
            importResponse: new[] { Summary("claude-code", imported: 0) });
        var svc = new SkillImportService(scanner, client, customScanner);

        var result = await svc.ImportAsync(projectPath: null, CancellationToken.None);

        Assert.Single(result[0].Errors);
        Assert.Contains("/custom/bad.md", result[0].Errors[0]);
    }

    [Fact]
    public async Task ImportAsync_NoCustomDirsScanner_BehavesAsBeforeForExistingTests()
    {
        var scanner = new FakeScanner(skills: new[] { BuildSkill("solo") });
        var client = new FakeClient(
            importResponse: new[] { Summary("claude-code", imported: 1) });
        var svc = new SkillImportService(scanner, client); // no custom scanner

        var result = await svc.ImportAsync(projectPath: null, CancellationToken.None);

        Assert.Equal(1, result[0].Imported);
    }

}
