using System.Diagnostics;
using System.Linq;
using System.Text;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Eval;

/// <summary>
/// The span-based redaction the transcript archive runs on every stored text (spec section 4). Every secret here comes
/// from <see cref="FakeSecrets"/>; each table row asserts the exact output.
/// </summary>
public sealed class SensitiveContentScannerRedactTests
{
    private static readonly FakeSecrets Fakes = new(7);

    private const string Token = "[redacted:token]";
    private const string PrivateKey = "[redacted:private-key]";
    private const string Bearer = "[redacted:bearer]";
    private const string UrlCredentials = "[redacted:url-credentials]";
    private const string SecretAssignment = "[redacted:secret-assignment]";
    private const string Unprocessable = "[redacted:unprocessable]";

    private static void AssertNoWindowOf(string secret, string output)
    {
        for (var i = 0; i + 8 <= secret.Length; i++)
        {
            var window = secret.Substring(i, 8);
            Assert.False(output.Contains(window, System.StringComparison.Ordinal),
                $"output holds the 8-character window at offset {i} of the secret");
        }
    }

    // ---- token ----

    [Theory]
    [InlineData("github-token")]
    [InlineData("github-pat")]
    [InlineData("gitlab-token")]
    [InlineData("slack-token")]
    [InlineData("openai-key")]
    [InlineData("anthropic-key")]
    [InlineData("openai-project-key")]
    [InlineData("openai-svcacct-key")]
    [InlineData("openai-admin-key")]
    [InlineData("aws-key")]
    public void Redact_TokenShapes_BecomeTokenMarker(string name)
    {
        var secret = Fakes.Get(name);
        var result = SensitiveContentScanner.Redact("use " + secret + " for the call");

        Assert.Equal("use " + Token + " for the call", result.Text);
        Assert.Equal(1, result.Count);
        Assert.Equal(new[] { "token" }, result.Categories);
        AssertNoWindowOf(secret, result.Text);
    }

    [Theory]
    [InlineData("None-")]
    [InlineData("or-v1-")]
    [InlineData("live_")]
    public void Redact_GeneralSkKeys_BecomeTokenMarker(string infix)
    {
        var secret = "s" + "k-" + infix + Fakes.Get("bearer-token")[..12] + "-" + Fakes.Get("bearer-token")[12..24];
        var result = SensitiveContentScanner.Redact("key " + secret + " here");

        Assert.Equal("key " + Token + " here", result.Text);
        Assert.Equal(1, result.Count);
        AssertNoWindowOf(secret, result.Text);
    }

