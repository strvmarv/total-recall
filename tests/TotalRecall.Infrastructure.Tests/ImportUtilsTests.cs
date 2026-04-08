using TotalRecall.Infrastructure.Importers;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Unit tests for <see cref="ImportUtils.ParseFrontmatter"/>, porting the
/// behaviour of <c>src-ts/importers/import-utils.ts</c>. Pure string-in /
/// string-out — no DB, no file I/O, not marked Integration.
/// </summary>
public class ImportUtilsTests
{
    [Fact]
    public void ParseFrontmatter_NoMarkers_ReturnsNullFrontmatterAndOriginalContent()
    {
        var result = ImportUtils.ParseFrontmatter("hello world");

        Assert.Null(result.Frontmatter);
        Assert.Equal("hello world", result.Content);
    }

    [Fact]
    public void ParseFrontmatter_StandardYamlBlock_ParsesAllThreeFields()
    {
        const string raw = "---\nname: greeting\ndescription: a hello message\ntype: user\n---\nhello world\n";

        var result = ImportUtils.ParseFrontmatter(raw);

        Assert.NotNull(result.Frontmatter);
        Assert.Equal("greeting", result.Frontmatter!.Name);
        Assert.Equal("a hello message", result.Frontmatter.Description);
        Assert.Equal("user", result.Frontmatter.Type);
        Assert.Equal("hello world\n", result.Content);
    }

    [Fact]
    public void ParseFrontmatter_PartialFields_LeavesUnsetFieldsNull()
    {
        const string raw = "---\nname: only-name\n---\nbody";

        var result = ImportUtils.ParseFrontmatter(raw);

        Assert.NotNull(result.Frontmatter);
        Assert.Equal("only-name", result.Frontmatter!.Name);
        Assert.Null(result.Frontmatter.Description);
        Assert.Null(result.Frontmatter.Type);
        Assert.Equal("body", result.Content);
    }

    [Fact]
    public void ParseFrontmatter_CrlfLineEndings_NormalizedBeforeParse()
    {
        const string crlf = "---\r\nname: crlf\r\ntype: test\r\n---\r\nbody\r\n";
        const string lf = "---\nname: crlf\ntype: test\n---\nbody\n";

        var crlfResult = ImportUtils.ParseFrontmatter(crlf);
        var lfResult = ImportUtils.ParseFrontmatter(lf);

        Assert.Equal(lfResult.Frontmatter, crlfResult.Frontmatter);
        Assert.Equal(lfResult.Content, crlfResult.Content);
        Assert.Equal("crlf", crlfResult.Frontmatter!.Name);
        Assert.Equal("test", crlfResult.Frontmatter.Type);
    }

    [Fact]
    public void ParseFrontmatter_UnknownKey_IsIgnored()
    {
        const string raw = "---\ncategory: foo\nauthor: bob\n---\nbody";

        var result = ImportUtils.ParseFrontmatter(raw);

        Assert.NotNull(result.Frontmatter);
        Assert.Null(result.Frontmatter!.Name);
        Assert.Null(result.Frontmatter.Description);
        Assert.Null(result.Frontmatter.Type);
        Assert.Equal("body", result.Content);
    }

    [Fact]
    public void ParseFrontmatter_NoClosingMarker_ReturnsNullFrontmatter()
    {
        const string raw = "---\nname: unterminated\nno closing marker here\nstill going";

        var result = ImportUtils.ParseFrontmatter(raw);

        Assert.Null(result.Frontmatter);
        Assert.Equal(raw, result.Content);
    }

    [Fact]
    public void ParseFrontmatter_KeyWithExtraSpaces_TrimmedValue()
    {
        const string raw = "---\nname:    foo   \n---\nbody";

        var result = ImportUtils.ParseFrontmatter(raw);

        Assert.NotNull(result.Frontmatter);
        Assert.Equal("foo", result.Frontmatter!.Name);
    }

    [Fact]
    public void ParseFrontmatter_EmptyValue_StoredAsEmptyString()
    {
        const string raw = "---\nname:\n---\nbody";

        var result = ImportUtils.ParseFrontmatter(raw);

        Assert.NotNull(result.Frontmatter);
        Assert.Equal(string.Empty, result.Frontmatter!.Name);
    }

    [Fact]
    public void ParseFrontmatter_FrontmatterAtMidFile_NotMatched()
    {
        const string raw = "preamble\n---\nname: mid\n---\nbody";

        var result = ImportUtils.ParseFrontmatter(raw);

        Assert.Null(result.Frontmatter);
        Assert.Equal(raw, result.Content);
    }

    [Fact]
    public void ParseFrontmatter_EmptyFrontmatterBody_ReturnsAllNullFields()
    {
        const string raw = "---\n\n---\ntail";

        var result = ImportUtils.ParseFrontmatter(raw);

        Assert.NotNull(result.Frontmatter);
        Assert.Null(result.Frontmatter!.Name);
        Assert.Null(result.Frontmatter.Description);
        Assert.Null(result.Frontmatter.Type);
        Assert.Equal("tail", result.Content);
    }

    [Fact]
    public void ImportResult_Empty_HasZeroCountsAndNoErrors()
    {
        var empty = ImportResult.Empty;

        Assert.Equal(0, empty.Imported);
        Assert.Equal(0, empty.Skipped);
        Assert.Empty(empty.Errors);
    }
}
