// src/TotalRecall.Cli/Commands/CompactCommand.cs
//
// Plan 5 Task 5.9 — `total-recall compact`. Informational stub. Per the
// host-agnostic architecture (spec Flow 2), compaction LLM judgment lives
// in the host tool's subagent layer, NOT in the .NET binary. The server
// exposes session_context / memory_store / memory_delete / memory_promote
// primitives; host tools (Claude Code, Copilot CLI, etc.) orchestrate
// compaction via their own judgment mechanism.
//
// This CLI verb exists so `total-recall compact` doesn't 404 — it prints
// an explanation pointing users at the host-side compaction path and the
// CLI-side inspection commands (memory history / memory lineage).

using System;
using System.IO;
using System.Threading.Tasks;
using Spectre.Console;

namespace TotalRecall.Cli.Commands;

public sealed class CompactCommand : ICliCommand
{
    private readonly TextWriter? _out;

    public CompactCommand() { }

    // Test/composition seam.
    public CompactCommand(TextWriter output)
    {
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Name => "compact";
    public string? Group => null;
    public string Description => "Manual compaction trigger (host-tool managed)";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        if (args.Length > 0)
        {
            var a = args[0];
            // --help is handled by the dispatcher, but tolerate it just in case.
            if (a == "--help" || a == "-h") return 0;
            Console.Error.WriteLine($"compact: unknown argument '{a}'");
            return 2;
        }

        // Intentionally plain writes rather than AnsiConsole.MarkupLine so
        // that in-process Console.SetOut capture from tests works reliably
        // (see CliApp help-rendering note). The output is still the same
        // explanatory block specified in Task 5.9.
        var lines = new[]
        {
            "Compaction is driven by the host tool's LLM layer (e.g., Claude Code's",
            "compactor subagent), not the total-recall CLI. See spec Flow 2: the",
            "server exposes session_context, memory_store, memory_delete, and",
            "memory_promote primitives, and the host orchestrates compaction via",
            "its own judgment mechanism.",
            "",
            "To trigger compaction:",
            "  - In Claude Code: run /total-recall compact (or let the session-end",
            "    hook fire it automatically).",
            "  - From another host tool: call session_context to inspect hot tier,",
            "    then issue the appropriate memory_store/memory_delete/memory_promote",
            "    calls.",
            "",
            "To inspect compaction history from the CLI:",
            "  - total-recall memory history        (recent movements)",
            "  - total-recall memory lineage <id>   (ancestry tree)",
        };

        foreach (var line in lines)
        {
            if (_out is not null) _out.WriteLine(line);
            else Console.Out.WriteLine(line);
        }
        return 0;
    }
}
