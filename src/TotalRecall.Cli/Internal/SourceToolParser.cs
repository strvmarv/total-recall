// src/TotalRecall.Cli/Internal/SourceToolParser.cs
//
// Plan 5 Task 5.10 — parses the wire-format `source_tool` string produced
// by `memory export` back into the F# SourceTool DU. Cli-local because
// the canonical mapping in TotalRecall.Infrastructure.Storage
// (SourceToolMapping) is internal to Infrastructure. Keep this table in
// sync with Infrastructure/Storage/SqliteStore.cs:SourceToolMapping.Parse
// and the exporter serializer in Memory/ExportCommand.cs.

using TotalRecall.Core;

namespace TotalRecall.Cli.Internal;

internal static class SourceToolParser
{
    /// <summary>
    /// Parses <paramref name="value"/> into a <see cref="SourceTool"/>.
    /// Returns null for unknown/empty strings so callers can fall through
    /// to the "no source tool" branch.
    /// </summary>
    public static SourceTool? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value switch
        {
            "claude-code" => SourceTool.ClaudeCode,
            "copilot-cli" => SourceTool.CopilotCli,
            "opencode" => SourceTool.Opencode,
            "cursor" => SourceTool.Cursor,
            "cline" => SourceTool.Cline,
            "hermes" => SourceTool.Hermes,
            "manual" => SourceTool.ManualSource,
            _ => null,
        };
    }
}
