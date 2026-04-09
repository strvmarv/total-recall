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

        // Step 1: migration guard. Runs against the data dir, not an open
        // connection — the guard renames the legacy DB out of the way BEFORE
        // we open a handle on it, so this must execute first.
        var dataDir = ConfigLoader.GetDataDir();
        try
        {
            System.IO.Directory.CreateDirectory(dataDir);
            var migrator = new TsDataMigrator(EmbedderFactory.CreateProduction());
            var guard = new AutoMigrationGuard(migrator);
            var guardResult = await guard.CheckAndMigrateAsync(dataDir, cts.Token)
                .ConfigureAwait(false);
            if (guardResult == GuardResult.MigrationFailed)
            {
                Console.Error.WriteLine("total-recall: aborting serve (migration failed)");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"total-recall: migration guard threw: {ex.Message}");
            return 1;
        }

        // Step 2: open production composition (shared Sqlite connection +
        // all Infrastructure singletons + populated ToolRegistry).
        ServerCompositionHandles handles;
        try
        {
            handles = ServerComposition.OpenProduction();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"total-recall: failed to open composition: {ex.Message}");
            return 1;
        }

        // Step 3: run the MCP server. Dispose handles on shutdown.
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
