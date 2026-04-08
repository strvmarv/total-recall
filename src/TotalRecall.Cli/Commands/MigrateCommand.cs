using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Migration;

namespace TotalRecall.Cli.Commands;

/// <summary>
/// <c>total-recall migrate --from &lt;old&gt; --to &lt;new&gt; [--force]</c>.
///
/// Explicit user invocation of the TS→.NET one-time data migration.
/// Startup auto-migration lives in <c>TotalRecall.Server.AutoMigrationGuard</c>
/// and shares the same <see cref="IMigrateCommand"/> seam. This CLI wrapper
/// parses argv, validates paths (including an opt-in <c>--force</c> to
/// overwrite the target), constructs a production <see cref="OnnxEmbedder"/>,
/// runs <see cref="TsDataMigrator"/>, and prints a concise summary.
///
/// Two constructors:
///   - default: hard-wires <see cref="OnnxEmbedder"/> + <see cref="TsDataMigrator"/>
///     (accepts the model load cost — migrate is an explicit user action).
///   - injectable: accepts a pre-built <see cref="IMigrateCommand"/>, used
///     by tests to avoid loading the real ONNX model.
/// </summary>
public sealed class MigrateCommand : ICliCommand
{
    private readonly Func<IMigrateCommand>? _migratorFactory;

    public MigrateCommand()
    {
        _migratorFactory = null;
    }

    // Test/composition seam.
    public MigrateCommand(IMigrateCommand migrator)
    {
        ArgumentNullException.ThrowIfNull(migrator);
        _migratorFactory = () => migrator;
    }

    public string Name => "migrate";
    public string? Group => null;
    public string Description => "One-time TS→.NET database migration (re-embeds content).";

    public async Task<int> RunAsync(string[] args)
    {
        string? from = null;
        string? to = null;
        bool force = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--from":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("total-recall migrate: --from requires a value");
                        return 2;
                    }
                    from = args[++i];
                    break;
                case "--to":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("total-recall migrate: --to requires a value");
                        return 2;
                    }
                    to = args[++i];
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    Console.Error.WriteLine($"total-recall migrate: unknown argument '{a}'");
                    PrintUsage(Console.Error);
                    return 2;
            }
        }

        if (string.IsNullOrEmpty(from))
        {
            Console.Error.WriteLine("total-recall migrate: --from <path> is required");
            PrintUsage(Console.Error);
            return 2;
        }
        if (string.IsNullOrEmpty(to))
        {
            Console.Error.WriteLine("total-recall migrate: --to <path> is required");
            PrintUsage(Console.Error);
            return 2;
        }

        if (!File.Exists(from))
        {
            Console.Error.WriteLine($"total-recall migrate: source database does not exist: {from}");
            return 1;
        }

        if (File.Exists(to))
        {
            if (!force)
            {
                Console.Error.WriteLine(
                    $"total-recall migrate: target already exists: {to} (pass --force to overwrite)");
                return 2;
            }
            try
            {
                File.Delete(to);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"total-recall migrate: could not delete target '{to}': {ex.Message}");
                return 1;
            }
        }

        IMigrateCommand migrator;
        try
        {
            migrator = _migratorFactory is not null
                ? _migratorFactory()
                : BuildProductionMigrator();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"total-recall migrate: failed to initialize embedder: {ex.Message}");
            return 1;
        }

        Console.Out.WriteLine($"total-recall: migrating {from} -> {to}");
        var sw = Stopwatch.StartNew();
        MigrationResult result;
        try
        {
            result = await migrator.MigrateAsync(from, to, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"total-recall migrate: failed: {ex.Message}");
            return 1;
        }
        sw.Stop();

        if (!result.Success)
        {
            Console.Error.WriteLine(
                $"total-recall migrate: failed: {result.ErrorMessage ?? "unknown error"}");
            return 1;
        }

        Console.Out.WriteLine(
            $"total-recall: migrated {result.EntriesMigrated} entries in {sw.ElapsedMilliseconds}ms");
        return 0;
    }

    private static IMigrateCommand BuildProductionMigrator()
    {
        // Walk up from the binary's directory to find the bundled models/
        // tree. Mirrors OnnxEmbedderIntegrationTests.FindRepoRoot — Plan 6
        // will replace this with a proper composition-root resolve.
        var bundledModelsDir = FindBundledModelsDir();
        var registryPath = Path.Combine(bundledModelsDir, "registry.json");
        var registry = ModelRegistry.LoadFromFile(registryPath);

        // User override dir: $HOME/.total-recall/models (the TS reference
        // uses this path too). Falls back to a temp path if HOME is unset.
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Path.GetTempPath();
        var userModelsDir = Path.Combine(home, ".total-recall", "models");
        Directory.CreateDirectory(userModelsDir);

        var manager = new ModelManager(registry, bundledModelsDir, userModelsDir);
        var embedder = new OnnxEmbedder(manager, "all-MiniLM-L6-v2");
        return new TsDataMigrator(embedder, progress: Console.Out);
    }

    private static string FindBundledModelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models", "registry.json");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "models");
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate bundled models/ directory (walked up from "
            + AppContext.BaseDirectory + " looking for models/registry.json)");
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall migrate --from <oldDb> --to <newDb> [--force]");
    }
}
