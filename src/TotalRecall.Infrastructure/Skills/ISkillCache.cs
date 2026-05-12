using TotalRecall.Infrastructure.Sync;

namespace TotalRecall.Infrastructure.Skills;

public interface ISkillCache
{
    // Existing — used by cortex pull (Phase 3 will widen its DTO).
    Task UpsertAsync(PluginSyncSkillDto skill, CancellationToken ct);
    Task RemoveAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<PluginSyncSkillDto>> GetAllAsync(CancellationToken ct);

    // New: local-scan write path. Looks up by natural key; assigns a local
    // GUID on insert. embedding is the row-vector serialized as little-endian
    // float32 bytes; null when embedding failed or is deferred.
    Task UpsertScannedAsync(
        ImportedSkill skill,
        string contentHash,
        byte[]? embedding,
        string? embedderFingerprint,
        CancellationToken ct);

    // New: mark every row whose natural key is NOT in keepNaturalKeys as orphaned.
    Task MarkOrphansAsync(
        IReadOnlyList<(string Name, string Scope, string ScopeId)> keepNaturalKeys,
        CancellationToken ct);

    // New: read paths.
    Task<CachedSkill?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<CachedSkill?> GetByNaturalKeyAsync(string name, string scope, string scopeId, CancellationToken ct);
    Task<IReadOnlyList<CachedSkill>> ListAllForSearchAsync(CancellationToken ct);

    // New: usage tracking.
    Task RecordInvocationAsync(Guid skillId, string? host, string? sessionId,
        DateTime occurredAt, CancellationToken ct);
}
