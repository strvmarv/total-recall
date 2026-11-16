// src/TotalRecall.Infrastructure/WhatsNew/UpdateInstructions.cs
//
// How each host updates total-recall: the single source for the "Update: <how>"
// part of the available notice. Fixed text only; nothing from the network ever
// becomes part of it. Pure, no I/O.

using System;
using System.Collections.Generic;

namespace TotalRecall.Infrastructure.WhatsNew;

public static class UpdateInstructions
{
    // Each entry is one line of the form ["host"] = "how", because a JS test parses this
    // file with /^\s*\["([a-z-]+)"\]\s*=\s*"([^"]*)",?\s*$/gm. Keep that form, one entry per
    // line, in HostIds.Known order.
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["claude-code"] = "/total-recall:commands update",
        ["pi"] = "pi update",
        ["copilot-cli"] = "copilot plugin update total-recall@strvmarv-total-recall-marketplace",
        ["cursor"] = "see docs/hosts/cursor.md",
        ["codex"] = "see docs/hosts/codex-cli.md",
        ["gemini-cli"] = "npm install -g @strvmarv/total-recall",
        ["kiro"] = "npm install -g @strvmarv/total-recall",
        ["opencode"] = "npm install -g @strvmarv/total-recall",
        ["hermes"] = "npm install -g @strvmarv/total-recall",
        ["cline"] = "npm install -g @strvmarv/total-recall",
        ["windsurf"] = "npm install -g @strvmarv/total-recall",
        ["zed"] = "npm install -g @strvmarv/total-recall",
        ["vscode"] = "npm install -g @strvmarv/total-recall",
        ["other"] = "see docs/hosts/other-mcp.md",
    };

    public static string For(string? hostId) => All[HostIds.Normalize(hostId)];
}
