// src/TotalRecall.Infrastructure/Memory/PinnedBlockRenderer.cs
//
// Single source of truth for rendering the pinned-directive block injected at
// session start, on session refresh, and by the per-turn pinned-floor hook.
// Pinned content is rendered VERBATIM — no detail levels, no truncation.

using System;
using System.Collections.Generic;
using System.Text;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Memory;

public static class PinnedBlockRenderer
{
    public const string Header = "## Pinned directives (always follow)";

    public static (string Block, IReadOnlyList<(Tier, ContentType, string)> Ids) Render(
        IReadOnlyList<Entry> pinnedMemories,
        IReadOnlyList<Entry> pinnedKnowledge)
    {
        var total = pinnedMemories.Count + pinnedKnowledge.Count;
        if (total == 0)
            return (string.Empty, Array.Empty<(Tier, ContentType, string)>());

        var sb = new StringBuilder();
        sb.Append(Header);
        var ids = new List<(Tier, ContentType, string)>(total);

        foreach (var e in pinnedMemories)
        {
            sb.Append("\n- ").Append(e.Content);
            ids.Add((Tier.Pinned, ContentType.Memory, e.Id));
        }
        foreach (var e in pinnedKnowledge)
        {
            sb.Append("\n- ").Append(e.Content);
            ids.Add((Tier.Pinned, ContentType.Knowledge, e.Id));
        }
        return (sb.ToString(), ids);
    }
}
