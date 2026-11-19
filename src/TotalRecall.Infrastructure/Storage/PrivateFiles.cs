using System;
using System.IO;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// Creates directories and files readable only by the current user (0700 / 0600 on Unix).
/// The mode is set at creation where the API allows it, so nothing is ever briefly
/// world-readable, and re-applied to a file that already exists. On Windows the files
/// are plain creates (the profile directory's ACL applies).
/// </summary>
public static class PrivateFiles
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode FileMode_ = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates <paramref name="path"/> and any missing parents (all 0700 on Unix). An
    /// existing directory is left as it is.
    /// </summary>
    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        var full = System.IO.Path.GetFullPath(path);
        if (Directory.Exists(full)) return;

        // CreateDirectory(path, mode) applies the mode to the leaf only, so make the parents first.
        var parent = System.IO.Path.GetDirectoryName(full.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent)) EnsureDirectory(parent);
        Directory.CreateDirectory(full, DirectoryMode);
    }

    /// <summary>
    /// Creates an empty file at <paramref name="path"/> (0600 on Unix). An existing file keeps
    /// its content and has its mode tightened to 0600.
    /// </summary>
    public static void CreateEmptyFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var _ = new FileStream(path, System.IO.FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.OpenOrCreate,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite,
            UnixCreateMode = FileMode_,
        };
        using (new FileStream(path, options)) { }

        // A file that already existed keeps its old mode (and the umask can only narrow, never widen).
        if (File.GetUnixFileMode(path) != FileMode_)
            File.SetUnixFileMode(path, FileMode_);
    }
}
