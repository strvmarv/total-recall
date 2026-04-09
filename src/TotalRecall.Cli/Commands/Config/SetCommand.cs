// src/TotalRecall.Cli/Commands/Config/SetCommand.cs
//
// Plan 5 Task 5.8 — `total-recall config set <key> <value> [--no-snapshot]`.
// Ports src-ts/tools/system-tools.ts:151-173 (config_set) and
// src-ts/config.ts:61-73 (saveUserConfig). Flow:
//   1) Coerce the raw <value> string to bool/long/double/string.
//   2) Optionally snapshot the current effective config into config_snapshots
//      with name "pre-change:<key>" — skipped gracefully if the DB doesn't
//      exist yet or --no-snapshot is passed.
//   3) Merge the override into the user config.toml via ConfigWriter.
//   4) Print "set <key> = <value>" and exit 0.

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands.Config;

public sealed class SetCommand : ICliCommand
{
    private readonly IConfigLoader? _loader;
    private readonly string? _userConfigPath;
    private readonly IConfigSnapshotStore? _snapshotStore;
    private readonly TextWriter? _out;

    public SetCommand() { }

    // Test/composition seam. Pass snapshotStore=null AND the user will rely on
    // --no-snapshot or the DB-open fallback path (which needs a real dbPath).
    public SetCommand(
        IConfigLoader loader,
        string userConfigPath,
        IConfigSnapshotStore? snapshotStore,
        TextWriter output)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _userConfigPath = userConfigPath ?? throw new ArgumentNullException(nameof(userConfigPath));
        _snapshotStore = snapshotStore;
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Name => "set";
    public string? Group => "config";
    public string Description => "Set a config key in the user config.toml (dotted path)";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        string? key = null;
        string? rawValue = null;
        bool noSnapshot = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--no-snapshot":
                    noSnapshot = true;
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"config set: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    if (key is null) { key = a; break; }
                    if (rawValue is null) { rawValue = a; break; }
                    Console.Error.WriteLine($"config set: unexpected positional '{a}'");
                    PrintUsage(Console.Error);
                    return 2;
            }
        }

        if (string.IsNullOrEmpty(key))
        {
            Console.Error.WriteLine("config set: <key> is required");
            PrintUsage(Console.Error);
            return 2;
        }
        if (rawValue is null)
        {
            Console.Error.WriteLine("config set: <value> is required");
            PrintUsage(Console.Error);
            return 2;
        }

        var coerced = Coerce(rawValue);

        IConfigLoader loader = _loader ?? new ConfigLoader();
        string userConfigPath = _userConfigPath
            ?? Path.Combine(ConfigLoader.GetDataDir(), "config.toml");
        var writer = _out ?? Console.Out;

        // --- Pre-change snapshot (best-effort) --------------------------
        if (!noSnapshot)
        {
            try
            {
                if (_snapshotStore is not null)
                {
                    var current = loader.LoadEffectiveConfig();
                    var json = ConfigJsonSerializer.Serialize(current);
                    _snapshotStore.CreateSnapshot(json, $"pre-change:{key}");
                }
                else
                {
                    var dbPath = ConfigLoader.GetDbPath();
                    if (!File.Exists(dbPath))
                    {
                        Console.Error.WriteLine(
                            $"config set: no DB at {dbPath}, skipping snapshot");
                    }
                    else
                    {
                        using var conn = SqliteConnection.Open(dbPath);
                        MigrationRunner.RunMigrations(conn);
                        var store = new ConfigSnapshotStore(conn);
                        var current = loader.LoadEffectiveConfig();
                        var json = ConfigJsonSerializer.Serialize(current);
                        store.CreateSnapshot(json, $"pre-change:{key}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"config set: snapshot failed: {ex.Message}");
                // Continue — snapshot is best-effort.
            }
        }

        // --- Write the override -----------------------------------------
        try
        {
            ConfigWriter.SaveUserOverride(userConfigPath, key, coerced);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"config set: failed to write {userConfigPath}: {ex.Message}");
            return 1;
        }

        writer.WriteLine($"set {key} = {FormatValueForDisplay(coerced)}");
        return 0;
    }

    /// <summary>
    /// Coerces a raw argv string into a TOML scalar. Order matters:
    /// bool first, then integer (long), then double, otherwise string.
    /// </summary>
    public static object Coerce(string raw)
    {
        if (raw == "true") return true;
        if (raw == "false") return false;

        if (IsIntegerLiteral(raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }

        if (raw.Contains('.')
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        return raw;
    }

    private static bool IsIntegerLiteral(string s)
    {
        if (s.Length == 0) return false;
        int start = 0;
        if (s[0] == '-' || s[0] == '+') start = 1;
        if (start >= s.Length) return false;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9') return false;
        }
        return true;
    }

    private static string FormatValueForDisplay(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => $"\"{s}\"",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall config set <key> <value> [--no-snapshot]");
    }
}
