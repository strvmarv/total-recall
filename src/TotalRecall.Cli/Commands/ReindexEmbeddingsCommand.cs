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

        Core.Config.TotalRecallConfig cfg;
        try { cfg = new ConfigLoader().LoadEffectiveConfig(); }
        catch (Exception ex) { Console.Error.WriteLine($"reindex-embeddings: failed to load config: {ex.Message}"); return Task.FromResult(1); }

        if (IsPostgres(cfg))
        {
            Console.Error.WriteLine(
                "reindex-embeddings: postgres backend not yet supported. " +
                "Re-embed by pointing at a fresh database and re-ingesting.");
            return Task.FromResult(1);
        }

        string resolved;
        try { resolved = dbPath ?? ConfigLoader.GetDbPath(); }
        catch (Exception ex) { Console.Error.WriteLine($"reindex-embeddings: invalid DB path: {ex.Message}"); return Task.FromResult(1); }
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

        Console.WriteLine($"total-recall: re-embedding {resolved} with {embedder.Descriptor.Model} ...");
        var sw = Stopwatch.StartNew();
        try
        {
            using var conn = SqliteConnection.Open(resolved);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);

            // Atomic reindex: a mid-run failure must roll the whole thing back so
            // the DB never ends up half-rewritten (a mix of old and new embedding
            // spaces). SqliteStore/VectorSearch create commands WITHOUT setting
            // .Transaction; Microsoft.Data.Sqlite throws if a SqliteTransaction
            // OBJECT is active and a command's .Transaction isn't set. Raw SQL
            // transaction control (BEGIN/COMMIT/ROLLBACK) does NOT create a
            // SqliteTransaction object, so it sidesteps that enforcement.
            void Exec(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
            Exec("BEGIN IMMEDIATE");
            try
            {
                var reindexer = new EmbeddingReindexer(store, vec, embedder);
                int n = reindexer.Reindex(Console.Out);
                EmbedderFingerprint.Restamp(store, embedder);
                Exec("COMMIT");
                sw.Stop();
                Console.WriteLine($"total-recall: re-embedded {n} entries in {sw.ElapsedMilliseconds}ms; fingerprint re-stamped.");
                return Task.FromResult(0);
            }
            catch
            {
                try { Exec("ROLLBACK"); } catch { /* best-effort */ }
                throw;
            }
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
        if (Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(s.Mode))
        {
            var m = s.Mode.Value;
            if (m == "postgres") return true;
            if (m == "cortex" || m == "local") return false;
            // Unknown mode with a connection string → treat as postgres to be safe.
            return Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(s.ConnectionString);
        }
        return Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(s.ConnectionString);
    }
}
