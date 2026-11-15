using System.Linq;
using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public class UpdateInstructionsTests
{
    [Fact]
    public void All_CoversExactlyTheKnownHostIds()
    {
        Assert.Equal(
            HostIds.Known.OrderBy(x => x, System.StringComparer.Ordinal),
            UpdateInstructions.All.Keys.OrderBy(x => x, System.StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("claude-code", "/total-recall:commands update")]
    [InlineData("pi", "pi update")]
    [InlineData("copilot-cli", "copilot plugin update total-recall@strvmarv-total-recall-marketplace")]
    [InlineData("cursor", "see docs/hosts/cursor.md")]
    [InlineData("codex", "see docs/hosts/codex-cli.md")]
    [InlineData("gemini-cli", "npm install -g @strvmarv/total-recall")]
    [InlineData("kiro", "npm install -g @strvmarv/total-recall")]
    [InlineData("opencode", "npm install -g @strvmarv/total-recall")]
    [InlineData("hermes", "npm install -g @strvmarv/total-recall")]
    [InlineData("cline", "npm install -g @strvmarv/total-recall")]
    [InlineData("windsurf", "npm install -g @strvmarv/total-recall")]
    [InlineData("zed", "npm install -g @strvmarv/total-recall")]
    [InlineData("vscode", "npm install -g @strvmarv/total-recall")]
    [InlineData("other", "see docs/hosts/other-mcp.md")]
    public void For_ExactTable(string host, string how)
    {
        Assert.Equal(how, UpdateInstructions.For(host));
        Assert.Equal(how, UpdateInstructions.All[host]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nonsense")]
    [InlineData("../x")]
    public void For_UnknownOrHostile_IsOtherPointer(string? host)
    {
        Assert.Equal("see docs/hosts/other-mcp.md", UpdateInstructions.For(host));
    }

    [Fact]
    public void For_MixedCaseAndPadded_NormalisesToTheKnownEntry()
    {
        Assert.Equal("/total-recall:commands update", UpdateInstructions.For("Claude-Code "));
    }

    [Fact]
    public void Pointers_NameTheHostsOwnPage()
    {
        Assert.Equal("see docs/hosts/cursor.md", UpdateInstructions.All["cursor"]);
        Assert.Equal("see docs/hosts/codex-cli.md", UpdateInstructions.All["codex"]);
        Assert.Equal("see docs/hosts/other-mcp.md", UpdateInstructions.All["other"]);
    }
}
