// src/TotalRecall.Infrastructure/WhatsNew/HostIds.cs
//
// Closed list of host ids for the what's-new notice. A host string reaches a
// file name only after Normalize, so hostile input can never form a path.
// Pure, no I/O.

using System;
using System.Collections.Generic;

namespace TotalRecall.Infrastructure.WhatsNew;

public static class HostIds
{
    public const string Other = "other";

    private static readonly string[] KnownIds =
    {
        "claude-code", "pi", "copilot-cli", "cursor", "codex", "gemini-cli", "kiro",
        "opencode", "hermes", "cline", "windsurf", "zed", "vscode", Other,
    };

    public static IReadOnlyList<string> Known => KnownIds;

    /// <summary>
    /// Trims spaces only (a newline or tab is left in, so the value is unknown), lower-cases
    /// ASCII letters, and returns the matching known id or <see cref="Other"/>.
    /// </summary>
    public static string Normalize(string? id)
    {
        if (id is null) return Other;
        var folded = AsciiLower(id.Trim(' '));
        foreach (var known in KnownIds)
        {
            if (string.Equals(folded, known, StringComparison.Ordinal)) return known;
        }
        return Other;
    }

    /// <summary>Maps an MCP client name to a host id: <c>hermes-mcp-client</c> is <c>hermes</c>; a known id passes through.</summary>
    public static string FromClientName(string? name)
    {
        if (name is null) return Other;
        var folded = AsciiLower(name.Trim(' '));
        if (string.Equals(folded, "hermes-mcp-client", StringComparison.Ordinal)) return "hermes";
        return Normalize(folded);
    }

    /// <summary>An env value that is not empty after trimming spaces wins (normalised); otherwise the client name is mapped.</summary>
    public static string Resolve(string? envValue, string? clientName)
        => string.IsNullOrEmpty(envValue?.Trim(' ')) ? FromClientName(clientName) : Normalize(envValue);

    // ToLowerInvariant would fold U+212A (Kelvin sign) to 'k'; host ids are ASCII only.
    private static string AsciiLower(string s)
    {
        var needs = false;
        foreach (var ch in s)
        {
            if (ch is >= 'A' and <= 'Z') { needs = true; break; }
        }
        if (!needs) return s;
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var ch = src[i];
                span[i] = ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch;
            }
        });
    }
}
