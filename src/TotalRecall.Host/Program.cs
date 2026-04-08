// Plan 5 Task 5.1 — argv router between MCP serve mode and CLI subcommand
// dispatch. Empty of production DI wiring: that's Plan 6's composition root
// territory (see Plan 4 carry-forward #9). The serve branch still prints a
// not-yet-wired notice; the CLI branch delegates to the hand-rolled
// TotalRecall.Cli.CliApp.Run() dispatcher (see CliApp.cs for the pivot
// rationale — Spectre.Console.Cli 0.55.0 was not AOT-clean in practice).

namespace TotalRecall.Host;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "serve")
        {
            // TODO(Plan 6): build the production ToolRegistry, wire
            // AutoMigrationGuard, instantiate McpServer, RunAsync().
            System.Console.Error.WriteLine("total-recall: MCP server composition not yet wired (Plan 6)");
            return 0;
        }

        return TotalRecall.Cli.CliApp.Run(args);
    }
}
