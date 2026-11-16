using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public class SummarySanitizerTests
{
    private sealed record FixtureCase(string Name, string Input, string Expected);

    private static string FindFixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "fixtures", "summary-sanitiser.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "scripts/fixtures/summary-sanitiser.json was not found walking up from " + AppContext.BaseDirectory);
    }

    // A small decoder of its own: System.Text.Json's GetString() throws on the fixture's lone surrogate.
    // Reads the array of { "name", "input", "expected" } objects; string escapes are decoded to chars
    // with no UTF-16 validity check.
    private static (List<FixtureCase> Cases, int NameCount) LoadFixture()
    {
        var text = File.ReadAllText(FindFixturePath(), new UTF8Encoding(false));
        var cases = new List<FixtureCase>();
        Dictionary<string, string>? current = null;
        string? pendingKey = null;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '{') { current = new Dictionary<string, string>(); pendingKey = null; i++; }
            else if (c == '}')
            {
                Assert.NotNull(current);
                cases.Add(new FixtureCase(current!["name"], current["input"], current["expected"]));
                current = null;
                i++;
            }
            else if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (text[i] != '"')
                {
                    if (text[i] != '\\') { sb.Append(text[i++]); continue; }
                    var e = text[i + 1];
                    i += 2;
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            sb.Append((char)int.Parse(text.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new InvalidDataException("unexpected escape \\" + e);
                    }
                }
                i++; // closing quote
                if (current is null) continue;
                if (pendingKey is null) pendingKey = sb.ToString();
                else { current[pendingKey] = sb.ToString(); pendingKey = null; }
            }
            else i++;
        }
        var nameCount = Regex.Matches(text, "\"name\"\\s*:").Count;
        return (cases, nameCount);
    }

    [Fact]
    public void Sanitize_EveryFixtureCase_GivesExpected()
    {
        var (cases, nameCount) = LoadFixture();
        Assert.True(nameCount > 0);
        Assert.Equal(nameCount, cases.Count);
        Assert.Contains(cases, c => c.Input.Contains('\ud83d') && !c.Input.Contains('\ude00')); // the lone surrogate runs too
        foreach (var c in cases)
            Assert.True(c.Expected == SummarySanitizer.Sanitize(c.Input), "fixture case: " + c.Name);
    }

    [Fact]
    public void Sanitize_FixtureInputs_AreIdempotent()
    {
        var (cases, _) = LoadFixture();
        foreach (var c in cases)
        {
            var once = SummarySanitizer.Sanitize(c.Input);
            Assert.True(once == SummarySanitizer.Sanitize(once), "idempotence: " + c.Name);
        }
    }

    [Fact]
    public void Sanitize_Null_ReturnsEmpty() => Assert.Equal("", SummarySanitizer.Sanitize(null));

    // The pass counter is the work measure the hostile-input test bounds (#122): the iterations of the pass-3 loop,
    // including the last one that changes nothing, and 0 for null or empty input, which returns early.
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("plain text", 1)]
    [InlineData("[a](b)", 2)]
    [InlineData("<<<https://x>>>", 4)]
    public void Sanitize_CountsThePassesOfThePassThreeLoop(string? input, int expectedPasses)
    {
        var output = SummarySanitizer.Sanitize(input, out var passes);
        Assert.Equal(expectedPasses, passes);
        Assert.Equal(SummarySanitizer.Sanitize(input), output);
    }

    [Fact]
    public void Sanitize_WhitespaceAndControlHandling_MatchesCharClassification()
    {
        for (var n = 0; n <= 0xFFFF; n++)
        {
            var c = (char)n;
            var expected = char.IsWhiteSpace(c) ? "a b" : char.IsControl(c) ? "ab" : "a" + c + "b";
            Assert.True(expected == SummarySanitizer.Sanitize("a" + c + "b"), "U+" + n.ToString("X4", CultureInfo.InvariantCulture));
        }
    }

    private static readonly Regex AnyLink = new(@"\[[^\]]*\]\([^)]*\)", RegexOptions.CultureInvariant);

    private static void AssertInvariants(string input)
    {
        var output = SummarySanitizer.Sanitize(input);
        Assert.True(output.Length <= SummarySanitizer.MaxSummary, "length of " + output.Length);
        Assert.DoesNotContain('\r', output);
        Assert.DoesNotContain('\n', output);
        Assert.False(output.Any(char.IsControl), "a control character is left");
        Assert.False(AnyLink.IsMatch(output), "a markdown link is left in: " + output);
        if (output.Length > 0) Assert.False(char.IsHighSurrogate(output[^1]), "ends in a lone high surrogate");
        Assert.Equal(output, SummarySanitizer.Sanitize(output));
    }

    [Theory]
    [InlineData("[[[a](b)](c)](d)")]
    [InlineData("<<https://x.y>>")]
    [InlineData("a\u001bb")]
    [InlineData("\u001b")]
    [InlineData("\u001b[")]
    [InlineData("\u001b\u001b[1m[")]
    public void Sanitize_HostileInputs_KeepInvariants(string input) => AssertInvariants(input);

    [Fact]
    public void Sanitize_SurrogatePairAtTheHardCut_KeepsInvariants()
    {
        var input = new string('a', 199) + "\ud83d\ude00 zzz";
        AssertInvariants(input);
        Assert.Equal(new string('a', 199), SummarySanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_LongInput_IsCappedAt200()
        => Assert.Equal(new string('x', 200), SummarySanitizer.Sanitize(new string('x', 5000)));

    [Fact]
    public void Sanitize_LinkStartingAtTheInputCap_IsCutBeforeTheLinkIsFlattened()
        => Assert.Equal("[", SummarySanitizer.Sanitize(new string(' ', 3999) + "[a](b)"));

    [Fact]
    public void Sanitize_SurrogatePairSplitByTheInputCap_KeepsOneUnitLess()
    {
        // Units 0..3997 are spaces, 3998 is 'a', 3999 and 4000 are a surrogate pair.
        var input = new string(' ', 3998) + "a\ud83d\ude00";
        Assert.Equal("a", SummarySanitizer.Sanitize(input));
        Assert.Equal(SummarySanitizer.MaxInputChars, 4000);
    }

    [Fact]
    public void Sanitize_PairEndingExactlyAtTheInputCap_IsKept()
    {
        var input = new string(' ', 3998) + "\ud83d\ude00zzz";
        Assert.Equal("\ud83d\ude00", SummarySanitizer.Sanitize(input));
    }

    private static string Pad(string s)
    {
        Assert.True(s.Length <= 4000);
        return s + new string('a', 4000 - s.Length);
    }

    public static IEnumerable<object[]> HostileTimedInputs()
    {
        yield return new object[] { Pad(new string('[', 3990) + "a](u)") };
        yield return new object[] { Pad(new string('<', 1990) + "https://x" + new string('>', 1990)) };
        yield return new object[] { string.Concat(Enumerable.Repeat("[a](", 1000)) };
        yield return new object[] { Pad(new string('[', 2000) + new string('<', 996) + "http://x" + new string('>', 996)) };
        yield return new object[] { Pad(new string('[', 1300) + "a" + string.Concat(Enumerable.Repeat("](b)", 650))) };
    }

    [Theory]
    [MemberData(nameof(HostileTimedInputs))]
    public void Sanitize_Hostile4000CharInput_FinishesUnder1Second(string input)
    {
        Assert.Equal(4000, input.Length);
        SummarySanitizer.Sanitize(input); // warm-up
        // Best of three: the bound is on the work done, not on scheduler noise on a busy CI runner.
        // Nested <https://x> pairs need one pass each, so the work is quadratic by design (about 1,990 passes over
        // 4,000 units): 100-220 ms on a GitHub-hosted runner. The backtracking regex form this replaced
        // grew about cubically, which a 1 s bound still catches without failing on a slow runner (#122).
        var best = long.MaxValue;
        string output = string.Empty;
        for (var run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            output = SummarySanitizer.Sanitize(input);
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        Assert.True(best < 1000, "took " + best + " ms");
        AssertInvariants(input);
        Assert.True(output.Length <= 200);
    }

    // ---- differential test against a reference implementation built from regular expressions ----

    private const string WsClass = " \\t\\n\\v\\f\\r\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000";
    private static readonly Regex RefAnsi = new("\u001b\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);
    private static readonly Regex RefControl = new("[\\u0000-\\u0008\\u000e-\\u001f\\u007f-\\u0084\\u0086-\\u009f]", RegexOptions.CultureInvariant);
    private static readonly Regex RefLink = new("\\[([^\\]]*)\\]\\([^)]*\\)", RegexOptions.CultureInvariant);
    private static readonly Regex RefAngle = new("<([Hh][Tt][Tt][Pp][Ss]?://[^<>" + WsClass + "]*)>", RegexOptions.CultureInvariant);
    private static readonly Regex RefWhitespace = new("[" + WsClass + "]+", RegexOptions.CultureInvariant);

    private static string Reference(string text)
    {
        var s = RefAnsi.Replace(text, "");
        s = RefControl.Replace(s, "");
        if (s.Length > 4000) s = s.Substring(0, char.IsHighSurrogate(s[3999]) ? 3999 : 4000);
        for (string? prev = null; prev != s;)
        {
            prev = s;
            s = RefLink.Replace(s, "$1");
            s = RefAngle.Replace(s, "$1");
        }
        s = RefWhitespace.Replace(s, " ");
        if (s.StartsWith(' ')) s = s.Substring(1);
        if (s.EndsWith(' ')) s = s.Substring(0, s.Length - 1);
        if (s.Length > 200)
        {
            var cut = s.LastIndexOf(' ', 200);
            if (cut > 0) s = s.Substring(0, cut);
            else
            {
                var splitsPair = char.IsHighSurrogate(s[199]) && char.IsLowSurrogate(s[200]);
                s = s.Substring(0, splitsPair ? 199 : 200);
            }
        }
        return s;
    }

    private static readonly string[] SingleChars = { "[", "]", "(", ")", "<", ">", "a", " " };
    private static readonly string[] Tokens =
    {
        "http://", "https://", "HTTPS://", "<", ">", "[a](", ")", "\n", "\u00a0", "\u017f",
        "\u001b[", "31m", "\u001b",
    };

    [Fact]
    public void Sanitize_MatchesTheReferenceImplementation_OnRandomStrings()
    {
        var rng = new Random(20261115);
        var all = new List<string>();
        for (var n = 0; n < 20000; n++)
        {
            var sb = new StringBuilder();
            var parts = rng.Next(1, 30);
            for (var p = 0; p < parts; p++)
            {
                if (rng.Next(2) == 0) sb.Append(SingleChars[rng.Next(SingleChars.Length)]);
                else sb.Append(Tokens[rng.Next(Tokens.Length)]);
            }
            all.Add(sb.ToString());
        }
        all.AddRange(LoadFixture().Cases.Select(c => c.Input));

        foreach (var input in all)
        {
            var expected = Reference(input);
            var actual = SummarySanitizer.Sanitize(input);
            Assert.True(expected == actual, "differs for " + Escape(input) + ": reference " + Escape(expected) + " actual " + Escape(actual));
        }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s) sb.Append(c is >= ' ' and < '\u007f' ? c.ToString() : "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
        return sb.Append('"').ToString();
    }
}
