using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// Claude Code's session files, <c>&lt;projects&gt;/&lt;encoded-cwd&gt;/&lt;id&gt;.jsonl</c> (spec section 2). Enumeration, the head
/// and the byte-exact line reading live here; <see cref="ClaudeRecordMapper"/> turns each line into records.
/// </summary>
public sealed class ClaudeCodeAdapter : ITranscriptAdapter
{
    public const string HostName = "claude-code";
    public const int MaxLineBytes = 16 * 1024 * 1024;
    public const int HeadLines = 200;

    private const string Extension = ".jsonl";
    private const int MaxIdLength = 128;

    private const int MaxJsonDepth = 64;

    private static readonly JsonDocumentOptions LineOptions = new() { MaxDepth = MaxJsonDepth };

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
        ArgumentNullException.ThrowIfNull(projectsRoot);   // may be empty: no home directory, nothing is detected
        Root = projectsRoot;
        _maxLineBytes = maxLineBytes;
    }

    public string Host => HostName;

    public string Root { get; }

    /// <summary>
    /// <c>&lt;dir&gt;/projects</c> when <paramref name="claudeConfigDir"/> is non-empty after trimming, else
    /// <c>&lt;userProfile&gt;/.claude/projects</c>; an empty string when neither is known (a relative path would be
    /// resolved against the working directory), so that nothing is detected.
    /// </summary>
    public static string ResolveRoot(string? claudeConfigDir, string userProfile)
    {
        var dir = claudeConfigDir?.Trim();
        if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "projects");
        return string.IsNullOrWhiteSpace(userProfile) ? "" : Path.Combine(userProfile, ".claude", "projects");
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
        if (IsEmpty(source)) return new SessionHead(null, null, null, null);
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
        if (offset < 0 || IsEmpty(source)) return false;
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
        if (IsEmpty(source)) yield break;
        using var fs = OpenRead(source.Path);
        foreach (var line in JsonlLineReader.Read(fs, startOffset, _maxLineBytes, ct))
        {
            if (line.TooLong)
            {
                stats.SkippedLines++;
                continue;
            }
            if (IsBlank(line.Bytes.Span)) continue;       // not a record and not a bad line: not counted

            var records = ParseLine(line);
            if (records is null)
            {
                stats.SkippedLines++;
                continue;
            }
            foreach (var record in records) yield return record;
        }
    }

    /// <summary>The records of one line, or null when the line cannot be read (it is then counted by the caller).</summary>
    private static IReadOnlyList<TranscriptRecord>? ParseLine(JsonlLine line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line.Bytes, LineOptions);
            return ClaudeRecordMapper.Map(doc.RootElement, line.Start, line.End);
        }
        catch (JsonException) { return null; }               // not JSON, invalid UTF-8, nested deeper than the limit
        catch (InvalidOperationException) { return null; }   // not a record, or a string that does not decode
        catch (ArgumentException) { return null; }
    }

    /// <summary>Empty, or only the ASCII white space a line can carry (a CR from CRLF, indentation).</summary>
    private static bool IsBlank(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not (0x20 or 0x09 or 0x0D or 0x0B or 0x0C)) return false;
        }
        return true;
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

    /// <summary>
    /// A source of size 0 has no complete line, so it is answered without opening it. This is what keeps a FIFO or a
    /// device node named <c>&lt;id&gt;.jsonl</c> from hanging the backup: .NET cannot tell the file type, but such a
    /// file always stats with size 0, and opening one without a writer blocks in open(2). An empty regular file gives
    /// the same answers as before.
    /// </summary>
    private static bool IsEmpty(SessionSource source) => source.Size <= 0;

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
