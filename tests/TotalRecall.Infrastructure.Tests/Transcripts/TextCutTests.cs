using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class TextCutTests
{
    private static readonly string[] Markers =
    {
        "[redacted:token]", "[redacted:private-key]", "[redacted:bearer]", "[redacted:url-credentials]",
        "[redacted:secret-assignment]", "[redacted:unprocessable]",
    };

    [Fact]
    public void Cut_ShortText_Unchanged()
    {
        Assert.Equal("hello", TextCut.Cut("hello", 5));
        Assert.Equal("hello", TextCut.Cut("hello", 160));
        Assert.Equal("", TextCut.Cut("", 0));
    }

    [Fact]
    public void Cut_AppendsEllipsis_PrefixAtMostLimit()
    {
        var cut = TextCut.Cut("abcdefghij", 4);
        Assert.Equal("abcd" + TextCut.Ellipsis, cut);
        Assert.Equal("...", TextCut.Ellipsis);
    }

    public static TheoryData<int> OffsetsInsideMarker()
    {
        var data = new TheoryData<int>();
        // "before " is 7 characters; the marker "[redacted:token]" occupies offsets 7..22 (the ']' at 22).
        for (var limit = 8; limit <= 22; limit++) data.Add(limit);
        return data;
    }

    [Theory]
    [MemberData(nameof(OffsetsInsideMarker))]
    public void Cut_LimitInsideMarker_CutsBeforeMarker(int limit)
    {
        const string text = "before [redacted:token] after";
        Assert.Equal("before " + TextCut.Ellipsis, TextCut.Cut(text, limit));
        Assert.Equal(7, TextCut.SafeLength(text, limit));
    }

    [Fact]
    public void Cut_LimitAtMarkerBoundaries_KeepsWholeMarkerOrNone()
    {
        const string text = "before [redacted:token] after";
        Assert.Equal("before " + TextCut.Ellipsis, TextCut.Cut(text, 7));
        Assert.Equal("before [redacted:token]" + TextCut.Ellipsis, TextCut.Cut(text, 23));
    }

    [Fact]
    public void Cut_AtEveryLimit_NeverEndsInsideMarkerOrPair()
    {
        var text = "ok \U0001F600[redacted:token]\U0001F600x [redacted:secret-assignment] words \U0001F680\U0001F680"
                   + "[redacted:private-key][redacted:bearer] y\U0001F600 [redacted:url-credentials][redacted:unprocessable]";

        for (var limit = 0; limit <= text.Length; limit++)
        {
            var n = TextCut.SafeLength(text, limit);
            Assert.True(n <= limit, $"limit {limit}: {n}");
            Assert.True(n >= 0);
            if (n > 0) Assert.False(char.IsHighSurrogate(text[n - 1]), $"limit {limit}: ends on a high surrogate");
            foreach (var marker in Markers)
            {
                var at = text.IndexOf(marker, System.StringComparison.Ordinal);
                while (at >= 0)
                {
                    Assert.False(at < n && n < at + marker.Length, $"limit {limit}: n {n} inside {marker} at {at}");
                    at = text.IndexOf(marker, at + 1, System.StringComparison.Ordinal);
                }
            }

            var cut = TextCut.Cut(text, limit);
            if (limit >= text.Length) Assert.Equal(text, cut);
            else Assert.Equal(text[..n] + TextCut.Ellipsis, cut);
        }
    }

    [Fact]
    public void SafeLength_LimitBeyondText_IsLength()
    {
        Assert.Equal(5, TextCut.SafeLength("hello", 99));
        Assert.Equal(0, TextCut.SafeLength("hello", 0));
    }

    [Fact]
    public void Cap_Appends_ExactRemainderCount()
    {
        var capped = TextCut.Cap(new string('a', 100_005), 100_000);
        Assert.EndsWith(" [... 5 more characters not stored]", capped);
        Assert.Equal(new string('a', 100_000) + " [... 5 more characters not stored]", capped);
        Assert.Equal("short", TextCut.Cap("short", 100_000));
    }

    [Fact]
    public void Cap_InsideMarker_CountsTheWholeMarkerAsNotStored()
    {
        var text = new string('a', 99_990) + "[redacted:token]" + new string('b', 10);
        var capped = TextCut.Cap(text, 100_000);
        Assert.Equal(new string('a', 99_990) + " [... 26 more characters not stored]", capped);
    }

    [Fact]
    public void Fold_CollapsesNewlinesAndWhitespace()
    {
        Assert.Equal("a b c d", TextCut.Fold("  a\r\nb\n\nc \t\r d  "));
        Assert.Equal("", TextCut.Fold(" \r\n\t "));
        Assert.Equal("one", TextCut.Fold("one"));
    }

    [Fact]
    public void SafeLength_TextThatOnlyLooksLikeAMarker_IsPlainText()
    {
        const string text = "x [redacted:other] y";
        Assert.Equal(8, TextCut.SafeLength(text, 8));
        Assert.Equal("x [redac" + TextCut.Ellipsis, TextCut.Cut(text, 8));
    }
}