    [Theory]
    [InlineData("task-list-management-and-more")]
    [InlineData("disk-usage-monitoring-tool-extra")]
    [InlineData("mask-abcdefghijklmnopqrst")]
    public void Redact_SkWithoutLeftBoundary_IsKept(string text)
    {
        var result = SensitiveContentScanner.Redact(text);
        Assert.Equal(text, result.Text);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void Redact_ExtendedSkShortSuffix_IsKept()
    {
        var text = "s" + "k-proj-" + "abcdefghij123456789";
        var result = SensitiveContentScanner.Redact(text);
        Assert.Equal(text, result.Text);
        Assert.Equal(0, result.Count);
    }

    // ---- private-key ----

    [Fact]
    public void Redact_PrivateKey_BeginToEnd_RestKept()
    {
        var key = Fakes.Get("private-key");
        var result = SensitiveContentScanner.Redact("before\n" + key + "\nafter the key");

        Assert.Equal("before\n" + PrivateKey + "\nafter the key", result.Text);
        Assert.Equal(1, result.Count);
        Assert.Equal(new[] { "private-key" }, result.Categories);
    }

    [Fact]
    public void Redact_PrivateKeyWithoutEnd_ToEndOfText()
    {
        var key = Fakes.Get("private-key-unterminated");
        var result = SensitiveContentScanner.Redact("before\n" + key + "\nand this trailing text");

        Assert.Equal("before\n" + PrivateKey, result.Text);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void Redact_PrivateKeyWithOtherLabelEnd_ToEndOfText()
    {
        var key = Fakes.Get("private-key-unterminated");
        var otherEnd = new string('-', 5) + "END EC " + "PRIVATE KEY" + new string('-', 5);
        var result = SensitiveContentScanner.Redact("before\n" + key + "\n" + otherEnd + "\nafter");

        Assert.Equal("before\n" + PrivateKey, result.Text);
        Assert.Equal(1, result.Count);
    }

    // ---- bearer ----

    public static TheoryData<string, string> BearerRows()
    {
        var v = Fakes.Get("bearer-token");
        return new TheoryData<string, string>
        {
            { "Authorization: Bearer " + v, "Authorization: Bearer " + Bearer },
            { "Authorization=Basic " + v, "Authorization=Basic " + Bearer },
            { "Authorization: Digest " + v, "Authorization: Digest " + Bearer },
            { "\"Authorization\": \"Token " + v + "\"", "\"Authorization\": \"Token " + Bearer + "\"" },
            { "Authorization: " + v, "Authorization: " + Bearer },
            { "x-api-key: " + v, "x-api-key: " + Bearer },
            { "Bearer " + v, "Bearer " + Bearer },
            { "authorization: bearer " + v, "authorization: bearer " + Bearer },
            { "curl -H \"Authorization: Bearer " + v + "\" https://example.com",
              "curl -H \"Authorization: Bearer " + Bearer + "\" https://example.com" },
            { "{\\\"Authorization\\\": \\\"Bearer " + v + "\\\"}", "{\\\"Authorization\\\": \\\"Bearer " + Bearer + "\\\"}" },
            { "{\"command\":\"curl -H \\\"Authorization: Bearer " + v + "\\\" https://x\"}",
              "{\"command\":\"curl -H \\\"Authorization: Bearer " + Bearer + "\\\" https://x\"}" },
            { "Proxy-Authorization: Basic " + v + "\r\nHost: x", "Proxy-Authorization: Basic " + Bearer + "\r\nHost: x" },
            { "Authorization: " + v[..8], "Authorization: " + Bearer },
            { "Bearer " + v[..16], "Bearer " + Bearer },
            // Long runs of blanks around the separator (aligned code, gofmt, terraform fmt).
            { "Authorization" + new string(' ', 13) + ":" + new string(' ', 11) + "Basic " + v,
              "Authorization" + new string(' ', 13) + ":" + new string(' ', 11) + "Basic " + Bearer },
            { "x-api-key:" + new string(' ', 11) + v, "x-api-key:" + new string(' ', 11) + Bearer },
            { "Bearer" + new string(' ', 11) + v, "Bearer" + new string(' ', 11) + Bearer },
            // An index, a PHP/Ruby arrow and a quoted name followed by a comma.
            { "headers[\"Authorization\"] = \"Basic " + v + "==\"", "headers[\"Authorization\"] = \"Basic " + Bearer + "\"" },
            { "$h = ['Authorization' => 'Basic " + v + "'];", "$h = ['Authorization' => 'Basic " + Bearer + "'];" },
            { "req.Header.Set(\"Authorization\", \"Basic " + v + "\")", "req.Header.Set(\"Authorization\", \"Basic " + Bearer + "\")" },
            { "headers['x-api-key'] = '" + v + "'", "headers['x-api-key'] = '" + Bearer + "'" },
            // Schemes outside the listed four are skipped as one word, so the credential after them is the value.
            { "Authorization: ApiKey " + v, "Authorization: ApiKey " + Bearer },
            { "Authorization: SSWS " + v, "Authorization: SSWS " + Bearer },
            { "Authorization: Key " + v, "Authorization: Key " + Bearer },
            { "Authorization: Bot " + v, "Authorization: Bot " + Bearer },
            { "\"Authorization\": \"ApiKey " + v + "\"", "\"Authorization\": \"ApiKey " + Bearer + "\"" },
            // A scheme-less credential followed by more words is still redacted.
            { "Authorization: " + v + " ok", "Authorization: " + Bearer + " ok" },
        };
    }

    [Theory]
    [MemberData(nameof(BearerRows))]
    public void Redact_Bearer(string input, string expected)
    {
        var result = SensitiveContentScanner.Redact(input);
        Assert.Equal(expected, result.Text);
        Assert.Equal(1, result.Count);
        Assert.Equal(new[] { "bearer" }, result.Categories);
    }

    [Fact]
    public void Redact_BearerTooShort_IsKept()
    {
        var v = Fakes.Get("bearer-token");
        foreach (var text in new[] { "Authorization: " + v[..7], "Bearer " + v[..15] })
        {
            var result = SensitiveContentScanner.Redact(text);
            Assert.Equal(text, result.Text);
            Assert.Equal(0, result.Count);
        }
    }

    // ---- url-credentials ----

    [Fact]
    public void Redact_UrlCredentials_PasswordOnly()
    {
        var spec = SensitiveContentScanner.Redact("mysql://root:P@ssw0rd@localhost");
        Assert.Equal("mysql://root:" + UrlCredentials + "@localhost", spec.Text);
        Assert.Equal(1, spec.Count);
        Assert.Equal(new[] { "url-credentials" }, spec.Categories);

        var pw = Fakes.Get("url-password");
        Assert.Equal(16, pw.Length);
        Assert.Contains("/", pw);
        Assert.Contains("+", pw);
        Assert.Contains("@", pw);
        var result = SensitiveContentScanner.Redact("connect postgres://admin:" + pw + "@db.example.com:5432/app now");
        Assert.Equal("connect postgres://admin:" + UrlCredentials + "@db.example.com:5432/app now", result.Text);
        Assert.Equal(1, result.Count);
        AssertNoWindowOf(pw, result.Text);
    }

    // ---- secret-assignment ----

    public static TheoryData<string, string> SecretAssignmentRows()
    {
        var k = Fakes.Get("api-key-value");
        var b = Fakes.Get("bearer-token");
        var left = b[..10];
        var right = b[10..20];
        return new TheoryData<string, string>
        {
            { "API_KEY=" + k + ")", "API_KEY=" + SecretAssignment },
            { "DB_PASSWORD=\"" + left + "\\\"" + right + "\" next", "DB_PASSWORD=\"" + SecretAssignment + "\" next" },
            { "{\\\"api_key\\\": \\\"" + b + "\\\"}", "{\\\"api_key\\\": \\\"" + SecretAssignment + "\\\"}" },
            { "{\"client_secret\": \"" + b + "\"}", "{\"client_secret\": \"" + SecretAssignment + "\"}" },
            { "export GH_TOKEN=" + k, "export GH_TOKEN=" + SecretAssignment },
            { "PASSWORD='$ecret!9'", "PASSWORD='" + SecretAssignment + "'" },
            { "PASSWORD=ab", "PASSWORD=" + SecretAssignment },
            { "TOKEN=$(echo hunter2) && run", "TOKEN=" + SecretAssignment + " && run" },
            { "TOKEN=`echo hunter2` && run", "TOKEN=" + SecretAssignment + " && run" },
            { "TOKEN=$(echo $(cat f) x) && run", "TOKEN=" + SecretAssignment + " && run" },
            { "tool --password hunter2 --verbose", "tool --password " + SecretAssignment + " --verbose" },
            { "tool --api-key=" + k, "tool --api-key=" + SecretAssignment },
            { "TOKEN=${X:-literal}", "TOKEN=" + SecretAssignment },
            { "PASSWORD='$Tr0ub4dor9'", "PASSWORD='" + SecretAssignment + "'" },
            { "PASSWORD=<abc", "PASSWORD=" + SecretAssignment },
            { "PASSWORD={x", "PASSWORD=" + SecretAssignment },
            { "PASSWORD=%x", "PASSWORD=" + SecretAssignment },
            { "password: " + b, "password: " + SecretAssignment },
            { "api_key = \"" + b + "\"", "api_key = \"" + SecretAssignment + "\"" },
            { "AWS_SECRET_ACCESS_KEY=" + b, "AWS_SECRET_ACCESS_KEY=" + SecretAssignment },
            { "TOKEN=\"unterminated " + b, "TOKEN=\"" + SecretAssignment },
            // Long runs of blanks around the separator (terraform fmt, gofmt, aligned flags).
            { "password" + new string(' ', 13) + "= \"" + b + "\"", "password" + new string(' ', 13) + "= \"" + SecretAssignment + "\"" },
            { "Password:" + new string(' ', 11) + "\"" + b + "\",", "Password:" + new string(' ', 11) + "\"" + SecretAssignment + "\"," },
            { "tool --password" + new string(' ', 11) + b + " -v", "tool --password" + new string(' ', 11) + SecretAssignment + " -v" },
            // Double-encoded JSON: an escaped quote of the inner layer (\\\") does not close the value.
            { "{\\\"password\\\": \\\"" + left + "\\\\\\\"" + right + "\\\"}", "{\\\"password\\\": \\\"" + SecretAssignment + "\\\"}" },
            // Punctuation inside or after an unquoted value is part of the run.
            { "TOKEN=" + left + "," + right, "TOKEN=" + SecretAssignment },
            { "TOKEN=" + left + "}", "TOKEN=" + SecretAssignment },
        };
    }

    [Theory]
    [MemberData(nameof(SecretAssignmentRows))]
    public void Redact_SecretAssignment(string input, string expected)
    {
        var result = SensitiveContentScanner.Redact(input);
        Assert.Equal(expected, result.Text);
        Assert.Equal(1, result.Count);
        Assert.Equal(new[] { "secret-assignment" }, result.Categories);
    }

    [Theory]
    [InlineData("GH_TOKEN=\"$t\" gh api repos/x/y")]
    [InlineData("TOKEN=${GH_TOKEN}")]
    [InlineData("PASSWORD=%PASS%")]
    [InlineData("API_KEY=<your-key>")]
    [InlineData("TOKEN={{ secrets.X }}")]
    [InlineData("SECRET=true")]
    [InlineData("TOKEN=null")]
    [InlineData("API_KEY=\nnext line")]
    [InlineData("PASSWORD=\"\"")]
    [InlineData("scheme://user:${PASS}@host")]
    [InlineData("Authorization: Bearer ${GITHUB_TOKEN}")]
    [InlineData("Authorization: Bearer <your-access-token>")]
    [InlineData("{\"token\": null, \"a\": 1}")]
    [InlineData("{\"api_key\": true}")]
    [InlineData("TOKEN=${GH_TOKEN};")]
    public void Redact_ReferencesAndPlaceholders_AreKept(string text)
    {
        var result = SensitiveContentScanner.Redact(text);
        Assert.Equal(text, result.Text);
        Assert.Equal(0, result.Count);
        Assert.Empty(result.Categories);
    }

    [Fact]
    public void Redact_Emails_AreKept()
    {
        const string text = "write to jane.doe@example.com or ops+alerts@corp.example.org";
        var result = SensitiveContentScanner.Redact(text);
        Assert.Equal(text, result.Text);
        Assert.Equal(0, result.Count);
    }

    // ---- overlaps, counts, edges ----

    [Fact]
    public void Redact_Overlap_IsOneMarker_FirstStartWins()
    {
        var token = Fakes.Get("github-token");

        var tie = SensitiveContentScanner.Redact("Authorization: Bearer " + token);
        Assert.Equal("Authorization: Bearer " + Token, tie.Text);
        Assert.Equal(1, tie.Count);
        Assert.Equal(new[] { "token" }, tie.Categories);

        var bearerFirst = SensitiveContentScanner.Redact("Authorization: Bearer xyz0" + token);
        Assert.Equal("Authorization: Bearer " + Bearer, bearerFirst.Text);
        Assert.Equal(1, bearerFirst.Count);
        Assert.Equal(new[] { "bearer" }, bearerFirst.Categories);
        AssertNoWindowOf(token, bearerFirst.Text);
    }

    [Fact]
    public void Redact_NullOrEmpty_ReturnsEmptyWithCountZero()
    {
        var empty = SensitiveContentScanner.Redact("");
        Assert.Equal("", empty.Text);
        Assert.Equal(0, empty.Count);
        Assert.Empty(empty.Categories);

        var none = SensitiveContentScanner.Redact(null);
        Assert.Equal("", none.Text);
        Assert.Equal(0, none.Count);
        Assert.Empty(none.Categories);
    }

    [Fact]
    public void Redact_CountAndCategories_InOrderOfFirstMarker()
    {
        var text = "x-api-key: " + Fakes.Get("bearer-token")
                   + " then " + Fakes.Get("aws-key")
                   + " then Bearer " + Fakes.Get("bearer-token")
                   + " then PASSWORD=" + Fakes.Get("api-key-value")
                   + " then " + Fakes.Get("openai-key");
        var result = SensitiveContentScanner.Redact(text);

        Assert.Equal(
            "x-api-key: " + Bearer + " then " + Token + " then Bearer " + Bearer + " then PASSWORD=" + SecretAssignment
            + " then " + Token,
            result.Text);
        Assert.Equal(5, result.Count);
        Assert.Equal(new[] { "bearer", "token", "secret-assignment" }, result.Categories);
    }

    // ---- adversarial inputs and the fail-closed timeout ----

    private const int OneMiB = 1 << 20;

    private static string Repeat(string unit, int length)
    {
        var sb = new StringBuilder(length + unit.Length);
        while (sb.Length < length) sb.Append(unit);
        return sb.ToString(0, length);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("=")]
    [InlineData(":")]
    [InlineData("\"")]
    [InlineData("\\\"")]
    [InlineData("A_KEY=")]
    [InlineData("-----BEGIN ")]
    [InlineData("Authorization: ")]
    [InlineData("x://a:")]
    [InlineData("sk-")]
    [InlineData("_")]
    [InlineData("'")]
    [InlineData("$(")]
    [InlineData("{{")]
    [InlineData("TOKEN=\"")]
    [InlineData("Bearer ")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("_key            ")]
    [InlineData("Authorization            ")]
    [InlineData("Authorization: Key ")]
    [InlineData("\\")]
    [InlineData("\\\"api_key\\\": \\\"\\\\\\")]
    [InlineData("TOKEN=null},")]
    public void Redact_AdversarialInputs_FinishWellUnderASecond(string unit)
    {
        var text = Repeat(unit, OneMiB);
        SensitiveContentScanner.Redact("warm up the patterns: TOKEN=x");

        var sw = Stopwatch.StartNew();
        var result = SensitiveContentScanner.Redact(text);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.DoesNotContain(Unprocessable, result.Text);
    }

    [Theory]
    [InlineData("_key", ' ')]
    [InlineData("_key", '\t')]
    [InlineData("password", ' ')]
    [InlineData("\"token\"", ' ')]
    [InlineData("Authorization", ' ')]
    [InlineData("\"Authorization\"", ' ')]
    [InlineData("x-api-key", ' ')]
    [InlineData("--password", ' ')]
    [InlineData("Bearer", ' ')]
    [InlineData("Authorization: Bearer", ' ')]
    public void Redact_NameThenAMebibyteOfBlanks_FinishesWellUnderASecond(string name, char blank)
    {
        var text = name + new string(blank, OneMiB);
        SensitiveContentScanner.Redact("warm up the patterns: TOKEN=x");

        var sw = Stopwatch.StartNew();
        var result = SensitiveContentScanner.Redact(text);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void Redact_OnTimeout_FailsClosed()
    {
        // Dense with places where a pattern must be tried but never matches, so a single match call runs for tens of
        // milliseconds: long enough to pass a 1-tick timeout even where the clock it is checked against is coarse.
        var mixed = Repeat("the api_key is api_key, task: sk-a ghp x token Authorization ok, mask-ab \"q\" p://h ", OneMiB)
                    + " TOKEN=" + Fakes.Get("bearer-token");

        var result = SensitiveContentScanner.Redact(mixed, System.TimeSpan.FromTicks(1));

        Assert.Equal(Unprocessable, result.Text);
        Assert.Equal(1, result.Count);
        Assert.Equal(new[] { "unprocessable" }, result.Categories);
    }

    [Fact]
    public void RedactionVersion_IsOne()
    {
        Assert.Equal(1, SensitiveContentScanner.RedactionVersion);
    }
}
