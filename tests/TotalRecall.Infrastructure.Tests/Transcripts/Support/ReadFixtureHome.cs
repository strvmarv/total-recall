using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>
/// One Claude Code session file for the archive read tests, written as fixture text with the placeholders of
/// <see cref="ClaudeFixtureHome.RenderText"/> (<c>{{T+n}}</c>, <c>{{CWD}}</c>, <c>{{ID}}</c>): dialogue exchanges with
/// chosen text, a compaction summary, and an automated (<c>sdk-py</c> entrypoint) session. Every size is a parameter
/// of the caller, so nothing large is committed. Uses only public API (this folder is also compiled into the Server and
/// Cli test projects).
/// </summary>
public sealed class ReadSessionScript
{
    private readonly StringBuilder _lines = new();
    private readonly string _entrypoint;
    private int _second;
    private int _line;
    private string? _previous;

    /// <param name="startSecond">The first record's time, in seconds after <see cref="ClaudeFixtureHome.BaseTime"/>.</param>
    /// <param name="automated">Write every record with the <c>sdk-py</c> entrypoint, which makes the session automated.</param>
    public ReadSessionScript(int startSecond = 0, bool automated = false)
    {
        _second = startSecond;
        _entrypoint = automated ? "sdk-py" : "cli";
    }

    /// <summary>The time of the last record written, in seconds after <see cref="ClaudeFixtureHome.BaseTime"/>.</summary>
    public int LastSecond { get; private set; }

    /// <summary>A user record and an assistant record one second later; the next record is ten seconds after this one.</summary>
    public ReadSessionScript Exchange(string user, string assistant)
    {
        Record("user", "{\"role\":\"user\",\"content\":" + Json(user) + "}", human: true);
        _second += 1;
        Record("assistant",
            "{\"model\":\"fixture-model\",\"id\":\"msg_rd" + _line.ToString(CultureInfo.InvariantCulture) +
            "\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":" + Json(assistant) +
            "}],\"stop_reason\":\"end_turn\"}",
            human: false);
        _second += 9;
        return this;
    }

    /// <summary>A compaction boundary and the summary record after it: one unit of type <c>compaction-summary</c>.</summary>
    public ReadSessionScript CompactionSummary(string summary)
    {
        Record("system", null, human: false,
            extra: "\"subtype\":\"compact_boundary\",\"content\":\"Conversation compacted\",\"level\":\"info\"," +
                   "\"compactMetadata\":{\"trigger\":\"manual\",\"preTokens\":1200,\"postTokens\":300,\"durationMs\":10},");
        _second += 1;
        Record("user", "{\"role\":\"user\",\"content\":" + Json(summary) + "}", human: false, extra: "\"isCompactSummary\":true,");
        _second += 9;
        return this;
    }

    /// <summary>The fixture text, every line ending in LF, placeholders unsubstituted.</summary>
    public string Text => _lines.ToString();

    private void Record(string type, string? messageJson, bool human, string extra = "")
    {
        _line++;
        var uuid = "00000000-0000-4000-8000-" + _line.ToString("D12", CultureInfo.InvariantCulture);
        _lines.Append("{\"parentUuid\":").Append(_previous is null ? "null" : "\"" + _previous + "\"")
              .Append(",\"isSidechain\":false,");
        if (type == "user" && human) _lines.Append("\"promptId\":\"p").Append(_line).Append("\",");
        _lines.Append("\"type\":\"").Append(type).Append("\",");
        if (messageJson is not null) _lines.Append("\"message\":").Append(messageJson).Append(',');
        _lines.Append(extra);
        if (human) _lines.Append("\"origin\":{\"kind\":\"human\"},");
        _lines.Append("\"uuid\":\"").Append(uuid).Append("\",\"timestamp\":\"{{T+")
              .Append(_second.ToString(CultureInfo.InvariantCulture))
              .Append("}}\",\"userType\":\"external\",\"entrypoint\":\"").Append(_entrypoint)
              .Append("\",\"cwd\":\"{{CWD}}\",\"sessionId\":\"{{ID}}\",\"version\":\"2.1.0\",\"gitBranch\":\"main\"}\n");
        LastSecond = _second;
        _previous = uuid;
    }

    private static string Json(string value) =>
        "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";
}

/// <summary>
/// Places <see cref="ReadSessionScript"/> sessions in a <see cref="ClaudeFixtureHome"/> and builds the long texts of the
/// read tests (paragraphs of plain words, so the chunker splits them on paragraph boundaries and nothing looks like a
/// secret to the redaction).
/// </summary>
public static class ReadFixtureHome
{
    private const string Sentence = "The archive keeps every exchange of a session in order. ";

    /// <summary>Renders <paramref name="script"/> and writes it as <c>projects/&lt;encoded cwd&gt;/&lt;id&gt;.jsonl</c>.</summary>
    public static PlacedSession Place(ClaudeFixtureHome home, string cwd, string id, ReadSessionScript script)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(script);
        var dir = Path.Combine(home.ProjectsDir, ClaudeFixtureHome.EncodeDir(cwd));
        Directory.CreateDirectory(dir);
        var session = new PlacedSession(id, Path.Combine(dir, id + ".jsonl"), cwd);
        home.Write(session, home.RenderText(script.Text, cwd, id));
        return session;
    }

    /// <summary>
    /// A session of <paramref name="exchanges"/> dialogue exchanges, the text of exchange <c>n</c> chosen by
    /// <paramref name="user"/> and <paramref name="assistant"/>.
    /// </summary>
    public static ReadSessionScript Dialogue(
        int exchanges, Func<int, string> user, Func<int, string> assistant, int startSecond = 0, bool automated = false)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(assistant);
        var script = new ReadSessionScript(startSecond, automated);
        for (var n = 0; n < exchanges; n++) script.Exchange(user(n), assistant(n));
        return script;
    }

    /// <summary>
    /// Plain words of exactly <paramref name="length"/> characters starting with <paramref name="lead"/>, with no
    /// trailing whitespace and no line break.
    /// </summary>
    public static string Words(string lead, int length)
    {
        ArgumentNullException.ThrowIfNull(lead);
        if (lead.Length > length) throw new ArgumentException("the lead is longer than the length", nameof(lead));
        var sb = new StringBuilder(lead, length + Sentence.Length);
        while (sb.Length < length) sb.Append(Sentence);
        sb.Length = length;
        for (var i = sb.Length - 1; i >= 0 && sb[i] == ' '; i--) sb[i] = '.';
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="count"/> paragraphs of <paramref name="length"/> characters each (see <see cref="Words"/>),
    /// separated by a blank line; paragraph <c>i</c> starts with <c>&lt;tag&gt; paragraph &lt;i&gt;: </c>.
    /// </summary>
    public static string Paragraphs(string tag, int count, int length)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append("\n\n");
            sb.Append(Words(tag + " paragraph " + i.ToString(CultureInfo.InvariantCulture) + ": ", length));
        }
        return sb.ToString();
    }
}
