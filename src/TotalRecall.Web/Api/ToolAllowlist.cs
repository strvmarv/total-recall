using System;
using System.Collections.Generic;

namespace TotalRecall.Web.Api;

/// <summary>
/// The curated set of MCP tool names reachable over the local web API.
/// Anything not in this set returns 404 from the dispatch endpoint, so the
/// UI can never invoke dangerous/irrelevant tools (e.g. migrate_to_remote).
/// Later UI plans extend this set as their sections land.
/// </summary>
public static class ToolAllowlist
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "status",
        "usage_status",
        "eval_report",
        "memory_search",
        "memory_list",
        "memory_recent",
        "memory_get",
        "memory_inspect",
        "kb_list_collections",
        "config_get",
    };

    public static bool IsAllowed(string name) =>
        !string.IsNullOrEmpty(name) && Allowed.Contains(name);

    public static IReadOnlyCollection<string> All => Allowed;
}
