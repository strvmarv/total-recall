// tests/TotalRecall.Server.Tests/ProvisionNoticeTests.cs
//
// v3.0.4 — unit tests for ProvisionNotice.ReadAndConsume. Covers the happy
// path (valid marker → populated SetupNoticeDto with Event="provisioned" +
// one-time consumption via file deletion), absent file → null, and garbage
// file → null (no throw, file still consumed).

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using System.Text.Json;
using Xunit;

public sealed class ProvisionNoticeTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "total-recall-provision-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ReadAndConsume_ValidMarker_ReturnsPopulatedNotice_AndDeletesFile()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, ProvisionNotice.MarkerFileName);
            File.WriteAllText(path, """
                {"version":"3.0.4","sizeBytes":12582912,"durationMs":4200,"completedAtUnixMs":1700000000000}
                """);

            var notice = ProvisionNotice.ReadAndConsume(dir);

            Assert.NotNull(notice);
            Assert.Equal("provisioned", notice!.Event);
            Assert.Equal("3.0.4", notice.Version);
            Assert.Equal(12582912L, notice.SizeBytes);
            Assert.Equal(4200L, notice.DurationMs);

            // One-time: the file must be deleted, so a second call returns null.
            Assert.False(File.Exists(path));
            Assert.Null(ProvisionNotice.ReadAndConsume(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadAndConsume_AbsentFile_ReturnsNull()
    {
        var dir = NewTempDir();
        try
        {
            Assert.Null(ProvisionNotice.ReadAndConsume(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadAndConsume_GarbageFile_ReturnsNull_NoThrow_AndConsumes()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, ProvisionNotice.MarkerFileName);
            File.WriteAllText(path, "this is not json {{{");

            var notice = ProvisionNotice.ReadAndConsume(dir);

            Assert.Null(notice);
            // A malformed marker is still consumed so it can't wedge every session.
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadAndConsume_NullOrEmptyDir_ReturnsNull()
    {
        Assert.Null(ProvisionNotice.ReadAndConsume(string.Empty));
    }

    [Fact]
    public void ReadAndConsume_NoticeSerializesViaSourceGen_WithExpectedShape()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, ProvisionNotice.MarkerFileName);
            File.WriteAllText(path, """
                {"version":"3.0.4","sizeBytes":5242880,"durationMs":1500,"completedAtUnixMs":1700000000000}
                """);

            var notice = ProvisionNotice.ReadAndConsume(dir);
            Assert.NotNull(notice);

            var json = JsonSerializer.Serialize(notice!, JsonContext.Default.SetupNoticeDto);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("provisioned", root.GetProperty("event").GetString());
            Assert.Equal("3.0.4", root.GetProperty("version").GetString());
            Assert.Equal(5242880L, root.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(1500L, root.GetProperty("durationMs").GetInt64());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
