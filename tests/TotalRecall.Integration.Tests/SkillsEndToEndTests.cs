using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Integration.Tests;

/// <summary>
/// End-to-end round-trip test for Plan 2: scanner → CortexSkillClient →
/// cortex /api/me/skills/import → search → delete. Requires a running cortex
/// reachable via CORTEX_URL + CORTEX_PAT env vars (same setup as the memory
/// sync integration test). The test seeds a temp .claude/skills/ dir, runs
/// the pipeline once, asserts the import appeared in cortex via search, then
/// deletes it so the test is idempotent.
/// </summary>
[Trait("Category", "Integration")]
public class SkillsEndToEndTests : IDisposable
{
    private readonly string _cortexUrl = Environment.GetEnvironmentVariable("CORTEX_URL") ?? "http://localhost:5000";
    private readonly string _cortexPat = Environment.GetEnvironmentVariable("CORTEX_PAT") ?? "tr_test";
    private readonly string _tempRoot;

    public SkillsEndToEndTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tr-skills-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ImportScanSearchDelete_RoundTrip()
    {
        // Arrange — seed {tempRoot}/.claude/skills/e2e-demo/SKILL.md
        var uniqueName = "e2e-demo-" + Guid.NewGuid().ToString("N")[..8];
        var skillDir = Path.Combine(_tempRoot, ".claude", "skills", uniqueName);
        Directory.CreateDirectory(skillDir);
        var skillBody = $"""
---
name: {uniqueName}
description: integration-test skill for Plan 2 end-to-end
---

Skill body for {uniqueName}. Contains a distinctive keyword: haystackneedle.
""";
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), skillBody);

        var scanner = new ClaudeCodeSkillScanner();
        var client = CortexSkillClient.Create(_cortexUrl, _cortexPat, TimeSpan.FromSeconds(30));
        var service = new SkillImportService(scanner, client);

        // Act 1 — scan + push
        var summaries = await service.ImportAsync(projectPath: _tempRoot, CancellationToken.None);

        // Assert 1 — one row, at least one imported, zero errors
        var row = Assert.Single(summaries);
        Assert.Equal("claude-code", row.Adapter);
        Assert.True(row.Imported + row.Updated >= 1,
            $"expected Imported + Updated >= 1, got {row.Imported}+{row.Updated}. Errors: [{string.Join("; ", row.Errors)}]");
        Assert.Empty(row.Errors);

        // Act 2 — search, confirm our skill is visible to the caller
        var hits = await client.SearchAsync(
            query: "haystackneedle", scope: null, tags: null, limit: 10, CancellationToken.None);

        // Assert 2 — search returns our skill
        var hit = hits.FirstOrDefault(h => h.Name == uniqueName);
        Assert.NotNull(hit);

        // Act 3 — delete
        await client.DeleteAsync(hit!.Id, CancellationToken.None);

        // Assert 3 — the deleted skill no longer appears
        var afterDelete = await client.SearchAsync(
            query: "haystackneedle", scope: null, tags: null, limit: 10, CancellationToken.None);
        Assert.DoesNotContain(afterDelete, h => h.Id == hit.Id);
    }

    [Fact]
    public async Task Import_Idempotent_SecondRunReportsUnchanged()
    {
        // Arrange — seed a stable skill
        var uniqueName = "e2e-idem-" + Guid.NewGuid().ToString("N")[..8];
        var skillDir = Path.Combine(_tempRoot, ".claude", "skills", uniqueName);
        Directory.CreateDirectory(skillDir);
        var body = $"---\nname: {uniqueName}\ndescription: idempotency test\n---\n\nBody.";
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), body);

        var scanner = new ClaudeCodeSkillScanner();
        var client = CortexSkillClient.Create(_cortexUrl, _cortexPat, TimeSpan.FromSeconds(30));
        var service = new SkillImportService(scanner, client);

        try
        {
            // Act 1 — first run inserts
            var first = await service.ImportAsync(projectPath: _tempRoot, CancellationToken.None);
            Assert.Single(first);
            Assert.True(first[0].Imported + first[0].Updated >= 1);

            // Act 2 — second run with no changes should report neither imported nor updated for our skill.
            // Because Unchanged is reported per-adapter across ALL skills in the scan (other existing
            // skills in the dev box's home dir may skew it), we just assert the new-work counters
            // don't grow — specifically, Imported stays 0 and Updated stays 0 for the idempotent run.
            var second = await service.ImportAsync(projectPath: _tempRoot, CancellationToken.None);
            Assert.Single(second);
            Assert.Equal(0, second[0].Imported);
            Assert.Equal(0, second[0].Updated);
            Assert.Empty(second[0].Errors);
        }
        finally
        {
            // Cleanup — delete the skill we created
            var hits = await client.SearchAsync(uniqueName, null, null, 10, CancellationToken.None);
            var hit = hits.FirstOrDefault(h => h.Name == uniqueName);
            if (hit is not null)
            {
                try { await client.DeleteAsync(hit.Id, CancellationToken.None); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public async Task Search_CortexUnreachable_ThrowsCortexUnreachable()
    {
        var client = CortexSkillClient.Create("http://127.0.0.1:1", "tr_test", TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<CortexUnreachableException>(() =>
            client.SearchAsync("x", null, null, 5, CancellationToken.None));
    }
}
