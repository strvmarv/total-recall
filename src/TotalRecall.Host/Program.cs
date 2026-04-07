namespace TotalRecall.Host;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Plan 1 stub. Real argv routing (serve vs CLI dispatch) lands in Plan 4 + Plan 5.

        // Plan 2 temporary subcommand: generate tokenizer reference fixtures.
        // Removed at the end of Plan 2 (Task 2.13).
        if (args.Length > 0 && args[0] == "generate-tokenizer-fixtures")
        {
            var vocabPath = args.Length > 1
                ? args[1]
                : Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "models",
                    "all-MiniLM-L6-v2",
                    "vocab.txt");
            var outputPath = args.Length > 2
                ? args[2]
                : Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "tests",
                    "fixtures",
                    "embeddings",
                    "tokenizer-reference.json");
            return TotalRecall.Infrastructure.Embedding.FixtureGenerator.GenerateToPath(vocabPath, outputPath);
        }

        if (args.Length == 0 || args[0] == "serve")
        {
            // MCP serve mode — implemented in Plan 4.
            System.Console.Error.WriteLine("total-recall: MCP server not yet implemented (Plan 4)");
            return 0;
        }

        System.Console.Error.WriteLine($"total-recall: CLI not yet implemented (Plan 5). args[0]={args[0]}");
        return 0;
    }
}
