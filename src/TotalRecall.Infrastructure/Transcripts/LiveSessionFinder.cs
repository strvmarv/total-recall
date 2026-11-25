using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// Finds the session a host process is running, from the marker its hook wrote (spec section 2): the
/// engine walks its own ancestor pids, nearest first, and reads <c>&lt;dataDir&gt;/live/&lt;host&gt;-&lt;pid&gt;.json</c>
/// for each pid and each adapter. A marker is trusted only after every check below passes; a refused or
/// unreadable one is skipped (the walk goes on) and logged once per file to stderr. The finder opens no
/// archive and reads nothing but the marker files and the two path components below the transcript root.
/// </summary>
public sealed class LiveSessionFinder
{
    private const int MaxMarkerBytes = 16 * 1024;
    private static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(2);

    private readonly string _liveDir;
    private readonly CachedAncestry _ancestry;
    private readonly Func<int, DateTimeOffset?> _processStart;
    private readonly Action<string> _log;
    private readonly object _logGate = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);

    public LiveSessionFinder(
        string dataDir,
        CachedAncestry ancestry,
        Func<int, DateTimeOffset?>? processStart = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(dataDir);
        _liveDir = Path.Combine(dataDir, "live");
        _ancestry = ancestry ?? throw new ArgumentNullException(nameof(ancestry));
        _processStart = processStart ?? DefaultProcessStart;
        _log = log ?? (line => Console.Error.WriteLine(line));   // stderr only: stdout is the MCP channel
    }

    /// <summary>The first accepted marker (nearest ancestor first), as <see cref="CurrentKind.Marker"/>; null when none.</summary>
    public CurrentSession? Find(IReadOnlyList<ITranscriptAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        if (adapters.Count == 0) return null;
        foreach (var pid in _ancestry.Get())
        {
            foreach (var adapter in adapters)
            {
                var file = Path.Combine(_liveDir, adapter.Host + "-" + pid.ToString(CultureInfo.InvariantCulture) + ".json");
                CurrentSession? accepted;
                try { accepted = Check(file, pid, adapter, out var refusal) ?? LogRefusal(file, refusal); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    accepted = LogRefusal(file, "unreadable (" + ex.GetType().Name + ")");
                }
                if (accepted is not null) return accepted;
            }
        }
        return null;
    }

    private CurrentSession? LogRefusal(string file, string? reason)
    {
        if (reason is null) return null;   // no such file: not a refusal
        lock (_logGate)
        {
            if (!_logged.Add(file)) return null;
        }
        try { _log("[total-recall] transcripts live marker: ignoring " + file + ": " + reason); }
        catch { /* a log failure never fails a read */ }
        return null;
    }

    /// <summary>The marker's session, or null with <paramref name="refusal"/> set (null when the file does not exist).</summary>
    private CurrentSession? Check(string file, int pid, ITranscriptAdapter adapter, out string? refusal)
    {
        refusal = null;

        byte[] bytes;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            var buffer = new byte[MaxMarkerBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
            if (read > MaxMarkerBytes) { refusal = "larger than 16 KiB"; return null; }
            bytes = buffer.AsSpan(0, read).ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes); }
        catch (JsonException) { refusal = "not valid JSON"; return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { refusal = "not a JSON object"; return null; }

            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != 1)
            { refusal = "unknown version"; return null; }

            if (Str(root, "host") != adapter.Host) { refusal = "host does not match"; return null; }

            if (!root.TryGetProperty("host_pid", out var hp) || hp.ValueKind != JsonValueKind.Number || !hp.TryGetInt32(out var hostPid) || hostPid != pid)
            { refusal = "host_pid does not match the file name"; return null; }

            var id = Str(root, "session_id");
            if (id is null || !ClaudeCodeAdapter.IsValidId(id)) { refusal = "session_id is not a valid session id"; return null; }

            var cwd = Str(root, "cwd");
            if (cwd is null || !IsAbsolute(cwd)) { refusal = "cwd is not an absolute path"; return null; }

            var transcriptPath = Str(root, "transcript_path");
            if (transcriptPath is null || !IsAbsolute(transcriptPath)) { refusal = "transcript_path is not an absolute path"; return null; }

            var pathRefusal = CheckTranscriptPath(transcriptPath, id, adapter.Root);
            if (pathRefusal is not null) { refusal = pathRefusal; return null; }

            var start = SafeProcessStart(pid);
            if (start is { } started)
            {
                var at = Str(root, "at");
                if (at is null || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var written))
                { refusal = "at is not a time"; return null; }
                if (written < started - StartSlack) { refusal = "written before the process it names started"; return null; }
            }

            return new CurrentSession(CurrentKind.Marker, adapter.Host, id, transcriptPath, cwd);
        }
    }

    /// <summary>
    /// The path rule: lexically clean (no <c>.</c> or <c>..</c> component), the root's components first, exactly two
    /// components below it, the second named <c>&lt;id&gt;.jsonl</c>; the first an existing directory and the second an
    /// existing regular file, neither a link. A clean path like that cannot leave the root.
    /// </summary>
    private static string? CheckTranscriptPath(string path, string id, string root)
    {
        var parts = Components(path);
        if (parts is null) return "transcript_path has a . or .. component";
        var rootParts = Components(root);
        if (rootParts is null || rootParts.Length == 0) return "the transcript root is unusable";

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (parts.Length != rootParts.Length + 2) return "transcript_path is not <root>/<project>/<session>.jsonl";
        for (var i = 0; i < rootParts.Length; i++)
            if (!string.Equals(parts[i], rootParts[i], comparison)) return "transcript_path is not under the transcript root";
        if (!string.Equals(parts[^1], id + ".jsonl", StringComparison.Ordinal)) return "the file is not named <session_id>.jsonl";

        var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
        if (!dir.Exists) return "the project directory does not exist";
        if (IsLink(dir)) return "the project directory is a link";

        var file = new FileInfo(path);
        if (!file.Exists) return "the session file does not exist";
        if (IsLink(file)) return "the session file is a link";
        return null;
    }

    /// <summary>The non-empty components of an absolute path, or null when one is <c>.</c> or <c>..</c>.</summary>
    private static string[]? Components(string path)
    {
        var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
            if (p is "." or "..") return null;
        return parts;
    }

    private static bool IsAbsolute(string path) =>
        path.Length > 0 && path.Length <= 4096 && path.IndexOf('\0') < 0 && Path.IsPathFullyQualified(path);

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private DateTimeOffset? SafeProcessStart(int pid)
    {
        try { return _processStart(pid); }
        catch { return null; }
    }

    private static DateTimeOffset? DefaultProcessStart(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch
        {
            return null;
        }
    }
}
