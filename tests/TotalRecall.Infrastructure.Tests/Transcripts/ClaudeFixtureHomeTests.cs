using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>The fixture corpus and its harness (spec section 10): rendering, placeholders, the leak scan.</summary>
public sealed class ClaudeFixtureHomeTests
{
    private const string Cwd = "/work/example-repo";
    private const string Id = "00000000-aaaa-4bbb-8ccc-000000000001";

    public static IEnumerable<object[]> FixtureNames() =>
        Directory.GetFiles(ClaudeFixtureHome.FixturesDir, "*.jsonl")
            .Select(p => new object[] { Path.GetFileNameWithoutExtension(p) })
            .OrderBy(a => (string)a[0], StringComparer.Ordinal);

    private static IEnumerable<JsonElement> Lines(byte[] rendered)
    {
        foreach (var line in Encoding.UTF8.GetString(rendered).Split('\n'))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }

    [Fact]
    public void Corpus_HoldsTheTenFixtures()
    {
        var names = Directory.GetFiles(ClaudeFixtureHome.FixturesDir, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "automated-opening", "automated-sdk", "compaction", "decision", "error", "no-kept", "secrets", "straddle", "worktree-missing", "worktree-repo" },
            names);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void EveryFixture_RendersToValidJsonl(string fixture)
    {
        using var home = new ClaudeFixtureHome();
        var rendered = home.Render(fixture, Cwd, Id);
        Assert.EndsWith("\n", Encoding.UTF8.GetString(rendered));
        var count = 0;
        foreach (var obj in Lines(rendered))
        {
            Assert.Equal(JsonValueKind.Object, obj.ValueKind);
            count++;
        }
        Assert.True(count >= 2);
        Assert.DoesNotContain("{{", Encoding.UTF8.GetString(rendered));
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void CommittedFixtures_HoldNoSecret(string fixture)
    {
        var text = File.ReadAllText(Path.Combine(ClaudeFixtureHome.FixturesDir, fixture + ".jsonl"));
        // "{{SECRET:name}}" is itself a "SECRET:" assignment to the detector, so it is swapped for a neutral
        // placeholder first; everything else in the file is scanned as it is committed.
        var scanned = Regex.Replace(text, @"\{\{SECRET:[a-z0-9-]+\}\}", "{{S}}");
        Assert.Equal(0, SensitiveContentScanner.Redact(scanned).Count);
        Assert.DoesNotContain("eyJ", text);
        Assert.DoesNotContain("/home/", text);
        Assert.DoesNotContain("/Users/", text);
        Assert.DoesNotContain("C:\\\\Users", text);

        foreach (var line in text.Split('\n').Where(l => l.Length > 0))
        {
            using var doc = JsonDocument.Parse(line);
            AssertPlaceholders(doc.RootElement);
        }
    }

    private static void AssertPlaceholders(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "sessionId") Assert.Equal("{{ID}}", p.Value.GetString());
                    else if (p.Name == "cwd") Assert.Equal("{{CWD}}", p.Value.GetString());
                    else AssertPlaceholders(p.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var v in e.EnumerateArray()) AssertPlaceholders(v);
                break;
        }
    }

    [Theory]
    [InlineData("github-token", "{0}", "token")]
    [InlineData("github-pat", "{0}", "token")]
    [InlineData("gitlab-token", "{0}", "token")]
    [InlineData("slack-token", "{0}", "token")]
    [InlineData("openai-key", "{0}", "token")]
    [InlineData("anthropic-key", "{0}", "token")]
    [InlineData("openai-project-key", "{0}", "token")]
    [InlineData("openai-svcacct-key", "{0}", "token")]
    [InlineData("openai-admin-key", "{0}", "token")]
    [InlineData("aws-key", "{0}", "token")]
    [InlineData("private-key", "{0}", "private-key")]
    [InlineData("private-key-unterminated", "{0}", "private-key")]
    [InlineData("bearer-token", "Authorization: Bearer {0}", "bearer")]
    [InlineData("url-password", "https://deploy:{0}@example.invalid/repo.git", "url-credentials")]
    [InlineData("api-key-value", "API_KEY={0}", "secret-assignment")]
    public void FakeSecrets_EachMatchesItsCategory(string name, string context, string category)
    {
        var fake = new FakeSecrets(1).Get(name);
        Assert.True(fake.Length >= 12);
        var result = SensitiveContentScanner.Redact(string.Format(context, fake));
        Assert.Contains("[redacted:" + category + "]", result.Text);
        foreach (var window in LeakScan.Windows(fake))
            Assert.DoesNotContain(window, result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void RenderText_OfACrlfCheckout_EqualsTheLfRendering(string fixture)
    {
        using var home = new ClaudeFixtureHome();
        var lf = File.ReadAllText(Path.Combine(ClaudeFixtureHome.FixturesDir, fixture + ".jsonl")).Replace("\r\n", "\n");

        var fromCrlf = home.RenderText(lf.Replace("\n", "\r\n"), Cwd, Id);

        Assert.Equal(home.RenderText(lf, Cwd, Id), fromCrlf);
        Assert.Equal(home.Render(fixture, Cwd, Id), fromCrlf);
        Assert.DoesNotContain((byte)'\r', fromCrlf);
    }

    [Fact]
    public void PrivateKey_SurvivesJsonEscaping()
    {
        using var home = new ClaudeFixtureHome();
        var key = home.Secrets.Get("private-key");
        Assert.Contains('\n', key);
        var texts = Lines(home.Render("secrets", Cwd, Id))
            .Where(r => r.GetProperty("type").GetString() == "assistant")
            .Select(r => r.GetProperty("message").GetProperty("content")[0])
            .Where(b => b.GetProperty("type").GetString() == "text")
            .Select(b => b.GetProperty("text").GetString()!)
            .Where(t => t.StartsWith("The key file held", StringComparison.Ordinal))
            .ToList();
        var text = Assert.Single(texts);
        Assert.Equal("The key file held " + key + "\nand the project key " + home.Secrets.Get("openai-project-key") + ".", text);
    }

    [Fact]
    public void PadTo_PlacesTheSecretAtTheOffset()
    {
        using var home = new ClaudeFixtureHome();
        var token = home.Secrets.Get("github-token");
        var openai = home.Secrets.Get("openai-key");
        var records = Lines(home.Render("secrets", Cwd, Id)).ToList();

        var commands = records.Where(r => r.GetProperty("type").GetString() == "assistant")
            .SelectMany(r => r.GetProperty("message").GetProperty("content").EnumerateArray())
            .Where(b => b.GetProperty("type").GetString() == "tool_use")
            .Select(b => b.GetProperty("input").GetProperty("command").GetString()!)
            .Where(c => c.StartsWith("xxxx", StringComparison.Ordinal))
            .ToList();
        var command = Assert.Single(commands);
        Assert.Equal(150, command.IndexOf(token, StringComparison.Ordinal));
        Assert.Equal(' ', command[149]);

        var errors = records.Where(r => r.GetProperty("type").GetString() == "user")
            .Select(r => r.GetProperty("message").GetProperty("content"))
            .Where(c => c.ValueKind == JsonValueKind.Array)
            .SelectMany(c => c.EnumerateArray())
            .Where(b => b.GetProperty("type").GetString() == "tool_result" && b.TryGetProperty("is_error", out _))
            .Select(b => b.GetProperty("content").GetString()!)
            .Where(c => c.Contains(openai, StringComparison.Ordinal))
            .ToList();
        var error = Assert.Single(errors);
        Assert.Equal(490, error.IndexOf(openai, StringComparison.Ordinal));
    }

    [Fact]
    public void ErrorFixture_HoldsTheSearchStringAndTailBeyondTheCut()
    {
        using var home = new ClaudeFixtureHome();
        var error = Lines(home.Render("error", Cwd, Id))
            .Where(r => r.TryGetProperty("message", out _))
            .Select(r => r.GetProperty("message").GetProperty("content"))
            .Where(c => c.ValueKind == JsonValueKind.Array)
            .SelectMany(c => c.EnumerateArray())
            .Where(b => b.GetProperty("type").GetString() == "tool_result")
            .Select(b => b.GetProperty("content").GetString()!)
            .Single();
        Assert.StartsWith("Parse error: no such table: zqx_schema_version", error);
        Assert.Equal(46 + 700 + "zqxerrortail".Length, error.Length);
    }

    [Fact]
    public void SdkFixture_HoldsA2000CharacterPrompt()
    {
        using var home = new ClaudeFixtureHome();
        var prompt = Lines(home.Render("automated-sdk", Cwd, Id))
            .Where(r => r.GetProperty("type").GetString() == "user")
            .Select(r => r.GetProperty("message").GetProperty("content").GetString()!)
            .First();
        Assert.Equal(2000, prompt.Length);
    }

    [Fact]
    public void Timestamps_AreRenderedFromTheBaseTime()
    {
        using var home = new ClaudeFixtureHome();
        var stamps = Lines(home.Render("straddle", Cwd, Id))
            .Where(r => r.TryGetProperty("timestamp", out _) && r.GetProperty("type").GetString() == "user")
            .Select(r => r.GetProperty("timestamp").GetString())
            .ToArray();
        Assert.Equal(new[] { "2026-01-01T00:00:10.000Z", "2026-01-01T00:00:50.000Z", "2026-01-01T00:01:30.000Z" }, stamps);
    }

    [Fact]
    public void EncodeDir_ReplacesNonAlphanumerics()
    {
        Assert.Equal("-home-u-my-repo", ClaudeFixtureHome.EncodeDir("/home/u/my.repo"));
        Assert.Equal("C--Users-a-b", ClaudeFixtureHome.EncodeDir("C:\\Users\\a b"));
    }

    [Fact]
    public void Place_WritesUnderTheEncodedDirectory()
    {
        using var home = new ClaudeFixtureHome();
        var s = home.Place("decision", Cwd, Id);
        Assert.Equal(Path.Combine(home.ProjectsDir, "-work-example-repo", Id + ".jsonl"), s.Path);
        Assert.True(File.Exists(s.Path));
        Assert.Equal(Cwd, Lines(File.ReadAllBytes(s.Path)).Skip(1).First().GetProperty("cwd").GetString());
        Assert.NotEqual(s.Id, home.Place("decision", Cwd).Id);
    }

    [Fact]
    public void AppendPartial_LeavesNoTrailingNewline()
    {
        using var home = new ClaudeFixtureHome();
        var s = home.Place("straddle", Cwd, Id);
        var before = File.ReadAllBytes(s.Path);
        Assert.Equal((byte)'\n', before[^1]);

        home.AppendPartial(s, "{\"type\":\"user\",\"mess");
        var after = File.ReadAllBytes(s.Path);
        Assert.Equal(before.Length + "{\"type\":\"user\",\"mess".Length, after.Length);
        Assert.NotEqual((byte)'\n', after[^1]);

        home.AppendLine(s, "{\"type\":\"x\"}");
        Assert.Equal((byte)'\n', File.ReadAllBytes(s.Path)[^1]);
    }

    [Fact]
    public void AppendExchange_AddsAUserAndAnAssistantRecord()
    {
        using var home = new ClaudeFixtureHome();
        var s = home.Place("automated-opening", Cwd, Id);
        home.AppendExchange(s, "A second question", "A second answer", 100);
        var records = Lines(File.ReadAllBytes(s.Path)).ToList();
        var user = records[^2];
        var assistant = records[^1];
        Assert.Equal("user", user.GetProperty("type").GetString());
        Assert.Equal("A second question", user.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("2026-01-01T00:01:40.000Z", user.GetProperty("timestamp").GetString());
        Assert.Equal("assistant", assistant.GetProperty("type").GetString());
        Assert.Equal("A second answer", assistant.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(Id, user.GetProperty("sessionId").GetString());
        Assert.Equal(Cwd, assistant.GetProperty("cwd").GetString());
    }

    [Fact]
    public void ReplaceDeleteAndSetMtime_ActOnThePlacedFile()
    {
        using var home = new ClaudeFixtureHome();
        var s = home.Place("decision", Cwd, Id);
        var size = new FileInfo(s.Path).Length;
        home.Replace(s, "straddle");
        Assert.NotEqual(size, new FileInfo(s.Path).Length);

        home.SetMtime(s, home.BaseTime.AddDays(1));
        Assert.Equal(home.BaseTime.AddDays(1).UtcDateTime, File.GetLastWriteTimeUtc(s.Path));

        home.Delete(s);
        Assert.False(File.Exists(s.Path));
    }

    [Fact]
    public void Dispose_RemovesTheHome()
    {
        var home = new ClaudeFixtureHome();
        var root = home.Root;
        home.Place("decision", Cwd, Id);
        home.Dispose();
        Assert.False(Directory.Exists(root));
    }

    // ---- LeakScan ----

    [Fact]
    public void LeakScan_Windows_AreAllEightCharacterSubstrings()
    {
        var windows = LeakScan.Windows("abcdefghij");
        Assert.Equal(new[] { "abcdefgh", "bcdefghi", "cdefghij" }, windows);
        Assert.Equal(new[] { "short" }, LeakScan.Windows("short"));
        Assert.Empty(LeakScan.Windows(""));
    }

    [Fact]
    public void LeakScan_FindsAPlantedWindow()
    {
        using var db = new TranscriptTestDb();
        var secret = new FakeSecrets(3).Get("github-token");
        var session = db.AddSession("claude-code", "s1");
        db.AddChunk(session, "the token was " + secret.Substring(10, 20).ToUpperInvariant() + " and then it was used");

        // The scan can fail: a stored window of the planted value (here upper-cased, so case is ignored) is found.
        var ex = Assert.Throws<InvalidOperationException>(() => LeakScan.AssertNotStored(db.Path, secret));
        Assert.Contains("planted secret", ex.Message);
    }

    [Fact]
    public void LeakScan_FindsAWindowThatIsOnlyInTheWal()
    {
        using var db = new TranscriptTestDb();
        var secret = new FakeSecrets(3).Get("github-token");
        var session = db.AddSession("claude-code", "s1");
        // The store keeps its connection open, so this commit stays in the WAL (no checkpoint into the main file).
        db.AddChunk(session, "the token was " + secret + " and then it was used");
        Assert.DoesNotContain(secret, Latin1(db.Path));
        Assert.Contains(secret, Latin1(db.Path + "-wal"));

        var ex = Assert.Throws<InvalidOperationException>(() => LeakScan.AssertNotStored(db.Path, secret));
        Assert.EndsWith("in the bytes of archive.db-wal", ex.Message);
    }

    [Fact]
    public void LeakScan_FindsANonAsciiWindowInTheTableText()
    {
        using var db = new TranscriptTestDb();
        // Not ASCII, so its UTF-8 bytes read as Latin-1 never match: only the table text scan can find it.
        var planted = "\u0441\u0435\u043a\u0440\u0435\u0442\u043d\u044b\u0439\u043a\u043b\u044e\u0447";
        var session = db.AddSession("claude-code", "s1");
        db.AddChunk(session, "the value was " + planted + " then");

        var ex = Assert.Throws<InvalidOperationException>(() => LeakScan.AssertNotStored(db.Path, planted));
        Assert.EndsWith("in tx_chunk.content", ex.Message);
    }

    [Fact]
    public void LeakScan_FindsAWindowThatIsOnlyInTheTextIndex()
    {
        using var db = new TranscriptTestDb();
        var planted = "\u0441\u0435\u043a\u0440\u0435\u0442\u043d\u044b\u0439\u043a\u043b\u044e\u0447";
        var session = db.AddSession("claude-code", "s1");
        db.AddChunk(session, "nothing to see here");
        // A stale index entry: written to the external-content index only, so tx_chunk never holds it.
        db.Exec("INSERT INTO tx_chunk_fts(rowid, content) VALUES (999, 'stale " + planted + "')");

        var ex = Assert.Throws<InvalidOperationException>(() => LeakScan.AssertNotStored(db.Path, planted));
        Assert.EndsWith("in the tx_chunk_fts index", ex.Message);
    }

    private static string Latin1(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    [Fact]
    public void LeakScan_PassesWhenNothingOfTheSecretIsStored_AndAssertStoredFindsText()
    {
        using var db = new TranscriptTestDb();
        var secret = new FakeSecrets(3).Get("github-token");
        var session = db.AddSession("claude-code", "s1");
        db.AddChunk(session, "the token was [redacted:token] and then it was used");

        LeakScan.AssertNotStored(db.Path, secret);
        LeakScan.AssertStored(db.Path, "[redacted:token]");
        Assert.Throws<InvalidOperationException>(() => LeakScan.AssertStored(db.Path, "[redacted:bearer]"));
    }
}
