namespace TotalRecall.Infrastructure.Skills;

/// <summary>
/// Resolves the current user's id as cortex sees it — the JWT <c>sub</c> claim.
/// Used by MCP handlers to build <c>user:{id}</c> scope filters.
/// </summary>
public interface ICurrentUserId
{
    /// <summary>Returns the UserId string matching cortex JWT <c>sub</c> claim.</summary>
    string GetUserId();
}
