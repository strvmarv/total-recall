// src/TotalRecall.Server/Handlers/EntryMapping.cs
//
// Plan 5 Task 5.0b — extracts helpers previously duplicated across
// MemoryGet/Search/Update/Delete and KbSearch handlers. Plan 4 carry-forward #3.
//
// Exposes:
//   - AllTablePairs: the 6 (Tier, ContentType) combinations used when
//     a tool has to sweep every table to locate an entry.
//   - TierName / ContentTypeName: F# DU → wire-format string.
//   - ToEntryDto: Core.Entry → EntryDto (applies OptString + Tags array).

using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;

namespace TotalRecall.Server.Handlers;

internal static class EntryMapping
{
    public static readonly (Tier Tier, ContentType Type)[] AllTablePairs =
    {
        (Tier.Hot,  ContentType.Memory),
        (Tier.Warm, ContentType.Memory),
        (Tier.Cold, ContentType.Memory),
        (Tier.Hot,  ContentType.Knowledge),
        (Tier.Warm, ContentType.Knowledge),
        (Tier.Cold, ContentType.Knowledge),
    };

    public static string TierName(Tier t) =>
        t.IsHot ? "hot" : t.IsWarm ? "warm" : "cold";

    public static string ContentTypeName(ContentType c) =>
        c.IsMemory ? "memory" : "knowledge";

    public static EntryDto ToEntryDto(Entry e) =>
        new(
            Id: e.Id,
            Content: e.Content,
            Summary: OptString(e.Summary),
            Source: OptString(e.Source),
            Project: OptString(e.Project),
            Tags: ListModule.ToArray(e.Tags),
            CreatedAt: e.CreatedAt,
            UpdatedAt: e.UpdatedAt,
            LastAccessedAt: e.LastAccessedAt,
            AccessCount: e.AccessCount,
            DecayScore: e.DecayScore);

    public static string? OptString(FSharpOption<string> opt) =>
        FSharpOption<string>.get_IsSome(opt) ? opt.Value : null;

    /// <summary>
    /// Returns the wire-format string for an optional <see cref="SourceTool"/>,
    /// matching the CLI <c>memory inspect --json</c> output. Null when no
    /// source tool is set.
    /// </summary>
    public static string? SourceToolName(FSharpOption<SourceTool> opt)
    {
        if (!FSharpOption<SourceTool>.get_IsSome(opt)) return null;
        var t = opt.Value;
        return t.IsClaudeCode ? "claude-code"
             : t.IsCopilotCli ? "copilot-cli"
             : t.IsOpencode ? "opencode"
             : t.IsCursor ? "cursor"
             : t.IsCline ? "cline"
             : t.IsHermes ? "hermes"
             : "manual";
    }
}
