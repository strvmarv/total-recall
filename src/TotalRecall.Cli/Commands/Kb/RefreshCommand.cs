// src/TotalRecall.Cli/Commands/Kb/RefreshCommand.cs
//
// Plan 5 Task 5.7 — `total-recall kb refresh <collection_id>`. Ports
// src-ts/tools/kb-tools.ts:218-290 (kb_refresh). Reads the collection's
// metadata.source_path, deletes all its children + the collection row +
// corresponding vector embeddings, then re-ingests the source via
// IFileIngester.IngestFile or IFileIngester.IngestDirectory.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Cli.Internal;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands.Kb;

public sealed class RefreshCommand : ICliCommand
{
    private readonly ISqliteStore? _store;
    private readonly IVectorSearch? _vec;
    private readonly IFileIngester? _ingester;
    private readonly TextWriter? _out;

    public RefreshCommand() { }

    // Test/composition seam. The IEmbedder dependency stays inside the
    // injected IFileIngester — test fakes can return canned results.
    public RefreshCommand(
        ISqliteStore store,
        IVectorSearch vec,
        IFileIngester ingester,
        TextWriter output)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _ingester = ingester ?? throw new ArgumentNullException(nameof(ingester));
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Name => "refresh";
    public string? Group => "kb";
    public string Description => "Re-ingest a knowledge base collection from its source path";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        string? collectionId = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith('-'))
            {
                Console.Error.WriteLine($"kb refresh: unknown argument '{a}'");
                PrintUsage(Console.Error);
                return 2;
            }
            if (collectionId is not null)
            {
                Console.Error.WriteLine($"kb refresh: unexpected positional '{a}'");
                PrintUsage(Console.Error);
                return 2;
            }
            collectionId = a;
        }

        if (string.IsNullOrEmpty(collectionId))
        {
            Console.Error.WriteLine("kb refresh: <collection_id> is required");
            PrintUsage(Console.Error);
            return 2;
        }

        ISqliteStore store;
        IVectorSearch vec;
        IFileIngester ingester;
        MsSqliteConnection? owned = null;
        try
        {
            if (_store is not null && _vec is not null && _ingester is not null)
            {
                store = _store;
                vec = _vec;
                ingester = _ingester;
            }
            else
            {
                // Production wiring: open connection, build store + vec,
                // construct a FileIngester (which requires a HierarchicalIndex
                // and IngestValidator, both of which take store/vec/embedder/conn).
                var dbPath = Path.Combine(ConfigLoader.GetDataDir(), "total-recall.db");
                owned = SqliteConnection.Open(dbPath);
                MigrationRunner.RunMigrations(owned);
                store = new SqliteStore(owned);
                vec = new VectorSearch(owned);
                var embedder = EmbedderFactory.CreateProduction();
                var index = new HierarchicalIndex(store, embedder, vec, owned);
                var validator = new IngestValidator(embedder, vec, owned);
                ingester = new FileIngester(index, validator);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"kb refresh: failed to open db: {ex.Message}");
            return 1;
        }

        try
        {
            var entry = store.Get(Tier.Cold, ContentType.Knowledge, collectionId);
            if (entry is null)
            {
                Console.Error.WriteLine($"kb refresh: collection {collectionId} not found");
                return 1;
            }

            var sourcePath = ExtractSourcePath(entry.MetadataJson);
            if (string.IsNullOrEmpty(sourcePath))
            {
                Console.Error.WriteLine("kb refresh: Collection has no source_path in metadata; cannot refresh");
                return 1;
            }

            // Delete children (by ParentId OR CollectionId match, excluding the root itself).
            var all = store.List(Tier.Cold, ContentType.Knowledge, null);
            var children = new List<Entry>();
            foreach (var e in all)
            {
                if (e.Id == collectionId) continue;
                var isChild = false;
                if (FSharpOption<string>.get_IsSome(e.ParentId) && e.ParentId.Value == collectionId)
                    isChild = true;
                else if (FSharpOption<string>.get_IsSome(e.CollectionId) && e.CollectionId.Value == collectionId)
                    isChild = true;
                if (isChild) children.Add(e);
            }
            foreach (var child in children)
            {
                vec.DeleteEmbedding(Tier.Cold, ContentType.Knowledge, child.Id);
                store.Delete(Tier.Cold, ContentType.Knowledge, child.Id);
            }

            // Delete the root.
            vec.DeleteEmbedding(Tier.Cold, ContentType.Knowledge, collectionId);
            store.Delete(Tier.Cold, ContentType.Knowledge, collectionId);

            // Figure out if the source is a file or a directory.
            bool isDir;
            if (Directory.Exists(sourcePath))
            {
                isDir = true;
            }
            else if (File.Exists(sourcePath))
            {
                isDir = false;
            }
            else
            {
                Console.Error.WriteLine($"kb refresh: Source path does not exist: {sourcePath}");
                return 1;
            }

            int files;
            int chunks;
            if (isDir)
            {
                var result = ingester.IngestDirectory(sourcePath, null);
                files = result.DocumentCount;
                chunks = result.TotalChunks;
            }
            else
            {
                var result = ingester.IngestFile(sourcePath, null);
                files = 1;
                chunks = result.ChunkCount;
            }

            WriteOut($"refreshed {collectionId}: {files} files, {chunks} chunks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"kb refresh: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static string? ExtractSourcePath(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("source_path", out var prop)) return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteOut(string line)
    {
        if (_out is not null) _out.WriteLine(line);
        else Console.Out.WriteLine(line);
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall kb refresh <collection_id>");
    }
}
