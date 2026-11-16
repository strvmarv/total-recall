using System.Diagnostics;
using System.Runtime.Versioning;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

public enum MachineIdKind { Found, Absent, Failed }

/// <summary>What looking for the operating system's machine id found: an id, nothing usable, or a failure.</summary>
public readonly record struct MachineIdResult(MachineIdKind Kind, string? Id);

/// <summary>
/// The operating system's machine id (spec decision 5): Linux <c>/etc/machine-id</c> then
/// <c>/var/lib/dbus/machine-id</c>; macOS <c>IOPlatformUUID</c> from <c>/usr/sbin/ioreg</c>; Windows
/// <c>MachineGuid</c> (64-bit view). A source that does not exist or holds nothing usable is <c>Absent</c>; one that
/// exists but fails (an unreadable file, a timeout, a registry error) is <c>Failed</c>.
/// </summary>
public static class MachineIdReader
{
    private static readonly string[] LinuxFiles = ["/etc/machine-id", "/var/lib/dbus/machine-id"];

    public static MachineIdResult Read()
    {
        try
        {
            if (OperatingSystem.IsLinux()) return Read(ReadFile);
            if (OperatingSystem.IsMacOS()) return ReadMacOs();
            if (OperatingSystem.IsWindows()) return ReadWindows();
            return new MachineIdResult(MachineIdKind.Absent, null);
        }
        catch (Exception)
        {
            return new MachineIdResult(MachineIdKind.Failed, null);
        }
    }

    /// <summary>The Linux combination: the first <c>Found</c> wins, an <c>Absent</c> file falls through, a <c>Failed</c> file fails the whole read.</summary>
    internal static MachineIdResult Read(Func<string, (bool Exists, string? Text, bool Failed)> readFile)
    {
        foreach (var path in LinuxFiles)
        {
            var (exists, text, failed) = readFile(path);
            var result = FromFileText(text, exists, failed);
            if (result.Kind != MachineIdKind.Absent) return result;
        }
        return new MachineIdResult(MachineIdKind.Absent, null);
    }

    internal static MachineIdResult FromFileText(string? text, bool fileExists, bool readFailed)
    {
        if (readFailed) return new MachineIdResult(MachineIdKind.Failed, null);
        if (!fileExists) return new MachineIdResult(MachineIdKind.Absent, null);
        var id = Clean(text);
        return id is null ? new MachineIdResult(MachineIdKind.Absent, null) : new MachineIdResult(MachineIdKind.Found, id);
    }

    /// <summary>The trimmed value when it is 1 to 128 characters with no control character; otherwise null.</summary>
    internal static string? Clean(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(char.IsControl)) return null;
        return value;
    }

    /// <summary>The value of <c>"IOPlatformUUID" = "..."</c> in the output of <c>ioreg -rd1 -c IOPlatformExpertDevice</c>.</summary>
    internal static string? ParseIoreg(string output)
    {
        const string key = "\"IOPlatformUUID\"";
        var at = output.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        var open = output.IndexOf('"', at + key.Length);
        if (open < 0) return null;
        var close = output.IndexOf('"', open + 1);
        return close < 0 ? null : Clean(output[(open + 1)..close]);
    }

    private static (bool Exists, string? Text, bool Failed) ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? (true, File.ReadAllText(path), false) : (false, null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (true, null, true);
        }
    }

    private static MachineIdResult ReadMacOs()
    {
        const string ioreg = "/usr/sbin/ioreg";
        if (!File.Exists(ioreg)) return new MachineIdResult(MachineIdKind.Absent, null);
        var info = new ProcessStartInfo(ioreg) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-rd1");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("IOPlatformExpertDevice");
        using var process = Process.Start(info);
        if (process is null) return new MachineIdResult(MachineIdKind.Failed, null);
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(3000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new MachineIdResult(MachineIdKind.Failed, null);
        }
        if (process.ExitCode != 0 || !output.Wait(1000)) return new MachineIdResult(MachineIdKind.Failed, null);
        var id = ParseIoreg(output.Result);
        return id is null ? new MachineIdResult(MachineIdKind.Absent, null) : new MachineIdResult(MachineIdKind.Found, id);
    }

    [SupportedOSPlatform("windows")]
    private static MachineIdResult ReadWindows()
    {
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var id = Clean(key?.GetValue("MachineGuid") as string);
        return id is null ? new MachineIdResult(MachineIdKind.Absent, null) : new MachineIdResult(MachineIdKind.Found, id);
    }
}

