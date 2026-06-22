// src/TotalRecall.Infrastructure/Memory/PinnedScope.cs
//
// Single home of the project-scoped pinned-injection policy. Maps
// (detected project, feature flag) → the ListEntriesOpts to use when
// listing pinned entries. Returning null means "list with no opts" — the
// legacy all-pins path used when scoping is disabled.

using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Memory;

public static class PinnedScope
{
    /// <summary>
    /// Builds the pinned-tier list filter:
    /// <list type="bullet">
    /// <item>scoping off → <c>null</c> (all pins inject)</item>
    /// <item>scoping on, project detected → project pins + globals</item>
    /// <item>scoping on, no project → globals only (fail-closed)</item>
    /// </list>
    /// </summary>
    public static ListEntriesOpts? OptsFor(string? project, bool enabled)
    {
        if (!enabled) return null;
        if (project is not null)
            return new ListEntriesOpts { Project = project, IncludeGlobal = true };
        return new ListEntriesOpts { GlobalOnly = true };
    }
}
