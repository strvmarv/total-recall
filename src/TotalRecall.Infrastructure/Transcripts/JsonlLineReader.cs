using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// One complete line of a JSONL file. <see cref="Start"/> and <see cref="End"/> are byte offsets; <see cref="End"/> is the
/// offset after the <c>0x0A</c>. <see cref="Bytes"/> exclude the <c>0x0A</c> (a CR stays) and a BOM on the file's first line,
/// and are empty when <see cref="TooLong"/>.
/// </summary>
internal readonly record struct JsonlLine(long Start, long End, ReadOnlyMemory<byte> Bytes, bool TooLong);

/// <summary>
/// Byte-exact line splitter for the archive's watermark: lines are split on <c>0x0A</c> only, offsets count bytes
/// (CRLF, BOM, blank lines and discarded over-limit lines included), and a trailing partial line is never yielded.
/// </summary>
internal static class JsonlLineReader
{
    private const int BufferSize = 64 * 1024;

    public static IEnumerable<JsonlLine> Read(Stream stream, long startOffset, int maxLineBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 1);
        return ReadCore(stream, startOffset, maxLineBytes, ct);
    }

    private static IEnumerable<JsonlLine> ReadCore(Stream stream, long startOffset, int maxLineBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        stream.Seek(startOffset, SeekOrigin.Begin);

        var buffer = new byte[BufferSize];
        var pending = new byte[Math.Min(4096, maxLineBytes)];
        var pendingLen = 0;
        var discarding = false;          // inside an over-limit line: count its bytes, keep none
        long lineStart = startOffset;
        long lineLen = 0;                // bytes of the current line so far, excluding the 0x0A
        long position = startOffset;     // offset of the next byte to read

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) yield break;

            var i = 0;
            while (i < read)
            {
                var nl = Array.IndexOf(buffer, (byte)0x0A, i, read - i);
                var chunkEnd = nl < 0 ? read : nl;
                var chunk = chunkEnd - i;

                if (chunk > 0)
                {
                    lineLen += chunk;
                    if (!discarding)
                    {
                        if (lineLen > maxLineBytes)
                        {
                            discarding = true;
                            pendingLen = 0;
                        }
                        else
                        {
                            if (pendingLen + chunk > pending.Length)
                                Array.Resize(ref pending, (int)Math.Min(maxLineBytes, Math.Max((long)pending.Length * 2, pendingLen + chunk)));
                            Buffer.BlockCopy(buffer, i, pending, pendingLen, chunk);
                            pendingLen += chunk;
                        }
                    }
                }

                if (nl < 0) break;

                var end = position + nl + 1;
                if (discarding)
                    yield return new JsonlLine(lineStart, end, ReadOnlyMemory<byte>.Empty, TooLong: true);
                else
                {
                    var skip = lineStart == 0 && HasBom(pending, pendingLen) ? 3 : 0;
                    var bytes = new byte[pendingLen - skip];
                    Buffer.BlockCopy(pending, skip, bytes, 0, bytes.Length);
                    yield return new JsonlLine(lineStart, end, bytes, TooLong: false);
                }

                lineStart = end;
                lineLen = 0;
                pendingLen = 0;
                discarding = false;
                i = nl + 1;
            }

            position += read;
        }
    }

    private static bool HasBom(byte[] data, int length) =>
        length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
}
