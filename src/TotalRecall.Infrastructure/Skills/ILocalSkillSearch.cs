namespace TotalRecall.Infrastructure.Skills;

public interface ILocalSkillSearch
{
    Task<IReadOnlyList<SkillSearchHitDto>> SearchAsync(
        string query, IReadOnlyList<string>? tags, int limit, CancellationToken ct);
}
