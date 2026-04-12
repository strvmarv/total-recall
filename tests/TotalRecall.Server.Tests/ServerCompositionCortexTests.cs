// tests/TotalRecall.Server.Tests/ServerCompositionCortexTests.cs
//
// Task 14 — verify cortex storage mode wiring in ServerComposition.

namespace TotalRecall.Server.Tests;

using TotalRecall.Infrastructure.Sync;
using Xunit;

public sealed class ServerCompositionCortexTests
{
    [Fact]
    public void OpenCortex_CreatesRoutingStore()
    {
        var handles = ServerComposition.OpenCortexForTest(
            sqliteDbPath: ":memory:",
            cortexUrl: "https://cortex.test",
            cortexPat: "tr_test123");

        Assert.NotNull(handles.Store);
        Assert.IsType<RoutingStore>(handles.Store);
    }
}
