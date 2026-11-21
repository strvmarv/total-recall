using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// Claude Code's session files, <c>&lt;projects&gt;/&lt;encoded-cwd&gt;/&lt;id&gt;.jsonl</c> (spec section 2). Enumeration, the head
/// and the byte-exact line reading live here; the mapping of a line to records is added with the record parser.
/// </summary>
public sealed class ClaudeCodeAdapter : ITranscriptAdapter
{
    public const string HostName = "claude-code";
    public const int MaxLineBytes = 16 * 1024 * 1024;
    public const int HeadLines = 200;

    private const string Extension = ".jsonl";
    private const int MaxIdLength = 128;

    private readonly int _maxLineBytes;

    public ClaudeCodeAdapter(string? projectsRoot = null)
        : this(projectsRoot ?? ResolveRoot(
                   Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
                   Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
               MaxLineBytes)
    {
    }

    internal ClaudeCodeAdapter(string projectsRoot, int maxLineBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectsRoot);
        Root = projectsRoot;
        _maxLineBytes = maxLineBytes;
    }

    public string Host => HostName;

    public string Root { get; }

    /// <summary><c>&lt;dir&gt;/projects</c> when <paramref name="claudeConfigDir"/> is non-empty after trimming, else <c>&lt;userProfile&gt;/.claude/projects</c>.</summary>
    public static string ResolveRoot(string? claudeConfigDir, string userProfile)
    {
        var dir = claudeConfigDir?.Trim();
        return !string.IsNullOrEmpty(dir)
            ? Path.Combine(dir, "projects")
            : Path.Combine(userProfile, ".claude", "projects");
    }

    public bool Detect() => Directory.Exists(Root);

    public SessionEnumeration EnumerateSessions()
    {
        var sources = new List<SessionSource>();
        var skipped = 0;
        if (!Directory.Exists(Root)) return new SessionEnumeration(sources, 0);

        // The root itself may be a symbolic link and is followed; what is beneath it is checked.
        foreach (var projectDir in SafeList(() => new DirectoryInfo(Root).EnumerateDirectories()))
        {
            if (IsLink(projectDir)) continue;
            foreach (var file in SafeList(() => projectDir.EnumerateFiles()))
            {
                if (!file.Name.EndsWith(Extension, StringComparison.Ordinal)) continue;
                if (IsLink(file) || (file.Attributes & FileAttributes.Directory) != 0) continue;

                var id = file.Name[..^Extension.Length];
                if (!IsValidId(id))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    file.Refresh();
                    if (!file.Exists) continue;
                    var mtime = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    sources.Add(new SessionSource(id, file.FullName, file.Length, mtime));
                }
                catch (IOException) { }                 // gone between listing and stat
                catch (UnauthorizedAccessException) { }
            }
        }

        sources.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new SessionEnumeration(sources, skipped);
    }

    public SessionHead ReadHead(SessionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string? cwd = null, entrypoint = null, fingerprint = null;
        DateTimeOffset? first = null;
        try
        {
            using var fs = OpenRead(source.Path);
            var n = 0;
            foreach (var line in JsonlLineReader.Read(fs, 0, _maxLineBytes, CancellationToken.None))
            {
                n++;
                if (n == 1)
                {
                    if (line.TooLong) break;         // no fingerprint: the session is skipped and counted by the caller
                    fingerprint = Convert.ToHexString(SHA256.HashData(line.Bytes.Span)).ToLowerInvariant();
                }
                if (!line.TooLong) ReadHeadFields(line.Bytes, ref cwd, ref entrypoint, ref first);
                if ((cwd is not null && entrypoint is not null && first is not null) || n >= HeadLines) break;
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }

        return new SessionHead(cwd, entrypoint, first, fingerprint);
    }

    public bool IsLineStart(SessionSource source, long offset)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (offset == 0) return true;
        if (offset < 0) return false;
        try
        {
            using var fs = OpenRead(source.Path);
            if (offset > fs.Length) return false;
            fs.Seek(offset - 1, SeekOrigin.Begin);
            return fs.ReadByte() == 0x0A;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public IEnumerable<TranscriptRecord> ReadRecords(SessionSource source, long startOffset, ReadStats stats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stats);
        return ReadRecordsCore(source, startOffset, stats, ct);
    }

    private IEnumerable<TranscriptRecord> ReadRecordsCore(SessionSource source, long startOffset, ReadStats stats, CancellationToken ct)
    {
        using var fs = OpenRead(source.Path);
        foreach (var line in JsonlLineReader.Read(fs, startOffset, _maxLineBytes, ct))
        {
            if (line.TooLong)
            {
                stats.SkippedLines++;
                continue;
            }
            // The mapping of a line to records is added with the record parser.
        }
        yield break;
    }

    private static void ReadHeadFields(ReadOnlyMemory<byte> bytes, ref string? cwd, ref string? entrypoint, ref DateTimeOffset? first)
    {
        if (bytes.IsEmpty) return;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            cwd ??= StringProperty(root, "cwd");
            entrypoint ??= StringProperty(root, "entrypoint");
            if (first is null && TranscriptTime.TryParse(StringProperty(root, "timestamp"), out var ts))
                first = ts;
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }       // a string with a lone surrogate escape
        catch (ArgumentException) { }
    }

    private static string? StringProperty(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>A character loop, not a regex: <c>$</c> would accept a trailing newline.</summary>
    private static bool IsValidId(string id)
    {
        if (id.Length is < 1 or > MaxIdLength) return false;
        foreach (var c in id)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!ok) return false;
        }
        return true;
    }

    private static IEnumerable<T> SafeList<T>(Func<IEnumerable<T>> list)
    {
        try
        {
            return new List<T>(list());
        }
        catch (IOException) { return Array.Empty<T>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<T>(); }
    }
}
