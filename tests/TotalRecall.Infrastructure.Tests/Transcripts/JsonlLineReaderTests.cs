using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class JsonlLineReaderTests
{
    private const int Limit = 16 * 1024 * 1024;

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static List<JsonlLine> ReadAll(byte[] data, long start = 0, int limit = Limit)
    {
        using var ms = new MemoryStream(data);
        return JsonlLineReader.Read(ms, start, limit, CancellationToken.None).ToList();
    }

    private static string Text(JsonlLine l) => Encoding.UTF8.GetString(l.Bytes.Span);

    [Fact]
    public void Reader_YieldsCompleteLinesWithByteOffsets()
    {
        // The second line holds a multi-byte character, so byte offsets differ from character offsets.
        var lines = ReadAll(B("{\"a\":1}\n{\"b\":\"é\"}\n{\"c\":3}\n"));
        Assert.Equal(3, lines.Count);
        Assert.Equal((0L, 8L, "{\"a\":1}"), (lines[0].Start, lines[0].End, Text(lines[0])));
        Assert.Equal((8L, 19L, "{\"b\":\"é\"}"), (lines[1].Start, lines[1].End, Text(lines[1])));
        Assert.Equal((19L, 27L, "{\"c\":3}"), (lines[2].Start, lines[2].End, Text(lines[2])));
        Assert.All(lines, l => Assert.False(l.TooLong));
    }

    [Fact]
    public void Reader_StartOffset_YieldsLinesFromThere()
    {
        var lines = ReadAll(B("aaa\nbbbb\ncc\n"), start: 4);
        Assert.Equal(2, lines.Count);
        Assert.Equal((4L, 9L, "bbbb"), (lines[0].Start, lines[0].End, Text(lines[0])));
        Assert.Equal((9L, 12L, "cc"), (lines[1].Start, lines[1].End, Text(lines[1])));
    }

    [Fact]
    public void Reader_TrailingPartialLine_IsNotYielded()
    {
        var lines = ReadAll(B("one\ntwo\nthr"));
        Assert.Equal(2, lines.Count);
        Assert.Equal(8L, lines[^1].End);
        Assert.Empty(ReadAll(B("no newline at all")));
    }

    [Fact]
    public void Reader_PartialLineCompletedLater_IsReadOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tr-jsonl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "s.jsonl");
            File.WriteAllBytes(path, B("first\nsec"));
            long watermark;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var first = JsonlLineReader.Read(fs, 0, Limit, CancellationToken.None).ToList();
                Assert.Single(first);
                watermark = first[0].End;
                Assert.Equal(6L, watermark);
            }
            using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                append.Write(B("ond\n"));
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var second = JsonlLineReader.Read(fs, watermark, Limit, CancellationToken.None).ToList();
                Assert.Single(second);
                Assert.Equal((6L, 13L, "second"), (second[0].Start, second[0].End, Text(second[0])));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Reader_LineOverLimit_IsTooLong_AndReadingContinues()
    {
        var big = new string('x', 5000);
        var data = B("ok1\n" + big + "\nok2\n" + new string('y', 1024) + "\nok3\n");
        var lines = ReadAll(data, limit: 1024);
        Assert.Equal(5, lines.Count);
        Assert.Equal(new[] { false, true, false, false, false }, lines.Select(l => l.TooLong).ToArray());
        Assert.Equal(4L, lines[1].Start);
        Assert.Equal(4L + 5000 + 1, lines[1].End);
        Assert.True(lines[1].Bytes.IsEmpty);
        Assert.Equal("ok2", Text(lines[2]));
        // A line of exactly the limit is not too long.
        Assert.Equal(1024, lines[3].Bytes.Length);
        Assert.Equal("ok3", Text(lines[4]));
    }

    [Fact]
    public void Reader_OddLines_CrlfBomBlank()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var data = bom.Concat(B("{\"a\":1}\r\n\n   \r\n{\"b\":2}\n")).ToArray();
        var lines = ReadAll(data);
        Assert.Equal(4, lines.Count);

        // BOM is inside Start..End but not in Bytes.
        Assert.Equal(0L, lines[0].Start);
        Assert.Equal(3L + 7 + 2, lines[0].End);
        Assert.Equal("{\"a\":1}", Text(lines[0]).TrimEnd('\r'));

        // Blank line: empty Bytes, End after the 0x0A.
        Assert.Equal((12L, 13L), (lines[1].Start, lines[1].End));
        Assert.True(lines[1].Bytes.IsEmpty);

        // Whitespace-only CRLF line: its End counts the CR and the LF, and it holds nothing but whitespace.
        Assert.Equal((13L, 18L), (lines[2].Start, lines[2].End));
        Assert.True(string.IsNullOrWhiteSpace(Text(lines[2])));

        Assert.Equal((18L, 26L, "{\"b\":2}"), (lines[3].Start, lines[3].End, Text(lines[3])));
        Assert.All(lines, l => Assert.False(l.TooLong));
    }

    [Fact]
    public void Reader_BomOnlyOnFirstLineOfTheFile()
    {
        // A BOM-looking prefix on a later line is data; reading from offset 3 of a BOM file starts mid-file and strips nothing.
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var data = bom.Concat(B("a\n")).Concat(bom).Concat(B("b\n")).ToArray();
        var lines = ReadAll(data);
        Assert.Equal(2, lines.Count);
        Assert.Equal("a", Text(lines[0]));
        Assert.Equal(4, lines[1].Bytes.Length);
        var fromMid = ReadAll(data, start: 5);
        Assert.Single(fromMid);
        Assert.Equal(4, fromMid[0].Bytes.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(70000)]
    [InlineData(200000)]
    public void Reader_BufferBoundaries_DoNotSplitLines(int lineLength)
    {
        var sb = new StringBuilder();
        var expected = new List<(long, long, int)>();
        long pos = 0;
        for (var i = 0; i < 5; i++)
        {
            var len = lineLength + i;
            sb.Append((char)('a' + i), len).Append('\n');
            expected.Add((pos, pos + len + 1, len));
            pos += len + 1;
        }
        var lines = ReadAll(B(sb.ToString()));
        Assert.Equal(expected, lines.Select(l => (l.Start, l.End, l.Bytes.Length)).ToList());
        for (var i = 0; i < 5; i++)
            Assert.True(lines[i].Bytes.Span.ToArray().All(b => b == (byte)('a' + i)));
    }

    [Fact]
    public void Reader_NewlineExactlyAtBufferEnd_AndOverLimitAcrossBuffers()
    {
        // Lines sized so that a 0x0A lands on and just past typical buffer ends; and an over-limit line spanning buffers.
        var data = B(new string('a', 4095) + "\n" + new string('b', 4096) + "\n" + new string('c', 300000) + "\nz\n");
        var lines = ReadAll(data, limit: 100000);
        Assert.Equal(4, lines.Count);
        Assert.Equal(4095, lines[0].Bytes.Length);
        Assert.Equal(4096, lines[1].Bytes.Length);
        Assert.True(lines[2].TooLong);
        Assert.Equal(4096L + 4097 + 300001, lines[2].End);
        Assert.Equal("z", Text(lines[3]));
    }

    [Fact]
    public void Reader_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var ms = new MemoryStream(B("a\nb\n"));
        Assert.Throws<OperationCanceledException>(() =>
            JsonlLineReader.Read(ms, 0, Limit, cts.Token).ToList());
    }

    [Fact]
    public void Reader_StartBeyondTheEnd_YieldsNothing()
    {
        Assert.Empty(ReadAll(B("a\n"), start: 50));
    }
}
