// src/TotalRecall.Host/Program.cs
//
// Plan 6 Task 6.3a — production composition root entry point.
//
// Top-level argv router:
//
//   total-recall               -> serve mode (MCP stdio server)
//   total-recall serve         -> serve mode (explicit)
//   total-recall ui            -> local web UI server (TotalRecall.Web)
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
// ============================================================================
// BOOT-PATH RULE — DO NOT BLOCK THE MCP HANDSHAKE ON LENGTHY WORK.
// ============================================================================
// Steps 1-3 run BEFORE step 4 begins answering the MCP `initialize` handshake.
// The MCP client (e.g. Claude Code) enforces that handshake with a HARD
// wall-clock startup timeout: no retry, and writing to stderr does NOT extend
// it. So everything from Main() down through ServerComposition.OpenProduction()
// must complete fast and bounded — typically a second or two, never tens of
// seconds.
//
// Any operation whose cost scales with user data or the network — first-run
// binary download, embedder re-index / model-change migration, large host
// imports — MUST be deferred to the background and self-heal, NOT run
// synchronously on this path. Boot degraded-but-functional and converge later;
// never make the server's very ability to start depend on a long job finishing.
//
// This is not hypothetical. v3.0.2's cortex auto-migration re-embedded the whole
// local DB synchronously inside OpenProduction; on a populated DB (tens of
// thousands of rows) that blew the startup timeout, and because the re-index was
// atomic it rolled back and re-ran on EVERY boot -> an unrecoverable boot loop.
// v3.0.4 moved the re-index to a background worker for exactly this reason.
// Prior art for the pattern already exists: the first-run binary download
// backgrounds in bin/start.js / scripts/fetch-binary.js, and host import
// backgrounds in SessionLifecycle (session_start reports zero immediately).
// When you add new startup work, follow that pattern.
// ============================================================================
//
// Plan 5 carry-forward closures:
//   #1 — production composition root: closed here.
//   #7 — sync-over-async at CliApp dispatch: closed by the Main-is-async
//        flip below plus CliApp.RunAsync.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Migration;
using TotalRecall.Server;
using TotalRecall.Web;

namespace TotalRecall.Host;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "serve")
        {
            return await RunServeAsync().ConfigureAwait(false);
        }

        if (args[0] == "ui")
        {
            return await RunUiAsync(args).ConfigureAwait(false);
        }

        return await TotalRecall.Cli.CliApp.RunAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> RunServeAsync()
    {
        // MCP stdio protocol is UTF-8. Force both streams before any I/O so
        // Windows OEM code page (CP437/850) never touches the wire.
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

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
        //
        // INVARIANT: OpenProduction MUST return fast and bounded — step 4 below
        // starts answering the MCP handshake, which has a hard, no-retry startup
        // timeout. Do NOT add data-/network-scaled work (re-index, downloads,
        // bulk imports) on this synchronous path; defer it to a background worker
        // and boot degraded-but-functional. See the BOOT-PATH RULE in the file
        // header for the v3.0.2 boot-loop that motivated this.
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

    private static async Task<int> RunUiAsync(string[] args)
    {
        int port = 5577;
        string host = "127.0.0.1";
        bool open = true;
        string token = "";
        bool smoke = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out port) || port < 0 || port > 65535)
                    {
                        Console.Error.WriteLine($"total-recall ui: invalid --port value '{args[i]}' (expected 0-65535)");
                        return 2;
                    }
                    break;
                case "--host" when i + 1 < args.Length: host = args[++i]; break;
                case "--token" when i + 1 < args.Length: token = args[++i]; break;
                case "--port":
                case "--host":
                case "--token":
                    Console.Error.WriteLine($"total-recall ui: {args[i]} requires a value");
                    return 2;
                case "--no-open": open = false; break;
                case "--smoke": smoke = true; open = false; break;
                case "--help" or "-h":
                    Console.WriteLine("Usage: total-recall ui [--port N] [--host H] [--no-open] [--token T] [--smoke]");
                    Console.WriteLine("");
                    Console.WriteLine("Launch the local web UI (loopback only by default).");
                    Console.WriteLine("  --port N     Port to bind (default 5577; 0 = pick a free port)");
                    Console.WriteLine("  --host H     Bind host (default 127.0.0.1). Non-loopback exposes the UI on your network.");
                    Console.WriteLine("  --token T    Bearer token for /api/*. If omitted, an ephemeral token is generated and printed at startup.");
                    Console.WriteLine("  --no-open    Do not open the browser automatically");
                    Console.WriteLine("  --smoke      Boot, confirm /api/health, then exit (CI hook)");
                    return 0;
                default:
                    Console.Error.WriteLine($"total-recall ui: unknown argument '{args[i]}'");
                    return 2;
            }
        }

        if (!host.Equals("127.0.0.1", StringComparison.Ordinal)
            && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && host != "::1")
        {
            Console.Error.WriteLine(
                $"total-recall ui: WARNING binding non-loopback host '{host}' exposes the UI on your network. " +
                "Access requires the bearer token printed at startup (an ephemeral token is generated unless you pass --token).");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Migration-guard preamble — parity with RunServeAsync. WebUiServer.RunAsync
        // calls ServerComposition.OpenProduction(), whose contract requires the caller
        // to run AutoMigrationGuard FIRST so a legacy TS-format DB is renamed out of
        // the way BEFORE a Sqlite handle is opened on the target path. The guard is
        // idempotent: an already-migrated DB short-circuits at a marker check.

        // Step 1: resolve DB path eagerly. Honors TOTAL_RECALL_DB_PATH; any
        // validation failure must fail at startup with a clear stderr message.
        string dbPath;
        try
        {
            dbPath = ConfigLoader.GetDbPath();
        }
        catch (SqliteDbPathException ex)
        {
            Console.Error.WriteLine($"total-recall ui: {ex.Message}");
            return 1;
        }

        // Step 1.5: ensure the data dir AND the dbPath parent dir both exist.
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
            Console.Error.WriteLine($"total-recall ui: failed to create data directories: {ex.Message}");
            return 1;
        }

        // Step 2: migration guard. Runs against the resolved dbPath, not an open
        // connection — the guard renames the legacy DB out of the way BEFORE we
        // open a handle on it, so this must execute first.
        try
        {
            var migrator = new TsDataMigrator(EmbedderFactory.CreateProduction());
            var guard = new AutoMigrationGuard(migrator);
            var guardResult = await guard.CheckAndMigrateAsync(dbPath, cts.Token)
                .ConfigureAwait(false);
            if (guardResult == GuardResult.MigrationFailed)
            {
                Console.Error.WriteLine("total-recall ui: aborting ui (migration failed)");
                return 1;
            }
        }
        catch (Exception ex)
        {
            ExceptionLogger.LogChain("total-recall ui: migration guard threw", ex);
            return 1;
        }

        var options = new WebUiOptions(Port: port, Host: host, OpenBrowser: open, Token: token, Smoke: smoke);
        return await WebUiServer.RunAsync(options, cts.Token).ConfigureAwait(false);
    }
}
