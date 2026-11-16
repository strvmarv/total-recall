using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class UserConfigFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tr-ucf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Render_OnAMissingParent_CreatesNothing()
    {
        IUserConfigFile file = new UserConfigFile(Path.Combine(_root, "deeper", "config.toml"));
        var text = file.Render([("storage.mode", "local")]);
        Assert.Contains("mode = \"local\"", text);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Write_CreatesTheParentDirectory_AndWritesTheText()
    {
        var path = Path.Combine(_root, "deeper", "config.toml");
        IUserConfigFile file = new UserConfigFile(path);
        file.Write("[storage]\nmode = \"git\"\n");
        Assert.Equal("[storage]\nmode = \"git\"\n", File.ReadAllText(path));
    }

    [Fact]
    public void RenderThenWrite_RoundTripsThroughTheSameFile()
    {
        var path = Path.Combine(_root, "config.toml");
        var file = new UserConfigFile(path);
        file.Write(file.Render([("git_sync.remote", "a:b/c")]));
        file.Write(file.Render([("git_sync.branch", "main")]));
        var text = File.ReadAllText(path);
        Assert.Contains("remote = \"a:b/c\"", text);
        Assert.Contains("branch = \"main\"", text);
    }
}
