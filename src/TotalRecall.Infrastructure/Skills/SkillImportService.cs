using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Sync;  // for CortexUnreachableException

namespace TotalRecall.Infrastructure.Skills;

public sealed class SkillImportService(
    IClaudeCodeSkillScanner scanner,
    ISkillClient client,
    ICustomDirsSkillScanner? customDirsScanner = null,
    ISkillCache? cache = null,
    IEmbedder? embedder = null) : ISkillImportService
{
    public async Task<SkillImportSummaryDto[]> ImportAsync(
        string? projectPath, CancellationToken ct)
    {
        var claudeScan = await scanner.ScanAsync(projectPath, ct);

        var allSkills = new List<ImportedSkill>(claudeScan.Skills);
        var allErrors = new List<ScanError>(claudeScan.Errors);

        if (customDirsScanner is not null)
        {
            var customScan = await customDirsScanner.ScanAsync(ct);
            allSkills.AddRange(customScan.Skills);
            allErrors.AddRange(customScan.Errors);
        }

        // Local-first: write every scanned skill to the cache (when configured).
        if (cache is not null && embedder is not null)
        {
            var fingerprint = FormatFingerprint(embedder.Descriptor);
            foreach (var s in allSkills)
            {
                var hash = SkillContentHash.Compute(s.Content ?? string.Empty);
                byte[]? embBytes = null;
                try
                {
                    var vec = embedder.Embed($"{s.Name} {s.Description} {s.Content}");
                    embBytes = new byte[vec.Length * 4];
                    Buffer.BlockCopy(vec, 0, embBytes, 0, embBytes.Length);
                }
                catch { /* keep embBytes null — keyword-only ranking until next pass */ }

                await cache.UpsertScannedAsync(s, hash, embBytes, fingerprint, ct).ConfigureAwait(false);
            }

            // Orphan any cache rows whose source files vanished from the scan.
            await cache.MarkOrphansAsync(
                allSkills.Select(s => (s.Name, s.SuggestedScope, s.SuggestedScopeId)).ToList(), ct).ConfigureAwait(false);
        }

        // Best-effort cortex push. NullSkillClient is a no-op; CortexUnreachable returns synthetic error.
        try
        {
            await client.ImportAsync("claude-code", allSkills, ct);
        }
        catch (CortexUnreachableException ex)
        {
            return [new SkillImportSummaryDto(
                Adapter: "claude-code",
                Scanned: allSkills.Count,
                Imported: 0, Updated: 0, Unchanged: 0, Orphaned: 0,
                Errors: [$"cortex_unreachable: {ex.Message}"])];
        }

        var errors = allErrors.Select(e => $"{e.SourcePath}: {e.Error}").ToArray();
        return [new SkillImportSummaryDto(
            Adapter: "claude-code",
            Scanned: allSkills.Count,
            Imported: 0, Updated: 0, Unchanged: 0, Orphaned: 0,
            Errors: errors)];
    }

    /// <inheritdoc />
    public Task<SkillListResponseDto> ListVisibleAsync(CancellationToken ct) =>
        // skip: 0, take: int.MaxValue — single-page fetch to get all visible skills.
        client.ListAsync(scope: null, tags: null, skip: 0, take: int.MaxValue, ct);

    /// <inheritdoc />
    public Task<ClaudeCodeScanResult> ScanExtraDirsAsync(CancellationToken ct)
    {
        if (customDirsScanner is null)
            return Task.FromResult(new ClaudeCodeScanResult(
                Array.Empty<ImportedSkill>(), Array.Empty<ScanError>()));
        return customDirsScanner.ScanAsync(ct);
    }

    private static string FormatFingerprint(EmbedderDescriptor d) =>
        $"{d.Provider}/{d.Model}/{d.Revision}/{d.Dimensions}";
}
