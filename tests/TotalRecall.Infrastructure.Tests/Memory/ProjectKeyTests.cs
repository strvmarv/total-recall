using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Memory;

public class ProjectKeyTests
{
    [Theory]
    [InlineData("git@github.com:radancy-pe/rai-ops-cortex.git", "radancy-pe/rai-ops-cortex")]
    [InlineData("https://github.com/strvmarv/total-recall.git", "strvmarv/total-recall")]
    [InlineData("https://github.com/strvmarv/total-recall", "strvmarv/total-recall")]
    [InlineData("ssh://git@github.com:22/owner/repo.git", "owner/repo")]
    [InlineData("https://github.com/strvmarv/total-recall/", "strvmarv/total-recall")]
    [InlineData("git@github.com:Radancy-PE/RAI-Ops-Cortex.git", "radancy-pe/rai-ops-cortex")]
    [InlineData("https://gitlab.com/group/subgroup/repo.git", "subgroup/repo")]
    public void Parses_known_remote_url_forms_to_lowercase_slug(string url, string expected)
        => Assert.Equal(expected, ProjectKey.FromRemoteUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("https://github.com/onlyowner")]
    public void Returns_null_for_unparseable_input(string? url)
        => Assert.Null(ProjectKey.FromRemoteUrl(url));
}
