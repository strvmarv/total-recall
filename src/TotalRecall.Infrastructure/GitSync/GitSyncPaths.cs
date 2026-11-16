using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// Where git sync keeps its files for one database file and one remote:
/// <c>&lt;directory of the database&gt;/sync/&lt;dbKey&gt;/{lock, no-hooks, empty, &lt;cloneKey&gt;/repo}</c>. Keyed on the
/// database's file name, so two database files in one directory never share a clone, a protective ref or a lock. The
/// class builds and creates paths and never deletes anything. Nothing calls it yet (milestone 1a-1).
/// </summary>
public sealed class GitSyncPaths
{
    private readonly string? _cloneDirectory;

    private GitSyncPaths(string databaseDirectory, string syncDirectory, string? cloneDirectory)
    {
        DatabaseDirectory = databaseDirectory;
        SyncDirectory = syncDirectory;
        _cloneDirectory = cloneDirectory;
    }

    /// <summary>The full path of the directory holding the database file; <c>sync</c> is its child.</summary>
    public string DatabaseDirectory { get; }

    /// <summary><c>&lt;DatabaseDirectory&gt;/sync/&lt;dbKey&gt;</c>.</summary>
    public string SyncDirectory { get; }

    /// <summary>
    /// The disposable clone: <c>&lt;SyncDirectory&gt;/&lt;cloneKey&gt;/repo</c>. Throws for paths made by
    /// <see cref="ForDatabase"/>, which know no remote.
    /// </summary>
    public string CloneDirectory =>
        _cloneDirectory ?? throw new InvalidOperationException("these paths were made without a remote and have no clone directory");

    public string LockPath => Path.Combine(SyncDirectory, "lock");

    /// <summary>An empty directory, the value of <c>core.hooksPath</c> on every call.</summary>
    public string NoHooksDirectory => Path.Combine(SyncDirectory, "no-hooks");

    /// <summary>A zero-byte file, the value of <c>core.attributesFile</c> and <c>core.excludesFile</c>.</summary>
    public string EmptyFile => Path.Combine(SyncDirectory, "empty");

    /// <param name="dbPath">The database file; only its directory and file name are used. A relative path is resolved against the current directory at the time of the call.</param>
    /// <param name="remote">The (validated) remote; it is hashed, never used as a path.</param>
    /// <param name="caseInsensitive">True on Windows and macOS: <c>A.db</c> and <c>a.db</c> are one file there.</param>
    public static GitSyncPaths For(string dbPath, string remote, bool caseInsensitive)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("a database path is required", nameof(dbPath));
        if (string.IsNullOrEmpty(remote))
            throw new ArgumentException("a remote is required", nameof(remote));

        var (directory, syncDirectory) = Resolve(dbPath, caseInsensitive);
        var cloneDirectory = Path.Combine(syncDirectory, SyncGit.cloneKey(remote), "repo");
        return new GitSyncPaths(directory, syncDirectory, cloneDirectory);
    }

    /// <summary>
    /// The same sync directory, lock, hooks directory and empty file as <see cref="For"/> for the database, and no
    /// clone directory (<see cref="CloneDirectory"/> throws). For the commands that need the lock but no remote.
    /// </summary>
    public static GitSyncPaths ForDatabase(string dbPath, bool caseInsensitive)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("a database path is required", nameof(dbPath));
        var (directory, syncDirectory) = Resolve(dbPath, caseInsensitive);
        return new GitSyncPaths(directory, syncDirectory, null);
    }

    private static (string Directory, string SyncDirectory) Resolve(string dbPath, bool caseInsensitive)
    {
        var full = Path.GetFullPath(dbPath);
        var directory = Path.GetDirectoryName(full)
            ?? throw new ArgumentException("the database path has no directory", nameof(dbPath));
        var fileName = Path.GetFileName(full);
        if (fileName.Length == 0)
            throw new ArgumentException("the database path has no file name", nameof(dbPath));
        return (directory, Path.Combine(directory, "sync", SyncGit.dbKey(caseInsensitive, fileName)));
    }

    /// <summary>
    /// Creates the sync directory, the empty hooks directory and the empty file. Idempotent; an existing file keeps its
    /// content and nothing is removed.
    /// </summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(SyncDirectory);
        Directory.CreateDirectory(NoHooksDirectory);
        using var _ = new FileStream(EmptyFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }
}
