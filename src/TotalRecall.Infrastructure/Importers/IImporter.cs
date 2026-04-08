using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Importers;

/// <summary>
/// Result of an importer's import call: how many entries were inserted vs.
/// skipped (typically due to dedupe), plus any non-fatal errors collected
/// during the run. Mirrors the TS <c>ImportResult</c> shape from
/// <c>src-ts/importers/importer.ts</c>.
/// </summary>
public sealed record ImportResult(
    int Imported,
    int Skipped,
    IReadOnlyList<string> Errors)
{
    /// <summary>Zero-count, no-error result. Useful as a default for
    /// no-op branches in the 7 host importers.</summary>
    public static ImportResult Empty { get; } = new(0, 0, Array.Empty<string>());
}

/// <summary>
/// File counts returned by <see cref="IImporter.Scan"/>. Lets the CLI /
/// host preview what an import would do without performing it. Mirrors
/// the TS <c>HostImporter.scan</c> return shape.
/// </summary>
public sealed record ImporterScanResult(
    int MemoryFiles,
    int KnowledgeFiles,
    int SessionFiles);

/// <summary>
/// Common interface for the 7 host-tool importers (Claude Code, Copilot
/// CLI, Opencode, Cursor, Cline, Hermes, ProjectDocs). Mirrors the TS
/// <c>HostImporter</c> shape from <c>src-ts/importers/importer.ts</c>.
///
/// Calls are synchronous: <see cref="IEmbedder.Embed"/> and
/// <see cref="ISqliteStore"/> are both sync in .NET, and file I/O goes
/// through <c>File.ReadAllText</c>; no async ceremony is warranted.
/// </summary>
public interface IImporter
{
    /// <summary>Stable host-tool name; used as <c>source_tool</c> in the DB.</summary>
    string Name { get; }

    /// <summary>Quick existence check — returns true if the host tool's data
    /// directory exists. Cheap; never throws.</summary>
    bool Detect();

    /// <summary>Counts importable files without performing the import.</summary>
    ImporterScanResult Scan();

    /// <summary>Imports the host tool's memory entries into the warm tier
    /// (or the tool-specific default tier). The optional
    /// <paramref name="project"/> scopes the import to a single project
    /// key when the host tool supports project-scoped memories.</summary>
    ImportResult ImportMemories(ISqliteStore store, IEmbedder embedder, string? project = null);

    /// <summary>Imports the host tool's knowledge entries (documentation,
    /// reference material) into the cold knowledge tier.</summary>
    ImportResult ImportKnowledge(ISqliteStore store, IEmbedder embedder);
}
