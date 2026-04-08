// src/TotalRecall.Infrastructure/Memory/SourceToolParser.cs
//
// Plan 6 Task 6.0a — promoted from src/TotalRecall.Cli/Internal/SourceToolParser.cs
// so the Server-side memory_import handler (which has no reference to the
// CLI project) can parse the wire-format source_tool string back into the
// F# SourceTool DU without duplicating the switch table. Keep in sync
// with the Infrastructure.Storage SourceToolMapping table and the
// ExportCommand/MemoryExportHandler serializers.

using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Memory;

public static class SourceToolParser
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
