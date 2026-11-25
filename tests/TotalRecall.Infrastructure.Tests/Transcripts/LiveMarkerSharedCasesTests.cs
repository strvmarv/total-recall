using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The two data files the node writer and the shell wrapper tests also read (hooks/live-marker/): one list of
/// accepted and refused session ids, one table of environments and the data directory each gives. Running the
/// same rows through the engine keeps the three implementations from drifting.
/// </summary>
[Collection("process-environment")]
public sealed class LiveMarkerSharedCasesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("package.json not found above " + AppContext.BaseDirectory);
    }

    private static JsonDocument Load(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "hooks", "live-marker", file)));

    [Fact]
    public void SessionIdCases_AcceptedAndRefused()
    {
        using var doc = Load("session-id-cases.json");
        var accepted = doc.RootElement.GetProperty("accepted").EnumerateArray().Select(e => e.GetString()!).ToList();
        var refused = doc.RootElement.GetProperty("refused").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.NotEmpty(accepted);
        Assert.NotEmpty(refused);

        foreach (var id in accepted) Assert.True(ClaudeCodeAdapter.IsValidId(id), "accepted: " + id);
        foreach (var id in refused) Assert.False(ClaudeCodeAdapter.IsValidId(id), "refused: " + id);
    }

    public static IEnumerable<object[]> DataDirRows()
    {
        // Name only: xunit serialises it; the row is looked up again in the test.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "hooks", "live-marker", "data-dir-cases.json")));
        foreach (var row in doc.RootElement.EnumerateArray())
            yield return new object[] { row.GetProperty("name").GetString()! };
    }

    [Theory]
    [MemberData(nameof(DataDirRows))]
    public void DataDirCases_ThroughGetDataDir(string name)
    {
        using var doc = Load("data-dir-cases.json");
        var row = doc.RootElement.EnumerateArray().Single(r => r.GetProperty("name").GetString() == name);

        var tmp = Path.Combine(Path.GetTempPath(), "tr-datadir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string Expand(string s) => s.Replace("{tmp}", tmp);

        var names = new[] { "TOTAL_RECALL_HOME", "HOME", "USERPROFILE" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            // Variables the row does not mention are unset, as the node side scrubs them.
            foreach (var n in names) Environment.SetEnvironmentVariable(n, null);
            foreach (var kv in row.GetProperty("env").EnumerateObject())
            {
                var value = kv.Value.ValueKind == JsonValueKind.Null ? null : Expand(kv.Value.GetString()!);
                Environment.SetEnvironmentVariable(kv.Name, value);
                if (value == "" && Environment.GetEnvironmentVariable(kv.Name) is null)
                {
                    // The platform cannot hold a set-but-empty variable in-process (Windows deletes it).
                    // Row "HOME set but empty (unusable)" is then asserted only by the node side.
                    return;
                }
            }

            var actual = ConfigLoader.GetDataDir();
            var normalised = Path.IsPathFullyQualified(actual) ? actual : null;   // a relative result is unusable

            var expected = row.GetProperty("dataDir").ValueKind == JsonValueKind.Null ? null : Expand(row.GetProperty("dataDir").GetString()!);
            Assert.Equal(expected is null ? null : Path.GetFullPath(expected), normalised is null ? null : Path.GetFullPath(normalised));
        }
        finally
        {
            foreach (var (n, v) in saved) Environment.SetEnvironmentVariable(n, v);
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }
}
