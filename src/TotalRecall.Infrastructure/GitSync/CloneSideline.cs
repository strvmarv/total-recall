using System.Globalization;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// Moves a clone directory out of the way by renaming it. A clone that must not be used (an earlier <c>init</c>'s
/// unpushed clone, a damaged clone, a clone of a repository the machine has left) may hold the only copy of
/// something, so it is never deleted: it becomes <c>&lt;name&gt;.&lt;label&gt;-&lt;UTC yyyyMMddHHmmss&gt;</c> beside
/// the original (<c>-2</c>, <c>-3</c> for a taken name).
/// </summary>
public static class CloneSideline
{
    /// <param name="directory">The directory to move; it must exist.</param>
    /// <param name="label">A short word (<c>unpushed</c>, <c>broken</c>, <c>left</c>, <c>superseded</c>).</param>
    /// <param name="now">The time to stamp the name with (the caller passes it; the class reads no clock).</param>
    /// <param name="move">The rename; <see cref="Directory.Move"/> when null (a seam for tests).</param>
    /// <returns>The new path.</returns>
    public static string Move(string directory, string label, DateTimeOffset now, Action<string, string>? move = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var source = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(source))
            throw new IOException($"the directory to move aside does not exist: {Path.GetFileName(source)}");

        var stamp = now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var baseName = $"{source}.{label}-{stamp}";
        var target = baseName;
        for (var n = 2; Directory.Exists(target) || File.Exists(target); n++)
            target = $"{baseName}-{n}";

        (move ?? Directory.Move)(source, target);
        return target;
    }
}
