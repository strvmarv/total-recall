using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Cli.Commands;

/// <summary>
/// <c>total-recall reindex-embeddings [--db &lt;path&gt;]</c>. Re-embeds every local
/// content row with the current (bge) embedder and re-stamps the embedder
/// fingerprint. Required after a local-embedder swap: the server's fingerprint
/// guard refuses to open a DB whose vectors are in a different model's space, so
/// this runs OFFLINE and opens the DB directly, bypassing the guard. Cortex is
/// untouched (content-only sync; cortex embeds independently with Cohere).
/// </summary>
public sealed class ReindexEmbeddingsCommand : ICliCommand
{
    public string Name => "reindex-embeddings";
    public string? Group => null;
    public string Description => "Re-embed all local memories + KB with the current model (after a model swap).";

    public Task<int> RunAsync(string[] args)
    {
        string? dbPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("reindex-embeddings: --db requires a value"); return Task.FromResult(2); }
                    dbPath = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"reindex-embeddings: unknown argument '{args[i]}'");
                    Console.Error.WriteLine("Usage: total-recall reindex-embeddings [--db <path>]");
                    return Task.FromResult(2);
            }
        }

        var cfg = new ConfigLoader().LoadEffectiveConfig();

        if (IsPostgres(cfg))
        {
            Console.Error.WriteLine(
                "reindex-embeddings: postgres backend not yet supported. " +
                "Re-embed by pointing at a fresh database and re-ingesting.");
            return Task.FromResult(1);
        }

        var resolved = dbPath ?? ConfigLoader.GetDbPath();
        if (!File.Exists(resolved))
        {
            Console.Error.WriteLine($"reindex-embeddings: database not found: {resolved}");
            return Task.FromResult(1);
        }

        IEmbedder embedder;
        try { embedder = EmbedderFactory.CreateFromConfig(cfg.Embedding); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"reindex-embeddings: failed to initialize embedder: {ex.Message}");
            return Task.FromResult(1);
        }

        Console.Out.WriteLine($"total-recall: re-embedding {resolved} with {embedder.Descriptor.Model} ...");
        var sw = Stopwatch.StartNew();
        try
        {
            using var conn = SqliteConnection.Open(resolved);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);

            var reindexer = new EmbeddingReindexer(store, vec, embedder);
            int n = reindexer.Reindex(Console.Out);

            EmbedderFingerprint.Restamp(store, embedder);
            sw.Stop();
            Console.Out.WriteLine($"total-recall: re-embedded {n} entries in {sw.ElapsedMilliseconds}ms; fingerprint re-stamped.");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"reindex-embeddings: failed: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static bool IsPostgres(Core.Config.TotalRecallConfig cfg)
    {
        if (!Microsoft.FSharp.Core.FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage))
            return false;
        var s = cfg.Storage.Value;
        if (Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(s.Mode) && s.Mode.Value == "postgres")
            return true;
        return Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(s.ConnectionString)
            && !(Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(s.Mode) && s.Mode.Value == "cortex");
    }
}
