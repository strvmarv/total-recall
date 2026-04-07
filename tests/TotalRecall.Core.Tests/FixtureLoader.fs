module TotalRecall.Core.Tests.FixtureLoader

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

// Loads test fixtures from the worktree's tests/fixtures/ directory.
// The test runner's cwd at test time is the Core.Tests build output directory,
// so we walk up the tree to find the worktree root.

let private findWorktreeRoot () =
    let rec search (dir: string) =
        if isNull dir then
            failwith "Could not find worktree root (no tests/fixtures/ directory found walking up)"
        elif Directory.Exists(Path.Combine(dir, "tests", "fixtures")) then
            dir
        else
            search (Path.GetDirectoryName(dir))
    search (Directory.GetCurrentDirectory())

let fixturesDir () =
    Path.Combine(findWorktreeRoot(), "tests", "fixtures")

[<CLIMutable>]
type TokenizerFixtureEntry = {
    Input: string
    TokenIds: int array
}

[<CLIMutable>]
type TokenizerFixtureFile = {
    Entries: TokenizerFixtureEntry array
}

let loadTokenizerFixtures () : TokenizerFixtureFile =
    let path = Path.Combine(fixturesDir(), "embeddings", "tokenizer-reference.json")
    if not (File.Exists path) then
        failwithf "Fixture not found: %s (run 'dotnet run --project src/TotalRecall.Host -- generate-tokenizer-fixtures' first)" path
    let json = File.ReadAllText(path)
    let options = JsonSerializerOptions()
    options.PropertyNameCaseInsensitive <- true
    JsonSerializer.Deserialize<TokenizerFixtureFile>(json, options)

let loadVocab () : Map<string, int> =
    // Read the vocab from tokenizer.json's model.vocab subtree (matches how
    // src-ts/embedding/embedder.ts loads it). vocab.txt is NOT tracked in
    // git; extracting it ad-hoc is a Task 2.1 temporary hack that should
    // NOT be repeated here.
    let tokenizerJsonPath =
        Path.Combine(findWorktreeRoot(), "models", "all-MiniLM-L6-v2", "tokenizer.json")
    if not (File.Exists tokenizerJsonPath) then
        failwithf "tokenizer.json not found: %s" tokenizerJsonPath
    let json = File.ReadAllText(tokenizerJsonPath)
    use doc = System.Text.Json.JsonDocument.Parse(json)
    let vocabElement =
        doc.RootElement.GetProperty("model").GetProperty("vocab")
    let mutable acc = Map.empty<string, int>
    for prop in vocabElement.EnumerateObject() do
        acc <- Map.add prop.Name (prop.Value.GetInt32()) acc
    acc
