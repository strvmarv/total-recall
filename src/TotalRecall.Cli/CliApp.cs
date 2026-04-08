// Plan 5 Task 5.1 (pivot) — hand-rolled subcommand dispatcher.
//
// Task 5.0 pinned Spectre.Console.Cli 0.55.0 and claimed AOT-clean, but that
// verification was hollow: no code actually instantiated CommandApp, so the
// library was trimmed as unreachable. The first real use triggered
// IL2026/IL3050/IL3053/IL3000/IL2104 warnings from inside the library itself.
// Plan 5 Task 5.0 pre-authorized a hand-rolled fallback — this is it.
//
// Design constraints:
//   * Zero reflection. Everything explicit and switch-based.
//   * Shape mirrors Spectre.Console.Cli conceptually (register commands in a
//     registry; dispatcher routes by verb) so we can swap back later with
//     minimal churn once upstream lands an AOT-clean release.
//   * Two-level dispatch: top-level verbs or group verbs (e.g. "eval report",
//     "memory promote", "kb list", "config get").
//   * `--help` / `--version` at top level; `<verb> --help` per command.
//   * Spectre.Console (the rendering package, without .Cli) IS AOT-clean on
//     its own, so AnsiConsole.WriteLine is used on the --version path to give
//     us a real AOT smoke test of the rendering dependency from Main.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Spectre.Console;

namespace TotalRecall.Cli;

/// <summary>
/// A single CLI leaf command. Task 5.2+ adds implementations and registers
/// them in <see cref="CliApp.BuildRegistry"/>.
/// </summary>
public interface ICliCommand
{
    /// <summary>Verb name (e.g. "migrate", "report", "promote").</summary>
    string Name { get; }

    /// <summary>
    /// Optional group name for two-level verbs (e.g. "eval" for "eval report").
    /// Null for top-level commands.
    /// </summary>
    string? Group { get; }

    /// <summary>One-line description shown in --help output.</summary>
    string Description { get; }

    /// <summary>
    /// Runs the command with the residual args (after the verb). Returns
    /// the process exit code. Async because some commands (e.g. migrate)
    /// perform I/O and embedding generation that are naturally awaitable.
    /// </summary>
    Task<int> RunAsync(string[] args);
}

public static class CliApp
{
    private const string AppName = "total-recall";
    private const string AppVersion = "0.1.0";
    private const int ExitOk = 0;
    private const int ExitUsage = 2;

    // Overrideable for tests. Null => use the real registry.
    private static IReadOnlyList<ICliCommand>? _overrideRegistry;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h" || args[0] == "help")
        {
            PrintTopLevelHelp();
            return ExitOk;
        }

        if (args[0] == "--version" || args[0] == "-v")
        {
            // Intentionally rendered through Spectre.Console so every AOT
            // publish smoke-tests the rendering dependency from Main.
            AnsiConsole.WriteLine($"{AppName} {AppVersion}");
            return ExitOk;
        }

        var registry = _overrideRegistry ?? BuildRegistry();
        var head = args[0];

        // Group dispatch: "eval report --flag" -> look up (group="eval", name="report").
        if (IsKnownGroup(registry, head))
        {
            if (args.Length < 2 || args[1] == "--help" || args[1] == "-h")
            {
                PrintGroupHelp(registry, head);
                return ExitOk;
            }

            var groupVerb = args[1];
            var groupCmd = FindCommand(registry, head, groupVerb);
            if (groupCmd is null)
            {
                Console.Error.WriteLine($"{AppName}: unknown command '{head} {groupVerb}'");
                PrintTopLevelHelp();
                return ExitUsage;
            }

            var rest = new string[args.Length - 2];
            Array.Copy(args, 2, rest, 0, rest.Length);
            if (HasHelpFlag(rest))
            {
                PrintCommandHelp(groupCmd);
                return ExitOk;
            }
            return groupCmd.RunAsync(rest).GetAwaiter().GetResult();
        }

        // Leaf dispatch: top-level verb.
        var leaf = FindCommand(registry, null, head);
        if (leaf is null)
        {
            Console.Error.WriteLine($"{AppName}: unknown command '{head}'");
            PrintTopLevelHelp();
            return ExitUsage;
        }

