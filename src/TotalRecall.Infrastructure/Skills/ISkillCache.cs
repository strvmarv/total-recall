using TotalRecall.Infrastructure.Sync;

namespace TotalRecall.Infrastructure.Skills;

public interface ISkillCache
{
    Task UpsertAsync(PluginSyncSkillDto skill, CancellationToken ct);
    Task RemoveAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<PluginSyncSkillDto>> GetAllAsync(CancellationToken ct);
}
