using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Memory;

public class PinnedScopeTests
{
    [Fact]
    public void Disabled_returns_null_meaning_all_pins()
        => Assert.Null(PinnedScope.OptsFor("o/r", enabled: false));

    [Fact]
    public void Enabled_with_project_returns_project_plus_globals()
    {
        var opts = PinnedScope.OptsFor("o/r", enabled: true);
        Assert.NotNull(opts);
        Assert.Equal("o/r", opts!.Project);
        Assert.True(opts.IncludeGlobal);
        Assert.False(opts.GlobalOnly);
    }

    [Fact]
    public void Enabled_without_project_returns_globals_only()
    {
        var opts = PinnedScope.OptsFor(null, enabled: true);
        Assert.NotNull(opts);
        Assert.True(opts!.GlobalOnly);
        Assert.Null(opts.Project);
    }
}
