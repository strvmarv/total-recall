using TotalRecall.Infrastructure.Config;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// The user's <c>config.toml</c> as the setup commands see it: render the text a set of keys would produce (no side
/// effect), then write it. Two steps so a value the writer cannot write fails before any network call, and so tests
/// can fail the write without file permissions.
/// </summary>
public interface IUserConfigFile
{
    /// <summary>The file's text after setting each key in order. Writes nothing and creates nothing.</summary>
    string Render(IReadOnlyList<(string Key, object Value)> keys);

    /// <summary>Writes the text, creating the parent directory first.</summary>
    void Write(string text);
}

/// <summary>The production <see cref="IUserConfigFile"/> over one file path (the file is rewritten without comments, as <c>config set</c> does).</summary>
public sealed class UserConfigFile(string path) : IUserConfigFile
{
    public string Render(IReadOnlyList<(string Key, object Value)> keys) => ConfigWriter.RenderUserOverrides(path, keys);

    public void Write(string text)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        File.WriteAllText(path, text);
    }
}
