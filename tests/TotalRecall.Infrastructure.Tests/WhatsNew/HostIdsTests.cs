using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public class HostIdsTests
{
    private static readonly string[] Expected =
    {
        "claude-code", "pi", "copilot-cli", "cursor", "codex", "gemini-cli", "kiro",
        "opencode", "hermes", "cline", "windsurf", "zed", "vscode", "other",
    };

    [Fact]
    public void Known_IsTheClosedList()
    {
        Assert.Equal(Expected, HostIds.Known);
        Assert.Equal("other", HostIds.Other);
    }

    [Fact]
    public void Normalize_EveryKnownId_PassesThrough()
    {
        foreach (var id in HostIds.Known)
        {
            Assert.Equal(id, HostIds.Normalize(id));
            Assert.Equal(id, HostIds.FromClientName(id));
            Assert.Equal(id, HostIds.Resolve(id, null));
        }
    }

    [Theory]
    [InlineData("Claude-Code ", "claude-code")]
    [InlineData("  PI", "pi")]
    [InlineData("VSCODE", "vscode")]
    public void Normalize_CaseAndSpaces_AreFolded(string input, string expected)
    {
        Assert.Equal(expected, HostIds.Normalize(input));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("CON")]
    [InlineData("claude-code\n")]
    [InlineData("claude-code\t")]
    [InlineData("claude-code\r\n")]
    [InlineData("\u212Airo")]
    [InlineData("hermes-mcp-client")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_UnknownOrHostile_ReturnsOther(string? input)
    {
        Assert.Equal("other", HostIds.Normalize(input));
    }

    [Fact]
    public void Normalize_LongString_ReturnsOther()
    {
        Assert.Equal("other", HostIds.Normalize(new string('a', 300)));
        Assert.Equal("other", HostIds.Normalize(new string('a', 300) + "claude-code"));
    }

    [Theory]
    [InlineData("hermes-mcp-client", "hermes")]
    [InlineData(" Hermes-MCP-Client ", "hermes")]
    [InlineData("claude-code", "claude-code")]
    [InlineData("claude-ai", "other")]
    [InlineData("../x", "other")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    public void FromClientName_MapsKnownNames(string? name, string expected)
    {
        Assert.Equal(expected, HostIds.FromClientName(name));
    }

    [Theory]
    [InlineData("pi", "hermes-mcp-client", "pi")]
    [InlineData("", "hermes-mcp-client", "hermes")]
    [InlineData(null, "hermes-mcp-client", "hermes")]
    [InlineData(null, null, "other")]
    [InlineData("", "", "other")]
    [InlineData("../x", "hermes-mcp-client", "other")]
    [InlineData("claude-code\n", "pi", "other")]
    public void Resolve_EnvWinsWhenNonEmpty_ElseClientName(string? env, string? client, string expected)
    {
        Assert.Equal(expected, HostIds.Resolve(env, client));
    }
}