/// <summary>
/// The fingerprint of this machine and data directory (spec decision 5): a hash of the operating system's machine id
/// and the canonical path of the database file, never the hostname. Computed once. <see cref="Fingerprint"/> is null
/// when it cannot be determined (the machine id source failed, or the path could not be resolved).
/// </summary>
public sealed class HostIdentity
{
    private readonly Lazy<(string? Canonical, string? Fingerprint)> _computed;

    public HostIdentity(Func<MachineIdResult> readMachineId, string databasePath, bool caseInsensitive)
    {
        ArgumentNullException.ThrowIfNull(readMachineId);
        ArgumentNullException.ThrowIfNull(databasePath);
        _computed = new Lazy<(string?, string?)>(() =>
        {
            var canonical = CanonicalPathOf(databasePath, caseInsensitive);
            if (canonical is null) return (null, null);
            var machine = readMachineId();
            return machine.Kind switch
            {
                MachineIdKind.Failed => (canonical, null),
                MachineIdKind.Found => (canonical, SyncIdentity.hostFingerprint(machine.Id ?? "", canonical)),
                _ => (canonical, SyncIdentity.hostFingerprint("", canonical)),
            };
        });
    }

    public static HostIdentity ForCurrentMachine(string databasePath) =>
        new(MachineIdReader.Read, databasePath, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    /// <summary>Null when the machine id could not be read or the path could not be resolved.</summary>
    public string? Fingerprint => _computed.Value.Fingerprint;

    /// <summary>Null when resolving the path failed (a link loop, an unreadable component).</summary>
    public string? CanonicalPath => _computed.Value.Canonical;

    /// <summary>
    /// The full path with every existing component resolved through its symbolic links (one component at a time), a
    /// <c>\\?\</c> prefix stripped (a <c>link/..</c> in the given path is collapsed lexically by <c>GetFullPath</c> first), trailing separators removed and, on a case-insensitive system, lower-cased with
    /// the invariant culture. Null on any failure.
    /// </summary>
    internal static string? CanonicalPathOf(string path, bool caseInsensitive)
    {
        try
        {
            var resolved = ResolveLinks(Path.GetFullPath(path), 0);
            if (resolved is null) return null;
            resolved = StripExtendedPrefix(resolved);
            var root = Path.GetPathRoot(resolved) ?? "";
            while (resolved.Length > root.Length && (resolved[^1] == Path.DirectorySeparatorChar || resolved[^1] == Path.AltDirectorySeparatorChar))
                resolved = resolved[..^1];
            return caseInsensitive ? resolved.ToLowerInvariant() : resolved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static string? ResolveLinks(string fullPath, int depth)
    {
        if (depth > 32) return null;
        var root = Path.GetPathRoot(fullPath) ?? "";
        var parts = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null)
            {
                // One hop at a time: every ancestor of `next` is already physical, so a relative target (`../z`) is
                // resolved against the right directory; the recursion walks the target's own components again.
                var target = info.ResolveLinkTarget(returnFinalTarget: false);
                if (target is null) return null;
                var rest = parts[(i + 1)..];
                return ResolveLinks(rest.Length == 0 ? target.FullName : Path.Combine([target.FullName, .. rest]), depth + 1);
            }
            current = next;
        }
        return current;
    }
}
