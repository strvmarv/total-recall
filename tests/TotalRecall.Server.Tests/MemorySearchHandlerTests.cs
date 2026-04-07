namespace TotalRecall.Server.Tests;

using Xunit;

// First handler-contract test for the memory_search MCP tool.
//
// This test is INTENTIONALLY RED in Plan 1. Plan 4 implements
// TotalRecall.Server.Handlers.MemorySearchHandler and turns it green.
//
// The test asserts the MCP response-shape contract:
//   1. A valid happy-path call (empty corpus, valid query) returns a
//      response with isError=false and a content array (possibly empty).
//   2. The handler does not throw on a valid input.
//   3. Result entries, if any, conform to the SearchResult shape with
//      score, tier, content_type, and entry fields populated.
public class MemorySearchHandlerTests
{
    [Fact(Skip = "TotalRecall.Server.Handlers.MemorySearchHandler not yet implemented (Plan 4)")]
    public void HappyPath_EmptyCorpus_ReturnsEmptyContentArray()
    {
        // Pending: handler does not exist yet.
        //
        // When Plan 4 lands, the body becomes roughly:
        //
        //   var fakeStore = new FakeSqliteStore();   // empty store
        //   var fakeEmbedder = new FakeEmbedder();
        //   var handler = new MemorySearchHandler(fakeStore, fakeEmbedder);
        //
        //   var args = JsonNode.Parse("""{"query":"hello","topK":10}""");
        //   var result = handler.Execute(args);
        //
        //   Assert.NotNull(result);
        //   var isError = result["isError"]?.GetValue<bool>() ?? false;
        //   Assert.False(isError);
        //   var content = result["content"]?.AsArray();
        //   Assert.NotNull(content);
        //   Assert.Empty(content);  // empty corpus -> no results
    }
}
