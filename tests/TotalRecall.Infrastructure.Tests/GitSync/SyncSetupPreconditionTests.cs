using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class SyncSetupPreconditionTests
{
    private const string GoodAddress = "git@host:o/r.git";
    private const string GitHubAddress = "https://github.com/o/r";

    private static readonly string[] CredentialAddresses =
    [
        "https://tok@h/x", "https://u:pw@h/x", "http://u@h/x", "git://u@h/x", "file://tok:pw@h/tmp/x.git",
        "ssh://git:ghp_x@h/o/r", "git+ssh://u:pw@h/x", "https://user:pa?ss@h/x", "https://h/o/r?token=y", "https://h/o/r#frag",
    ];

    private static SyncSetup.Refusal? Check(string address, string? branch, Core.Config.GitSyncConfig? settings, out string text, out string used) =>
        SyncSetupService.CheckAddress(address, branch, settings ?? GuardSettings.For(null), windows: false, out text, out used);

    // ---- the address and the branch -------------------------------------------------------------------------

    [Fact]
    public void Preconditions_RefuseEachCredentialAddress()
    {
        foreach (var address in CredentialAddresses.Append("ext::sh").Append("-x").Append(""))
        {
            var refusal = Check(address, null, null, out var text, out _);
            Assert.Equal(SyncSetup.Refusal.BadRemote, refusal);
            if (address.Length > 0) Assert.DoesNotContain(address, text);
            Assert.DoesNotContain("ghp_x", text);

            using var rig = new SetupRig();
            var result = rig.Service().InitAsync(address, null).GetAwaiter().GetResult();
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(SyncSetup.Refusal.BadRemote, result.Refusal);
            Assert.DoesNotContain("ghp_x", result.RefusalText);
            if (address.Length > 0) Assert.DoesNotContain(address, result.RefusalText);
            rig.AssertNothingCreated();
            Assert.Empty(rig.Config.Writes);
        }
    }

    [Fact]
    public void Preconditions_AcceptTheLegitimateAddresses()
    {
        var addresses = new List<string>
        {
            "git@host:o/r", "ssh://git@host/o/r", "https://host/o/repo@v2.git", "host:team/repo@2.git", "https://[::1]:8080/o/r",
        };
        if (!OperatingSystem.IsWindows())
            addresses.Add("file:///tmp/x");
        foreach (var address in addresses)
            Assert.Null(Check(address, null, null, out _, out _));
    }

    [Fact]
    public void Branch_HeadIsRefused_AnOverrideBeatsABadConfiguredBranch_AndTheConfiguredOneIsUsed()
    {
        Assert.Equal(SyncSetup.Refusal.BadBranch, Check(GoodAddress, "HEAD", null, out _, out _));
        Assert.Equal(SyncSetup.Refusal.BadBranch, Check(GoodAddress, "a..b", null, out _, out _));

        var badConfigured = GuardSettings.For(null, "not a branch!");
        Assert.Equal(SyncSetup.Refusal.BadBranch, Check(GoodAddress, null, badConfigured, out var text, out _));
        Assert.Contains("--branch", text);
        Assert.Null(Check(GoodAddress, "main", badConfigured, out _, out var used));
        Assert.Equal("main", used);

        Assert.Null(Check(GoodAddress, null, GuardSettings.For(null, "dev/sync"), out _, out var configured));
        Assert.Equal("dev/sync", configured);
        Assert.Null(Check(GoodAddress, null, null, out _, out var dflt));
        Assert.Equal("main", dflt);
    }

    // ---- refusals before the lock ---------------------------------------------------------------------------

    [Theory]
    [InlineData("cortex")]
    [InlineData("postgres")]
    [InlineData("GIT")]
    [InlineData("foo")]
    public async Task WrongMode_IsRefusedWithTheModeNamed_AndNothingIsCreated(string mode)
    {
        using var rig = new SetupRig { Mode = mode };
        var result = await rig.Service().InitAsync(GoodAddress, null);

        Assert.Equal(SyncSetup.Refusal.WrongMode, result.Refusal);
        Assert.Contains($"'{mode}'", result.RefusalText);
        Assert.Empty(rig.Config.Writes);
        rig.AssertNothingCreated();
        Assert.Empty(rig.Recorder.Calls);
    }

    [Fact]
    public async Task TheDryRunRenderFailing_IsFailedBeforeAnyGitCall()
    {
        using var rig = new SetupRig();
        rig.Config.RenderFault = _ => new NotSupportedException("the writer cannot write this");

        var result = await rig.Service().InitAsync(GoodAddress, null);

        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("the writer cannot write this", result.RefusalText);
        Assert.Empty(rig.Recorder.Calls);
        rig.AssertNothingCreated();
    }

    [Fact]
    public async Task NoGitOnThePath_IsRefused_BeforeAnyFileIsCreated()
    {
        using var rig = new SetupRig();
        var empty = Path.Combine(rig.Root, "empty-path");
        Directory.CreateDirectory(empty);
        rig.PathVariable = empty;

        var result = await rig.Service().JoinAsync(GoodAddress, null);

        Assert.Equal(SyncSetup.Refusal.NoGit, result.Refusal);
        rig.AssertNothingCreated();
    }

    [GitFact]
    public async Task AnOldGit_IsRefused()
    {
        using var rig = new SetupRig();
        rig.Fault = c => c.IsVersion ? FaultingGitProcess.Text("git version 2.20.0\n") : null;

        var result = await rig.Service().InitAsync(GoodAddress, null);

        Assert.Equal(SyncSetup.Refusal.GitTooOld, result.Refusal);
        rig.AssertNothingCreated();
    }

    [GitFact]
    public async Task AHostThatCannotBeIdentified_IsRefused()
    {
        using var rig = new SetupRig { MachineIdKind = MachineIdKind.Failed };

        var result = await rig.Service().InitAsync(GoodAddress, null);

        Assert.Equal(SyncSetup.Refusal.HostUnverified, result.Refusal);
        rig.AssertNothingCreated();
    }

    [Fact]
    public async Task ALoadError_IsReportedAsFailed()
    {
        using var rig = new SetupRig { LoadError = "config.toml is malformed" };
        var result = await rig.Service().InitAsync(GoodAddress, null);
        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("config.toml is malformed", result.RefusalText);
        rig.AssertNothingCreated();
    }

    // ---- the lock and the database ----------------------------------------------------------------------------

    [GitFact]
    public async Task Preconditions_Busy_CreatesNoDatabase()
    {
        using var rig = new SetupRig();
        var paths = rig.PathsFor(GoodAddress);
        paths.EnsureCreated();
        using var held = await SyncLock.TryAcquireAsync(paths.LockPath, TimeSpan.Zero);
        Assert.NotNull(held);

        var result = await rig.Service().InitAsync(GoodAddress, null);

        Assert.Equal(SyncSetup.Refusal.Busy, result.Refusal);
        Assert.False(File.Exists(rig.DbPath), "the database was created before the lock");
        Assert.Empty(rig.Config.Writes);
    }

    [GitFact]
    public async Task AnOldFormatDatabase_IsRefusedAndNotChanged()
    {
        using var rig = new SetupRig();
        SyncDb.CreatePlain(rig.DbPath, "CREATE TABLE hot_memories (id TEXT)", "INSERT INTO hot_memories VALUES ('x')");
        var before = SyncDb.Snapshot(Path.GetDirectoryName(rig.DbPath)!);

        var result = await rig.Service().InitAsync(GoodAddress, null);

        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("has not been migrated yet", result.RefusalText);
        var after = SyncDb.Snapshot(Path.GetDirectoryName(rig.DbPath)!);
        Assert.Equal(before[Path.GetFileName(rig.DbPath)], after[Path.GetFileName(rig.DbPath)]);
        Assert.False(File.Exists(rig.DbPath + "-wal"));
    }

    [GitFact]
    public async Task APublicRepository_IsRefused_BeforeAnyNetworkGitCommand()
    {
        using var rig = new SetupRig { Probe = new StubProbe(() => 200) };

        var result = await rig.Service().InitAsync(GitHubAddress, null);

        Assert.Equal(SyncSetup.Refusal.PublicRepo, result.Refusal);
        Assert.All(rig.Recorder.Calls, c => Assert.True(c.Command.IsVersion, $"{c.Command} ran"));
        Assert.Empty(rig.Config.Writes);
    }

    [GitFact]
    public async Task APrivateAnswer_LetsItThrough_AndIsCached()
    {
        using var rig = new SetupRig { Probe = new StubProbe(() => 404) };
        var (prepared, refusal) = await rig.Service().PrepareForTestsAsync(GitHubAddress, null, default);
        Assert.Null(refusal);
        using (prepared!)
        {
            Assert.NotNull(prepared!.Meta.GetMeta(SyncIdentity.MetaKeys.PublicCheck));
            Assert.Equal("main", prepared.Branch);
            Assert.False(FSharpOption<string>.get_IsSome(prepared.Stored.Remote));
        }
        Assert.Empty(rig.Reported);
    }

    [GitFact]
    public async Task NoAnswer_LetsItThrough_WithOneNotice()
    {
        using var rig = new SetupRig { Probe = new StubProbe(() => null) };
        var (prepared, refusal) = await rig.Service().PrepareForTestsAsync(GitHubAddress, null, default);
        Assert.Null(refusal);
        prepared!.Dispose();
        Assert.Single(rig.Reported);
    }

    [GitFact]
    public async Task AnUnexpectedException_IsFailedNamingThePhase()
    {
        using var rig = new SetupRig { Probe = new StubProbe(() => throw new InvalidOperationException("the probe broke")) };

        var result = await rig.Service().InitAsync(GitHubAddress, null);

        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("checking whether the repository is public", result.RefusalText);
        Assert.Contains("the probe broke", result.RefusalText);
    }

    [GitFact]
    public async Task AGitThatThrows_IsFailedNamingThePhase()
    {
        using var rig = new SetupRig();
        rig.Before = c =>
        {
            if (c.IsVersion)
                throw new IOException("cannot run git");
        };

        var result = await rig.Service().InitAsync(GoodAddress, null);

        Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
        Assert.Contains("looking for git", result.RefusalText);
        rig.AssertNothingCreated();
    }

    [GitFact]
    public async Task Preconditions_ReleaseTheLockAndConnectionOnEveryExit()
    {
        // each way out after the lock is taken: a garbage database (the probe throws), the old format (refused),
        // a public repository (refused after the database was opened), an exception from the probe
        async Task AssertReleased(SetupRig rig, string address)
        {
            var paths = rig.PathsFor(address);
            using (var again = await SyncLock.TryAcquireAsync(paths.LockPath, TimeSpan.Zero))
                Assert.NotNull(again); // the lock is free in this process at once
            Assert.False(File.Exists(rig.DbPath + "-wal"), "a connection is still open");
            Assert.False(File.Exists(rig.DbPath + "-shm"), "a connection is still open");
        }

        using (var rig = new SetupRig())
        {
            File.WriteAllText(rig.DbPath, string.Concat(Enumerable.Repeat("not sqlite. ", 200)));
            Assert.Equal(SyncSetup.Refusal.Failed, (await rig.Service().InitAsync(GoodAddress, null)).Refusal);
            await AssertReleased(rig, GoodAddress);
        }
        using (var rig = new SetupRig())
        {
            SyncDb.CreatePlain(rig.DbPath, "CREATE TABLE hot_memories (id TEXT)");
            Assert.Equal(SyncSetup.Refusal.Failed, (await rig.Service().InitAsync(GoodAddress, null)).Refusal);
            await AssertReleased(rig, GoodAddress);
        }
        using (var rig = new SetupRig { Probe = new StubProbe(() => 200) })
        {
            Assert.Equal(SyncSetup.Refusal.PublicRepo, (await rig.Service().InitAsync(GitHubAddress, null)).Refusal);
            await AssertReleased(rig, GitHubAddress);
        }
        using (var rig = new SetupRig { Probe = new StubProbe(() => throw new InvalidOperationException("boom")) })
        {
            Assert.Equal(SyncSetup.Refusal.Failed, (await rig.Service().InitAsync(GitHubAddress, null)).Refusal);
            await AssertReleased(rig, GitHubAddress);
        }
    }
}

/// <summary>File locking switched off makes the lock throw; the command must answer failed, never let the exception out.</summary>
[Collection("process-environment")]
public sealed class SyncSetupNoFileLockingTests
{
    [GitFact]
    public async Task FileLockingDisabled_IsFailed_NotAnUnhandledException()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", "1");
        try
        {
            using var rig = new SetupRig();
            var result = await rig.Service().InitAsync("git@host:o/r.git", null);
            Assert.Equal(SyncSetup.Refusal.Failed, result.Refusal);
            Assert.Contains("taking the sync lock", result.RefusalText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", previous);
        }
    }
}
