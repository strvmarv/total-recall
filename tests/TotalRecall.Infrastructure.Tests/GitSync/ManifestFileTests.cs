using System.Text;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class ManifestFileTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    private static object[] Row(string name, byte[] bytes) => [name, bytes];

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    private static byte[] WithBom(byte[] body) => [0xEF, 0xBB, 0xBF, .. body];

    private static string V1(string extra = "") =>
        $$"""{"format":"total-recall/v1","repo_id":"{{Id}}","created_by":"total-recall 4.7.0","created_at":"2026-11-15T10:00:00Z"{{extra}}}""";

    private static byte[] PadTo(string json, int length)
    {
        // pad with whitespace after the closing brace so the object itself is unchanged
        var bytes = Utf8(json);
        return [.. bytes, .. Enumerable.Repeat((byte)' ', Math.Max(0, length - bytes.Length))];
    }

    [Fact]
    public void AValidV1Manifest_IsOk_WithItsFields()
    {
        var parse = ManifestFile.Parse(Utf8(V1() + "\n"));

        Assert.Equal(ManifestParseKind.Ok, parse.Kind);
        Assert.Equal(Id, parse.Manifest!.RepoId);
        Assert.Equal("total-recall/v1", parse.Manifest.Format);
        Assert.Equal("total-recall 4.7.0", parse.Manifest.CreatedBy);
        Assert.Equal("2026-11-15T10:00:00Z", parse.Manifest.CreatedAt);
    }

    [Fact]
    public void AnotherKeyOrder_AnUnknownKey_AndMissingOptionalFields_AreOk()
    {
        var reordered = Utf8($$"""{"repo_id":"{{Id}}","extra":{"a":[1,2]},"format":"total-recall/v1"}""");

        var parse = ManifestFile.Parse(reordered);

        Assert.Equal(ManifestParseKind.Ok, parse.Kind);
        Assert.Null(parse.Manifest!.CreatedBy);
        Assert.Null(parse.Manifest.CreatedAt);
    }

    public static IEnumerable<object[]> InvalidManifests()
    {
        yield return Row("not json", Utf8("hello"));
        yield return Row("array root", Utf8("[]"));
        yield return Row("duplicate repo_id", Utf8($$"""{"format":"total-recall/v1","repo_id":"{{Id}}","repo_id":"{{Id}}"}"""));
        yield return Row("upper-case id", Utf8($$"""{"format":"total-recall/v1","repo_id":"{{Id.ToUpperInvariant()}}"}"""));
        yield return Row("31-digit id", Utf8($$"""{"format":"total-recall/v1","repo_id":"{{Id[1..]}}"}"""));
        yield return Row("numeric format", Utf8($$"""{"format":1,"repo_id":"{{Id}}"}"""));
        yield return Row("no format", Utf8($$"""{"repo_id":"{{Id}}"}"""));
        yield return Row("no repo_id", Utf8("""{"format":"total-recall/v1"}"""));
        yield return Row("format v0", Utf8($$"""{"format":"total-recall/v0","repo_id":"{{Id}}"}"""));
        yield return Row("other format", Utf8($$"""{"format":"other","repo_id":"{{Id}}"}"""));
        yield return Row("invalid UTF-8", [.. Utf8("{\"format\":\"total-recall/v1\",\"repo_id\":\"" + Id + "\",\"created_by\":\""), 0xFF, .. Utf8("\"}")]);
        yield return Row("BOM before v1", WithBom(Utf8(V1())));
        yield return Row("created_by is a number", Utf8($$"""{"format":"total-recall/v1","repo_id":"{{Id}}","created_by":5}"""));
        yield return Row("content after the object", Utf8(V1() + " x"));
        yield return Row("truncated object", Utf8("{\"format\":\"total-recall/v1\",\"repo_id\":\"" + Id + "\""));
        yield return Row("empty", []);
    }

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void AnInvalidManifest_IsRefused_WithAReason(string name, byte[] bytes)
    {
        var parse = ManifestFile.Parse(bytes);

        Assert.True(parse.Kind == ManifestParseKind.Invalid, name);
        Assert.False(string.IsNullOrEmpty(parse.Reason), name);
    }

    [Fact]
    public void The4096ByteBoundary_AtV1()
    {
        Assert.Equal(ManifestParseKind.Ok, ManifestFile.Parse(PadTo(V1(), 4096)).Kind);
        Assert.Equal(ManifestParseKind.Invalid, ManifestFile.Parse(PadTo(V1(), 4097)).Kind);
    }

    public static IEnumerable<object[]> NewerManifests()
    {
        yield return Row("alone", Utf8("""{"format":"total-recall/v2"}"""));
        yield return Row("BOM", WithBom(Utf8("""{"format":"total-recall/v2"}""")));
        yield return Row("60000 bytes", PadTo("""{"format":"total-recall/v2"}""", 60000));
        yield return Row("duplicate key", Utf8("""{"format":"total-recall/v2","x":1,"x":2}"""));
        yield return Row("bad repo_id", Utf8("""{"format":"total-recall/v2","repo_id":"nope"}"""));
        yield return Row("repo_id a number", Utf8("""{"format":"total-recall/v2","repo_id":5}"""));
        yield return Row("after other keys", Utf8("""{"a":{"b":[1]},"format":"total-recall/v2"}"""));
    }

    [Theory]
    [MemberData(nameof(NewerManifests))]
    public void ANewerFormat_WinsOverEveryOtherRefusal(string name, byte[] bytes)
    {
        var parse = ManifestFile.Parse(bytes);

        Assert.True(parse.Kind == ManifestParseKind.Newer, name);
        Assert.Equal(2, parse.NewerVersion);
    }

    [Fact]
    public void ANewerFormat_WithInvalidUtf8_IsInvalid()
    {
        byte[] bytes = [.. Utf8("{\"format\":\"total-recall/v2\",\"created_by\":\""), 0xFF, .. Utf8("\"}")];

        Assert.Equal(ManifestParseKind.Invalid, ManifestFile.Parse(bytes).Kind);
    }

    [Fact]
    public void ALoneSurrogateEscape_IsInvalid_NotAnException_AndANewerFormatStillWins()
    {
        Assert.Equal(ManifestParseKind.Invalid, ManifestFile.Parse(Utf8($$"""{"format":"total-recall/v1","repo_id":"{{Id}}","created_by":"\ud800"}""")).Kind);
        Assert.Equal(ManifestParseKind.Invalid, ManifestFile.Parse(Utf8($$"""{"format":"total-recall/v1","repo_id":"{{Id}}","\ud800":1}""")).Kind);
        Assert.Equal(ManifestParseKind.Invalid, ManifestFile.Parse(Utf8("""{"format":"\ud800"}""")).Kind);
        Assert.Equal(ManifestParseKind.Newer, ManifestFile.Parse(Utf8("""{"format":"total-recall/v2","x":"\ud800"}""")).Kind);
        Assert.Equal(ManifestParseKind.Newer, ManifestFile.Parse(Utf8("""{"format":"total-recall/v2","\ud800":1}""")).Kind);
        Assert.Equal(ManifestParseKind.Newer, ManifestFile.Parse(Utf8("""{"\ud800":1,"format":"total-recall/v2"}""")).Kind);
    }

    [Theory]
    [InlineData("""{"format":"total-recall/v2","x":""")]
    [InlineData("""{"format":"total-recall/v2",,,}""")]
    [InlineData("""{"format":"total-recall/v2"} garbage""")]
    public void NonJson_NeverReadsAsANewerFormat(string json) =>
        Assert.Equal(ManifestParseKind.Invalid, ManifestFile.Parse(Utf8(json)).Kind);

    [Theory]
    [InlineData("""{"format":"total-recall/v2","format":"total-recall/v1"}""", ManifestParseKind.Newer)]
    [InlineData("""{"format":"total-recall/v1","format":"total-recall/v2"}""", ManifestParseKind.Invalid)]
    [InlineData("""{"format":5,"format":"total-recall/v2"}""", ManifestParseKind.Invalid)]
    public void OfADuplicatedFormat_TheFirstIsUsed(string json, ManifestParseKind expected) =>
        Assert.Equal(expected, ManifestFile.Parse(Utf8(json)).Kind);

    [Fact]
    public void ASpanOver64KiB_IsInvalid_EvenWhenItNamesANewerFormat()
    {
        Assert.Equal(ManifestParseKind.Invalid, ManifestFile.Parse(PadTo("""{"format":"total-recall/v2"}""", 65537)).Kind);
        Assert.Equal(ManifestParseKind.Newer, ManifestFile.Parse(PadTo("""{"format":"total-recall/v2"}""", 65536)).Kind);
    }

    [Fact]
    public void RenderThenParse_RoundTrips()
    {
        var bytes = ManifestFile.Render(Id, "total-recall 4.7.0", "2026-11-15T10:00:00Z");

        var parse = ManifestFile.Parse(bytes);

        Assert.Equal(ManifestParseKind.Ok, parse.Kind);
        Assert.Equal(new ParsedManifest("total-recall/v1", Id, "total-recall 4.7.0", "2026-11-15T10:00:00Z"), parse.Manifest);
    }
}
