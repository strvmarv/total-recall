using System;
using System.Collections.Generic;

namespace TotalRecall.Web.Api;

/// <summary>
/// The curated set of MCP tool names reachable over the local web API.
/// Anything not in this set returns 404 from the dispatch endpoint, so the
/// UI can never invoke dangerous/irrelevant tools (e.g. migrate_to_remote).
/// Later UI plans extend this set as their sections land.
///
/// NOTE: some allowlisted tools are only registered in certain backend modes
/// (e.g. usage_status is SQLite/Cortex-only, not Postgres). Allowlisting a name
/// does not guarantee the tool exists in the active backend — the dispatch
/// endpoint re-checks ToolRegistry.TryGet after this allowlist check and
/// returns 404 when an allowlisted tool is not registered in the current mode.
/// </summary>
public static class ToolAllowlist
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "status",
        "usage_status",
        "eval_report",
        // memory read tools
        "memory_search",
        "memory_list",
        "memory_recent",
        "memory_history",
        "memory_get",
        "memory_inspect",
        // memory write/curate tools — reachable only with the per-launch token
        "memory_lineage",
        "memory_update",
        "memory_pin",
        "memory_unpin",
        "memory_promote",
        "memory_demote",
        "memory_delete",
        "kb_list_collections",
        "config_get",
    };

    public static bool IsAllowed(string name) =>
        !string.IsNullOrEmpty(name) && Allowed.Contains(name);

    public static IReadOnlyCollection<string> All => Allowed;
}
