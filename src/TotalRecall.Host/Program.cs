// src/TotalRecall.Host/Program.cs
//
// Plan 6 Task 6.3a — production composition root entry point.
//
// Top-level argv router:
//
//   total-recall               -> serve mode (MCP stdio server)
//   total-recall serve         -> serve mode (explicit)
//   total-recall <anything>    -> CLI dispatch via TotalRecall.Cli.CliApp
//
// Serve mode flow:
//
//   1. Run AutoMigrationGuard.CheckAndMigrateAsync against the user data
//      dir BEFORE opening any Sqlite handle on the target DB. If the guard
//      reports MigrationFailed, bail out with a non-zero exit code; the
//      server must not start on a half-migrated database.
//
//   2. Open the production composition via ServerComposition.OpenProduction().
//      This builds the Sqlite connection, runs schema migrations, constructs
//      all Infrastructure singletons (SqliteStore, VectorSearch, FtsSearch,
//      HybridSearch, OnnxEmbedder, HierarchicalIndex, IngestValidator,
//      FileIngester, CompactionLog, ImportLog, SessionLifecycle) and
//      registers all 32 production handlers in a ToolRegistry.
//
//   3. Construct McpServer with stdin/stdout and run the dispatch loop until
//      the client sends `shutdown`. Disposal of the composition handles
//      releases the Sqlite connection.
//
// Plan 5 carry-forward closures:
//   #1 — production composition root: closed here.
//   #7 — sync-over-async at CliApp dispatch: closed by the Main-is-async
//        flip below plus CliApp.RunAsync.

using System;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Migration;
using TotalRecall.Server;

namespace TotalRecall.Host;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "serve")
        {
            return await RunServeAsync().ConfigureAwait(false);
        }

        return await TotalRecall.Cli.CliApp.RunAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> RunServeAsync()
    {
        // Graceful Ctrl+C / SIGTERM — flip a CancellationTokenSource and let
        // McpServer's next ReadLine unwind naturally. McpServer.RunAsync does
        // not currently thread a CT through the dispatch loop (see its TODO);
        // the CTS is reserved for future work and also prevents the default
        // Console.CancelKeyPress behavior from hard-killing mid-flight work.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Step 1: resolve DB path eagerly. Honors TOTAL_RECALL_DB_PATH; any
        // validation failure must crash the server at startup with a clear
        // stderr message — no partial DB will be created downstream.
        string dbPath;
        try
        {
            dbPath = ConfigLoader.GetDbPath();
        }
        catch (SqliteDbPathException ex)
        {
            Console.Error.WriteLine($"total-recall: {ex.Message}");
            return 1;
        }

        // Ensure the data dir AND the dbPath parent dir both exist. When
        // TOTAL_RECALL_DB_PATH points outside TOTAL_RECALL_HOME, these are
        // different directories — config.toml, model cache, and exports
        // still live under the data dir; only the DB file relocates.
        try
        {
            System.IO.Directory.CreateDirectory(ConfigLoader.GetDataDir());
            var dbParent = System.IO.Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbParent))
            {
                System.IO.Directory.CreateDirectory(dbParent);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"total-recall: failed to create data directories: {ex.Message}");
            return 1;
        }

        // Step 2: migration guard. Runs against the resolved dbPath, not an
        // open connection — the guard renames the legacy DB out of the way
        // BEFORE we open a handle on it, so this must execute first.
        try
        {
            var migrator = new TsDataMigrator(EmbedderFactory.CreateProduction());
            var guard = new AutoMigrationGuard(migrator);
            var guardResult = await guard.CheckAndMigrateAsync(dbPath, cts.Token)
                .ConfigureAwait(false);
            if (guardResult == GuardResult.MigrationFailed)
            {
                Console.Error.WriteLine("total-recall: aborting serve (migration failed)");
                return 1;
            }
        }
        catch (Exception ex)
        {
            // Unwrap TypeInitializationException / DllNotFoundException chains
            // so the real failure surfaces — a bare ex.Message here produced
            // the "type initializer threw" mystery in 0.8.0-beta.4 when the
            // GitHub Release asset shipped only the executable without its
            // sibling libonnxruntime.dylib / vec0.dylib.
            ExceptionLogger.LogChain("total-recall: migration guard threw", ex);
            return 1;
        }

        // Step 3: open production composition (shared Sqlite connection +
        // all Infrastructure singletons + populated ToolRegistry).
        ServerCompositionHandles handles;
        try
        {
            handles = ServerComposition.OpenProduction(dbPath);
        }
        catch (Exception ex)
        {
            ExceptionLogger.LogChain("total-recall: failed to open composition", ex);
            return 1;
        }

        // Step 4: run the MCP server. Dispose handles on shutdown.
        try
        {
            var server = new McpServer(Console.In, Console.Out, handles.Registry);
            return await server.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"total-recall: server terminated with error: {ex.Message}");
            return 1;
        }
        finally
        {
            handles.Dispose();
        }
    }
}
