using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Ingestion;

/// <summary>
/// Per-file ingest outcome: the new document's id, how many chunks were
/// written, the post-ingest validation result, and a convenience flag
/// mirroring <see cref="ValidationResult.Passed"/>.
/// </summary>
public sealed record IngestFileResult(
    string DocumentId,
    int ChunkCount,
    bool ValidationPassed,
    ValidationResult Validation);

/// <summary>
/// Per-directory ingest outcome: the (new) collection id, counts, a list of
/// <c>"{path}: {message}"</c> error strings for files that threw during
/// ingest, and the aggregate validation status.
/// </summary>
public sealed record IngestDirectoryResult(
    string CollectionId,
    int DocumentCount,
    int TotalChunks,
    IReadOnlyList<string> Errors,
    bool ValidationPassed,
    IReadOnlyList<string> ValidationFailures);

/// <summary>
/// Orchestrates <see cref="Chunker.chunkFile"/> +
/// <see cref="HierarchicalIndex.AddDocumentToCollection"/> +
/// <see cref="IngestValidator.ValidateChunks"/>. Ports
/// <c>src-ts/ingestion/ingest.ts</c>.
///
/// The ingestable extension set, the chunker options (maxTokens=512,
/// overlapTokens=50), the parent-directory auto-collection behaviour, the
/// directory walker's hidden/node_modules skip logic, and the tiny glob
/// matcher (<c>*.ext</c> or exact filename) all match the TS reference.
/// </summary>
public sealed class FileIngester
{
    private static readonly HashSet<string> IngestableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".mdx", ".markdown", ".txt", ".rst",
        ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs",
        ".java", ".kt", ".cs", ".cpp", ".c", ".h",
        ".json", ".yaml", ".yml", ".toml",
    };

    private static readonly Chunker.ChunkerOptions DefaultChunkerOpts =
        new(512, FSharpOption<int>.Some(50));

    private readonly HierarchicalIndex _index;
    private readonly IngestValidator _validator;

    public FileIngester(HierarchicalIndex index, IngestValidator validator)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(validator);
        _index = index;
        _validator = validator;
    }

    /// <summary>
    /// Read <paramref name="filePath"/>, chunk it, write a document +
    /// chunks under <paramref name="collectionId"/> (or a new collection
    /// derived from the parent directory name if null), and return the
    /// document id along with a validation probe result.
    /// </summary>
    public IngestFileResult IngestFile(string filePath, string? collectionId = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var content = File.ReadAllText(filePath);
        var chunks = Chunker.chunkFile(content, filePath, DefaultChunkerOpts);
        var chunkArray = ListModule.ToArray(chunks);

        var resolvedCollectionId = collectionId;
        if (resolvedCollectionId is null)
        {
            var dirPath = Path.GetDirectoryName(filePath) ?? string.Empty;
            var dirName = Path.GetFileName(dirPath);
            resolvedCollectionId = _index.CreateCollection(
                new CreateCollectionOpts(dirName, dirPath));
        }

        var chunkInputs = new ChunkInput[chunkArray.Length];
        for (var i = 0; i < chunkArray.Length; i++)
            chunkInputs[i] = ConvertChunk(chunkArray[i]);

        var docId = _index.AddDocumentToCollection(new AddDocumentOpts(
            resolvedCollectionId, filePath, chunkInputs));

        var chunkContents = new string[chunkArray.Length];
        for (var i = 0; i < chunkArray.Length; i++)
            chunkContents[i] = chunkArray[i].Content;

        var validation = _validator.ValidateChunks(chunkContents, resolvedCollectionId);

        return new IngestFileResult(
            docId,
            chunkArray.Length,
            validation.Passed,
            validation);
    }

    /// <summary>
    /// Walk <paramref name="dirPath"/>, ingest every supported file into a
    /// single new collection named after the directory, and collect per-file
    /// errors + validation failures. A trailing <paramref name="glob"/>
    /// filter applies to the file basename.
    /// </summary>
    public IngestDirectoryResult IngestDirectory(string dirPath, string? glob = null)
    {
        ArgumentNullException.ThrowIfNull(dirPath);

        var trimmed = dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirName = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(dirName)) dirName = trimmed;
        var collectionId = _index.CreateCollection(new CreateCollectionOpts(dirName, dirPath));

        var files = WalkDirectory(dirPath);

        var documentCount = 0;
        var totalChunks = 0;
        var errors = new List<string>();
        var validationFailures = new List<string>();

        foreach (var filePath in files)
        {
            if (glob is not null)
            {
                var name = Path.GetFileName(filePath);
                if (!MatchesGlob(name, glob)) continue;
            }

            try
            {
                var result = IngestFile(filePath, collectionId);
                documentCount++;
                totalChunks += result.ChunkCount;
                if (!result.ValidationPassed)
                    validationFailures.Add(Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                errors.Add($"{filePath}: {ex.Message}");
            }
        }

        return new IngestDirectoryResult(
            collectionId,
            documentCount,
            totalChunks,
            errors,
            ValidationPassed: validationFailures.Count == 0,
            validationFailures);
    }

    // --- conversion helpers ----------------------------------------------

    private static ChunkInput ConvertChunk(Chunker.Chunk c)
    {
        IReadOnlyList<string>? headingPath = null;
        if (FSharpOption<FSharpList<string>>.get_IsSome(c.HeadingPath))
        {
            headingPath = ListModule.ToArray(c.HeadingPath.Value);
        }

        string? name = FSharpOption<string>.get_IsSome(c.Name) ? c.Name.Value : null;

        string? kind = null;
        if (FSharpOption<Parsers.CodeChunkKind>.get_IsSome(c.Kind))
        {
            kind = CodeChunkKindToString(c.Kind.Value);
        }

        return new ChunkInput(c.Content, headingPath, name, kind);
    }

    /// <summary>
    /// Exhaustive map of <see cref="Parsers.CodeChunkKind"/> (4 cases:
    /// <c>Import | Function | Class | Block</c>) to the lowercase strings
    /// the TS reference emits in chunk metadata.
    /// </summary>
    internal static string CodeChunkKindToString(Parsers.CodeChunkKind kind)
    {
        if (kind.IsImport) return "import";
        if (kind.IsFunction) return "function";
        if (kind.IsClass) return "class";
        if (kind.IsBlock) return "block";
        // Defensive: if the DU ever grows, fall back to the case name
        // lowercased rather than throwing mid-ingest.
        return kind.ToString().ToLowerInvariant();
    }

    // --- directory walker ------------------------------------------------

    private static List<string> WalkDirectory(string root)
    {
        var files = new List<string>();
        WalkDirectoryRecursive(root, files);
        return files;
    }

    private static void WalkDirectoryRecursive(string dir, List<string> files)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name)) continue;
            if (name[0] == '.') continue;
            if (name == "node_modules") continue;

            try
            {
                if (Directory.Exists(entry))
                {
                    WalkDirectoryRecursive(entry, files);
                }
                else if (File.Exists(entry))
                {
                    var ext = Path.GetExtension(entry).ToLowerInvariant();
                    if (IngestableExtensions.Contains(ext))
                        files.Add(entry);
                }
            }
            catch
            {
                // skip unreadable entries
            }
        }
    }

    internal static bool MatchesGlob(string filename, string glob)
    {
        // Matches src-ts/ingestion/ingest.ts: only *.ext or exact filename.
        if (glob.StartsWith("*.", StringComparison.Ordinal))
        {
            var ext = glob.Substring(1);
            return filename.EndsWith(ext, StringComparison.Ordinal);
        }
        return filename == glob;
    }
}
