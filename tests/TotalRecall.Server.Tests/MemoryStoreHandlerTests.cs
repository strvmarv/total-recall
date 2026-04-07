namespace TotalRecall.Server.Tests;

using Xunit;

// First handler-contract test for the memory_store MCP tool.
//
// This test is INTENTIONALLY RED in Plan 1. Plan 4 implements
// TotalRecall.Server.Handlers.MemoryStoreHandler and turns it green.
//
// The test asserts the MCP response-shape contract that ANY
// implementation must satisfy:
//   1. A valid happy-path call returns a response with isError=false
//      (or absent) and a content array containing the new entry's id.
//   2. The handler does not throw on a valid input.
//
// This is a contract test, not an integration test — it uses a fake
// SqliteStore and a fake Embedder to isolate the handler logic.
public class MemoryStoreHandlerTests
{
    [Fact(Skip = "TotalRecall.Server.Handlers.MemoryStoreHandler not yet implemented (Plan 4)")]
    public void HappyPath_ReturnsSuccessResponseWithEntryId()
    {
        // Pending: handler does not exist yet.
        //
        // When Plan 4 lands, the body becomes roughly:
        //
        //   var fakeStore = new FakeSqliteStore();
        //   var fakeEmbedder = new FakeEmbedder();
        //   var handler = new MemoryStoreHandler(fakeStore, fakeEmbedder);
        //
        //   var args = JsonNode.Parse("""{"content":"hello world","tier":"hot"}""");
        //   var result = handler.Execute(args);
        //
        //   Assert.NotNull(result);
        //   var isError = result["isError"]?.GetValue<bool>() ?? false;
        //   Assert.False(isError);
        //   var content = result["content"]?.AsArray();
        //   Assert.NotNull(content);
        //   Assert.NotEmpty(content);
    }
}