        var leafRest = new string[args.Length - 1];
        Array.Copy(args, 1, leafRest, 0, leafRest.Length);
        if (HasHelpFlag(leafRest))
        {
            PrintCommandHelp(leaf);
            return ExitOk;
        }
        return leaf.RunAsync(leafRest).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Test seam: swap in a synthetic registry for the duration of a test.
    /// Pass null to restore the real registry. Public because the CLI test
    /// project is a separate assembly; no InternalsVisibleTo needed.
    /// </summary>
    public static void SetRegistryForTestsInternal(IReadOnlyList<ICliCommand>? registry)
    {
        _overrideRegistry = registry;
    }

    /// <summary>
    /// Builds the production command registry. Task 5.2+ adds entries here.
    /// Intentionally a plain list — no plugin model, no reflection.
    /// </summary>
    private static IReadOnlyList<ICliCommand> BuildRegistry()
    {
        return new List<ICliCommand>
        {
            new Commands.MigrateCommand(),
            new Commands.Eval.BenchmarkCommand(),
            new Commands.Eval.ReportCommand(),
            new Commands.Eval.CompareCommand(),
            new Commands.Eval.SnapshotCommand(),
            new Commands.Eval.GrowCommand(),
            new Commands.Memory.DemoteCommand(),
            new Commands.Memory.ExportCommand(),
            new Commands.Memory.HistoryCommand(),
            new Commands.Memory.ImportCommand(),
            new Commands.Memory.InspectCommand(),
            new Commands.Memory.LineageCommand(),
            new Commands.Memory.PromoteCommand(),
            // TODO(Plan 5.7+): kb verbs, config verbs.
        };
    }

    private static bool IsKnownGroup(IReadOnlyList<ICliCommand> registry, string token)
    {
        foreach (var c in registry)
        {
            if (c.Group is not null && string.Equals(c.Group, token, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static ICliCommand? FindCommand(IReadOnlyList<ICliCommand> registry, string? group, string name)
    {
        foreach (var c in registry)
        {
            if (string.Equals(c.Group, group, StringComparison.Ordinal) &&
                string.Equals(c.Name, name, StringComparison.Ordinal))
            {
                return c;
            }
        }
        return null;
    }

    private static bool HasHelpFlag(string[] rest)
    {
        foreach (var a in rest)
        {
            if (a == "--help" || a == "-h")
            {
                return true;
            }
        }
        return false;
    }

    // NOTE: Help/group/command-help rendering intentionally uses plain
    // Console.WriteLine instead of AnsiConsole.WriteLine. Spectre.Console's
    // AnsiConsole binds its internal IAnsiConsole to Console.Out at type
    // init, which makes in-process Console.SetOut capture unreliable for
    // tests. The --version path still goes through AnsiConsole to give us
    // an AOT smoke test of the Spectre.Console rendering dependency from
    // Main (see Run above).
    private static void PrintTopLevelHelp()
    {
        var registry = _overrideRegistry ?? BuildRegistry();
        Console.WriteLine($"{AppName} {AppVersion}");
        Console.WriteLine("Usage: total-recall <command> [options]");
        Console.WriteLine("       total-recall <group> <command> [options]");
        Console.WriteLine("");
        Console.WriteLine("Options:");
        Console.WriteLine("  -h, --help       Show this help text");
        Console.WriteLine("  -v, --version    Show the total-recall version");
        Console.WriteLine("");

        if (registry.Count == 0)
        {
            Console.WriteLine("Commands: (no subcommands registered yet — Plan 5.2+)");
            return;
        }

        // Top-level leaves first, then groups.
        Console.WriteLine("Commands:");
        foreach (var c in registry)
        {
            if (c.Group is null)
            {
                Console.WriteLine($"  {c.Name,-16} {c.Description}");
            }
        }

        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in registry)
        {
            if (c.Group is not null && seenGroups.Add(c.Group))
            {
                Console.WriteLine($"  {c.Group,-16} <command> — use 'total-recall {c.Group} --help'");
            }
        }
    }

    private static void PrintGroupHelp(IReadOnlyList<ICliCommand> registry, string group)
    {
        Console.WriteLine($"Usage: total-recall {group} <command> [options]");
        Console.WriteLine("");
        Console.WriteLine("Commands:");
        foreach (var c in registry)
        {
            if (string.Equals(c.Group, group, StringComparison.Ordinal))
            {
                Console.WriteLine($"  {c.Name,-16} {c.Description}");
            }
        }
    }

    private static void PrintCommandHelp(ICliCommand cmd)
    {
        var header = cmd.Group is null
            ? $"Usage: total-recall {cmd.Name} [options]"
            : $"Usage: total-recall {cmd.Group} {cmd.Name} [options]";
        Console.WriteLine(header);
        Console.WriteLine("");
        Console.WriteLine(cmd.Description);
    }
}
