// TEMPORARY: fixture generator for the F# tokenizer port in Plan 2.
// Deleted at the end of Plan 2 (Task 2.13).
//
// Runs Microsoft.ML.Tokenizers.BertTokenizer with the all-MiniLM-L6-v2 vocab
// on a fixed corpus and writes (input, token_ids) pairs to a JSON file that
// the F# tokenizer tests load as their oracle.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML.Tokenizers;

namespace TotalRecall.Infrastructure.Embedding;

public static class FixtureGenerator
{
    private static readonly string[] Corpus = new[]
    {
        // Natural language (10)
        "The quick brown fox jumps over the lazy dog.",
        "Hello, world!",
        "Machine learning models predict outcomes.",
        "This is a simple sentence.",
        "Memory systems store information.",
        "Vectors represent text numerically.",
        "Embedding models map words to points.",
        "Retrieval augments generation.",
        "Transformers process sequences.",
        "Attention is all you need.",

        // Code identifiers (10)
        "snake_case_identifier",
        "camelCaseVariable",
        "PascalCaseClass",
        "kebab-case-name",
        "SCREAMING_SNAKE",
        "mixed_Case_123",
        "has_underscores_everywhere",
        "lowerCamelCase",
        "Class_With_Underscore",
        "trailing_",

        // File paths (5)
        "/usr/local/bin/node",
        "/home/user/projects/my-app/src/index.ts",
        "C:\\Users\\Dev\\Documents\\code.cs",
        "./relative/path/file.md",
        "~/.config/app/settings.json",

        // URLs with query strings (5)
        "https://example.com/path?query=value",
        "http://localhost:3000/api/search?q=test&limit=10",
        "https://api.github.com/repos/owner/name/issues?state=open",
        "ftp://files.example.org/public/download.tar.gz",
        "https://example.com/page#section-1",

        // CJK mixed (5)
        "Hello \u4e16\u754c",
        "\u65e5\u672c\u8a9e text mixed",
        "\u4e2d\u6587 characters",
        "\ud55c\uad6d\uc5b4 Korean",
        "\u6f22\u5b57 and kanji",

        // Punctuation edge cases (6)
        "key=value",
        "a > b",
        "a + b = c",
        "pointer->field",
        "lambda => expr",
        "`backticked`",

        // Long strings (3)
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "supercalifragilisticexpialidociousxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
        "looooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooongword",

        // Empty and whitespace (3)
        "",
        "   ",
        "\n\t",
    };

    public static int GenerateToPath(string vocabPath, string outputPath)
    {
        if (!File.Exists(vocabPath))
        {
            Console.Error.WriteLine($"vocab not found at {vocabPath}");
            return 1;
        }

        var options = new BertOptions
        {
            LowerCaseBeforeTokenization = true,
            ApplyBasicTokenization = true,
            IndividuallyTokenizeCjk = true,
        };
        var tokenizer = BertTokenizer.Create(vocabPath, options);

        var entries = new List<FixtureEntry>();
        foreach (var input in Corpus)
        {
            var tokenIds = tokenizer.EncodeToIds(input).ToArray();
            entries.Add(new FixtureEntry { Input = input, TokenIds = tokenIds });
        }

        var json = JsonSerializer.Serialize(
            new FixtureFile { Entries = entries.ToArray() },
            FixtureJsonContext.Default.FixtureFile);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, json);
        Console.Error.WriteLine($"wrote {entries.Count} entries to {outputPath}");
        return 0;
    }

    public sealed class FixtureFile
    {
        public FixtureEntry[] Entries { get; set; } = Array.Empty<FixtureEntry>();
    }

    public sealed class FixtureEntry
    {
        public string Input { get; set; } = "";
        public int[] TokenIds { get; set; } = Array.Empty<int>();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FixtureGenerator.FixtureFile))]
[JsonSerializable(typeof(FixtureGenerator.FixtureEntry))]
internal partial class FixtureJsonContext : JsonSerializerContext
{
}
