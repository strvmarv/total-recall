using System;
using System.Collections.Generic;

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
/// Concrete implementations take their dependencies (<c>ISqliteStore</c>,
/// <c>IEmbedder</c>, <c>IVectorSearch</c>, <c>ImportLog</c>) via
/// constructor injection. This matches the composition pattern used by
/// <c>HybridSearch</c> and friends and keeps the interface narrow.
///
/// Calls are synchronous: <see cref="Embedding.IEmbedder.Embed"/> and
/// <see cref="Storage.ISqliteStore"/> are both sync in .NET, and file I/O
/// goes through <c>File.ReadAllText</c>; no async ceremony is warranted.
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

    /// <summary>
    /// Imports the host tool's memory entries. The optional
    /// <paramref name="project"/> is the value stamped on the
    /// <c>Project</c> column of every imported entry — it does NOT scope
    /// the file scan; concrete importers walk all projects in the host
    /// tool's data directory regardless. Pass <c>null</c> for global
    /// memories.
    /// </summary>
    ImportResult ImportMemories(string? project = null);

    /// <summary>
    /// Imports the host tool's knowledge entries (documentation, plans,
    /// reference material). The destination tier and content-type are
    /// implementation-specific per host tool — see each concrete
    /// <see cref="IImporter"/> implementation's XML doc for the routing
    /// it uses.
    /// </summary>
    ImportResult ImportKnowledge();
}
