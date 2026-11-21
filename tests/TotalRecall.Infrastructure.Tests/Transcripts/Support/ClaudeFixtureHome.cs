using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>One session file placed in a <see cref="ClaudeFixtureHome"/>.</summary>
public sealed record PlacedSession(string Id, string Path, string Cwd);

/// <summary>
/// A temporary Claude Code <c>projects/</c> tree built from the synthetic fixtures under
/// <c>tests/fixtures/transcripts/claude-code</c>. Fixtures hold placeholders (<c>{{SECRET:name}}</c>, <c>{{CWD}}</c>,
/// <c>{{ID}}</c>, <c>{{T+n}}</c>, <c>{{PADTO:n}}</c>) that are substituted after JSON-escaping the value, so a fake
/// secret is never a literal in any committed file. Uses only public API so that it also compiles in the CLI tests.
/// </summary>
public sealed class ClaudeFixtureHome : IDisposable
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public string Root { get; }
    public string ProjectsDir { get; }
    public FakeSecrets Secrets { get; }

    /// <summary>Fixed and well in the past: 2026-01-01T00:00:00Z.</summary>
    public DateTimeOffset BaseTime { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public ClaudeFixtureHome(int seed = 1)
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tr-claude-home-" + Guid.NewGuid().ToString("N"));
        ProjectsDir = System.IO.Path.Combine(Root, "projects");
        Directory.CreateDirectory(ProjectsDir);
        Secrets = new FakeSecrets(seed);
    }

    /// <summary>The committed fixtures, found by walking up from the test output directory.</summary>
    public static string FixturesDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "tests", "fixtures", "transcripts", "claude-code");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                "tests/fixtures/transcripts/claude-code was not found walking up from " + AppContext.BaseDirectory);
        }
    }

    /// <summary>Claude Code's directory name for a working directory: every non-alphanumeric character becomes <c>-</c>.</summary>
    public static string EncodeDir(string cwd)
    {
        var sb = new StringBuilder(cwd.Length);
        foreach (var c in cwd)
            sb.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? c : '-');
        return sb.ToString();
    }

    /// <summary>Renders a fixture and writes it as <c>projects/&lt;encoded cwd&gt;/&lt;id&gt;.jsonl</c>.</summary>
    public PlacedSession Place(string fixture, string cwd, string? id = null)
    {
        id ??= Guid.NewGuid().ToString();
        var dir = System.IO.Path.Combine(ProjectsDir, EncodeDir(cwd));
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, id + ".jsonl");
        var session = new PlacedSession(id, path, cwd);
        Write(session, Render(fixture, cwd, id));
        return session;
    }

    /// <summary>The bytes of a fixture with every placeholder substituted (UTF-8, no BOM, every line ends in LF).</summary>
    public byte[] Render(string fixture, string cwd, string id) =>
        RenderText(File.ReadAllText(System.IO.Path.Combine(FixturesDir, fixture + ".jsonl"), Utf8), cwd, id);

    /// <summary>
    /// The bytes of fixture text with every placeholder substituted. A trailing CR on a line is dropped, so a checkout
    /// with CRLF line ends renders the same bytes as one with LF.
    /// </summary>
    public byte[] RenderText(string raw, string cwd, string id)
    {
        var sb = new StringBuilder(raw.Length + 256);
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0) continue;
            sb.Append(RenderLine(line, cwd, id)).Append('\n');
        }
        return Utf8.GetBytes(sb.ToString());
    }

    public void Write(PlacedSession s, byte[] content) => File.WriteAllBytes(s.Path, content);

    /// <summary>Appends one complete line (a trailing LF is added).</summary>
    public void AppendLine(PlacedSession s, string jsonLine) =>
        AppendBytes(s, Utf8.GetBytes(jsonLine + "\n"));

    /// <summary>Appends text with no trailing newline: a record still being written.</summary>
    public void AppendPartial(PlacedSession s, string text) => AppendBytes(s, Utf8.GetBytes(text));

    /// <summary>Appends a user record and an assistant record, one second apart, starting <paramref name="secondsAfterBase"/> after <see cref="BaseTime"/>.</summary>
    public void AppendExchange(PlacedSession s, string userText, string assistantText, int secondsAfterBase)
    {
        AppendLine(s, ExchangeRecord("user", userText, secondsAfterBase, s));
        AppendLine(s, ExchangeRecord("assistant", assistantText, secondsAfterBase + 1, s));
    }

    /// <summary>Rewrites the session's file from another fixture (same id and working directory).</summary>
    public void Replace(PlacedSession s, string fixture) => Write(s, Render(fixture, s.Cwd, s.Id));

    public void Delete(PlacedSession s) => File.Delete(s.Path);

    public void SetMtime(PlacedSession s, DateTimeOffset t) => File.SetLastWriteTimeUtc(s.Path, t.UtcDateTime);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void AppendBytes(PlacedSession s, byte[] bytes)
    {
        using var fs = new FileStream(s.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(bytes, 0, bytes.Length);
    }

    private string ExchangeRecord(string role, string text, int seconds, PlacedSession s)
    {
        var content = role == "user"
            ? Json(text)
            : "[{\"type\":\"text\",\"text\":" + Json(text) + "}]";
        return "{\"parentUuid\":null,\"isSidechain\":false,\"type\":\"" + role + "\"," +
               "\"message\":{\"role\":\"" + role + "\",\"content\":" + content + "}," +
               (role == "user" ? "\"origin\":{\"kind\":\"human\"}," : "") +
               "\"uuid\":\"" + Guid.NewGuid() + "\",\"timestamp\":\"" + Stamp(seconds) + "\"," +
               "\"userType\":\"external\",\"entrypoint\":\"cli\",\"cwd\":" + Json(s.Cwd) +
               ",\"sessionId\":" + Json(s.Id) + ",\"version\":\"2.1.0\",\"gitBranch\":\"main\"}";
    }

    private string Stamp(int seconds) =>
        BaseTime.AddSeconds(seconds).UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>A JSON string literal, quotes included.</summary>
    private static string Json(string value) =>
        "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString() + "\"";

    private static string Escaped(string value) =>
        JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();

    private string RenderLine(string line, string cwd, string id)
    {
        var sb = new StringBuilder(line.Length + 64);
        var pos = 0;
        while (pos < line.Length)
        {
            var open = line.IndexOf("{{", pos, StringComparison.Ordinal);
            if (open < 0) { sb.Append(line, pos, line.Length - pos); break; }
            var close = line.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) throw new FormatException("unterminated placeholder in a fixture line");
            sb.Append(line, pos, open - pos);
            var name = line.Substring(open + 2, close - open - 2);
            sb.Append(Substitute(name, sb, cwd, id));
            pos = close + 2;
        }
        return sb.ToString();
    }

    private string Substitute(string name, StringBuilder before, string cwd, string id)
    {
        if (name == "CWD") return Escaped(cwd);
        if (name == "ID") return Escaped(id);
        if (name.StartsWith("SECRET:", StringComparison.Ordinal)) return Escaped(Secrets.Get(name["SECRET:".Length..]));
        if (name.StartsWith("T+", StringComparison.Ordinal))
            return BaseTime.AddSeconds(int.Parse(name.AsSpan(2), CultureInfo.InvariantCulture))
                .UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
        if (name.StartsWith("PADTO:", StringComparison.Ordinal))
        {
            var target = int.Parse(name.AsSpan("PADTO:".Length), CultureInfo.InvariantCulture);
            var pad = target - DecodedLengthInOpenString(before);
            if (pad < 1) throw new FormatException($"{{{{{name}}}}}: the text before it is already {target - pad} characters");
            return new string('x', pad - 1) + " ";
        }
        throw new FormatException("unknown fixture placeholder: {{" + name + "}}");
    }

    /// <summary>The number of characters of the JSON string literal that is still open at the end of <paramref name="text"/>, as decoded.</summary>
    private static int DecodedLengthInOpenString(StringBuilder text)
    {
        var inString = false;
        var decoded = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!inString)
            {
                if (c == '"') { inString = true; decoded = 0; }
                continue;
            }
            if (c == '"') { inString = false; continue; }
            if (c == '\\')
            {
                i += i + 1 < text.Length && text[i + 1] == 'u' ? 5 : 1;
                decoded++;
                continue;
            }
            decoded++;
        }
        if (!inString) throw new FormatException("{{PADTO:n}} must sit inside a JSON string");
        return decoded;
    }
}
