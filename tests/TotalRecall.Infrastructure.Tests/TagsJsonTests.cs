using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Direct unit tests for the hand-rolled <see cref="TagsJson"/> codec. Covers
/// encode/decode round-trips, JSON escape handling, UTF-16 surrogate pairs,
/// and the error paths added in the decoder hardening pass.
/// </summary>
public class TagsJsonTests
{
    [Fact]
    public void Encode_EmptyList_ReturnsEmptyArrayLiteral()
    {
        Assert.Equal("[]", TagsJson.Encode(Array.Empty<string>()));
    }

    [Fact]
    public void Encode_SingleElement_ReturnsSingletonArray()
    {
        Assert.Equal("[\"a\"]", TagsJson.Encode(new[] { "a" }));
    }

    [Fact]
    public void Encode_MultipleElements_SeparatedByCommas()
    {
        Assert.Equal("[\"a\",\"b\",\"c\"]", TagsJson.Encode(new[] { "a", "b", "c" }));
    }

    [Fact]
    public void Encode_StringWithQuote_EscapesQuote()
    {
        Assert.Equal("[\"a\\\"b\"]", TagsJson.Encode(new[] { "a\"b" }));
    }

    [Fact]
    public void Encode_StringWithBackslash_EscapesBackslash()
    {
        Assert.Equal("[\"a\\\\b\"]", TagsJson.Encode(new[] { "a\\b" }));
    }

    [Fact]
    public void Encode_ControlChars_EscapedAsUnicode()
    {
        // newline maps to the short \n form; 0x01 maps to \u0001.
        Assert.Equal("[\"\\n\"]", TagsJson.Encode(new[] { "\n" }));
        Assert.Equal("[\"\\u0001\"]", TagsJson.Encode(new[] { "\x01" }));
    }

    [Fact]
    public void Decode_EmptyArray_ReturnsEmptyList()
    {
        Assert.Empty(TagsJson.Decode("[]"));
    }

    [Fact]
    public void Decode_SingleElement_ReturnsSingleton()
    {
        Assert.Equal(new List<string> { "a" }, TagsJson.Decode("[\"a\"]"));
    }

    [Fact]
    public void Decode_CommonEscapes_Decoded()
    {
        var result = TagsJson.Decode("[\"\\n\\t\\\"\\\\\\/\\b\\f\\r\"]");
        Assert.Single(result);
        Assert.Equal("\n\t\"\\/\b\f\r", result[0]);
    }

    [Fact]
    public void Decode_BmpUnicodeEscape_Decoded()
    {
        var result = TagsJson.Decode("[\"\\u00e9\"]");
        Assert.Equal("é", result[0]);
    }

    [Fact]
    public void Decode_SurrogatePair_Decoded()
    {
        // U+1F600 GRINNING FACE = \uD83D\uDE00 in UTF-16. In a .NET string
        // it occupies 2 char code units.
        var result = TagsJson.Decode("[\"\\uD83D\\uDE00\"]");
        Assert.Single(result);
        Assert.Equal(2, result[0].Length);
        Assert.Equal("\uD83D\uDE00", result[0]);
    }

    [Fact]
    public void Decode_UnpairedHighSurrogate_Throws()
    {
        Assert.Throws<FormatException>(() => TagsJson.Decode("[\"\\uD83D\\u0041\"]"));
    }

    [Fact]
    public void Decode_LoneLowSurrogate_Throws()
    {
        Assert.Throws<FormatException>(() => TagsJson.Decode("[\"\\uDE00\"]"));
    }

    [Fact]
    public void Decode_MissingComma_Throws()
    {
        Assert.Throws<FormatException>(() => TagsJson.Decode("[\"a\"\"b\"]"));
    }

    [Fact]
    public void Decode_TrailingComma_Throws()
    {
        Assert.Throws<FormatException>(() => TagsJson.Decode("[\"a\",]"));
    }

    [Fact]
    public void Decode_RoundTrip_PreservesContent()
    {
        var original = new[] { "plain", "with \"quotes\"", "tab\there", "emoji \uD83D\uDE00", "é" };
        var encoded = TagsJson.Encode(original);
        var decoded = TagsJson.Decode(encoded);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_WhitespaceBetweenTokens_Accepted()
    {
        var result = TagsJson.Decode("[ \"a\" , \"b\" ]");
        Assert.Equal(new List<string> { "a", "b" }, result);
    }
}
