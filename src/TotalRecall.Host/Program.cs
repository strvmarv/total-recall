namespace TotalRecall.Host;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Plan 1 stub. Real argv routing (serve vs CLI dispatch) lands in Plan 4 + Plan 5.
        // For now this just exits cleanly so the project compiles and AOT-publishes.
        if (args.Length == 0 || args[0] == "serve")
        {
            // TODO(Plan 6): wire AutoMigrationGuard before McpServer.RunAsync
            // MCP serve mode — implemented in Plan 4.
            System.Console.Error.WriteLine("total-recall: MCP server not yet implemented (Plan 4)");
            return 0;
        }

        System.Console.Error.WriteLine($"total-recall: CLI not yet implemented (Plan 5). args[0]={args[0]}");
        return 0;
    }
}
