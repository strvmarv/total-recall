using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class RepoGuardPublicTests : IDisposable
{
    private readonly GuardStore _store = new();
    private DateTimeOffset _now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _store.Dispose();

    private RepoGuard Guard(string? remote, StubProbe probe) =>
        new(GuardSettings.For(remote), _store.Store, client: null,
            new HostIdentity(() => new MachineIdResult(MachineIdKind.Absent, null), "/tmp/db", false), probe, () => _now);

    private const string Remote = "https://github.com/o/r";

    [Fact]
    public async Task A200_IsPublic_AndNeverCached()
    {
        var probe = new StubProbe(() => 200);
        var guard = Guard(Remote, probe);

        var result = await guard.CheckPublicAsync();

        Assert.True(result.Verdict.IsPublic);
        Assert.Null(_store.Store.GetMeta(SyncIdentity.MetaKeys.PublicCheck));
        await guard.CheckPublicAsync();
        Assert.Equal(2, probe.Calls); // no verdict memo
    }

    [Fact]
    public async Task A404_IsPrivate_AndTheCacheKeyIsWritten()
    {
        var probe = new StubProbe(() => 404);

        var result = await Guard(Remote, probe).CheckPublicAsync();

        Assert.True(result.Verdict.IsPrivate);
        Assert.False(result.FromCache);
        var slug = SyncPublic.parseGitHubSlug(Remote).Value;
        Assert.Equal(SyncPublic.formatCache(slug, _now.ToUnixTimeSeconds()), _store.Store.GetMeta(SyncIdentity.MetaKeys.PublicCheck));
        Assert.Equal(("o", "r"), probe.Asked.Single());
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(null)]
    public async Task AnyOtherAnswer_IsUnknown_WithOneNoticePerGuard_AndIsNotCached(int? status)
    {
        var probe = new StubProbe(() => status);
        var guard = Guard(Remote, probe);

        var first = await guard.CheckPublicAsync();
        var second = await guard.CheckPublicAsync();

        Assert.True(first.Verdict.IsUnknown);
        Assert.NotNull(first.Notice);
        Assert.Null(second.Notice);
        Assert.Equal(2, probe.Calls);
        Assert.Null(_store.Store.GetMeta(SyncIdentity.MetaKeys.PublicCheck));
    }

    [Theory]
    [InlineData("git@work-github:o/r.git")]
    [InlineData("https://gitlab.com/o/r")]
    [InlineData("/srv/git/r.git")]
    public async Task ANonGitHubRemote_IsSkipped_WithOneNotice_AndNoRequest(string remote)
    {
        var probe = new StubProbe(() => 200);
        var guard = Guard(remote, probe);

        var first = await guard.CheckPublicAsync();
        var second = await guard.CheckPublicAsync();

        Assert.True(first.Verdict.IsSkipped);
        Assert.Contains("private", first.Notice);
        Assert.Null(second.Notice);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task NoRemote_IsSkipped_WithoutARequest()
    {
        var probe = new StubProbe(() => 200);

        var result = await Guard(null, probe).CheckPublicAsync();

        Assert.True(result.Verdict.IsSkipped);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task AUserInfoToken_IsNeverAsked_AndAppearsInNoResult()
    {
        var probe = new StubProbe(() => 403);

        var result = await Guard("https://user:tok3n12345@github.com/o/r", probe).CheckPublicAsync();

        Assert.Equal(("o", "r"), probe.Asked.Single());
        Assert.DoesNotContain("tok3n12345", result.Notice);
        Assert.DoesNotContain("tok3n12345", result.Verdict.ToString());
    }

    [Fact]
    public async Task APrivateAnswerLessThan24HoursOld_IsUsed_WithNoRequest()
    {
        var probe = new StubProbe(() => 404);
        await Guard(Remote, probe).CheckPublicAsync();
        _now = _now.AddHours(23).AddMinutes(59);

        var again = await Guard(Remote, probe).CheckPublicAsync();

        Assert.True(again.FromCache);
        Assert.True(again.Verdict.IsPrivate);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task AnAnswerOlderThan24Hours_AsksAgain()
    {
        var probe = new StubProbe(() => 404);
        await Guard(Remote, probe).CheckPublicAsync();
        _now = _now.AddHours(24).AddSeconds(1);

        await Guard(Remote, probe).CheckPublicAsync();

        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task ACacheForAnotherRepository_AsksAgain()
    {
        var probe = new StubProbe(() => 404);
        await Guard("https://github.com/o/other", probe).CheckPublicAsync();

        await Guard(Remote, probe).CheckPublicAsync();

        Assert.Equal(2, probe.Calls);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(6, false)]
    public async Task ACacheStampedInTheFuture_IsUsedWithinFiveMinutesOnly(int minutesAhead, bool used)
    {
        var probe = new StubProbe(() => 404);
        var slug = SyncPublic.parseGitHubSlug(Remote).Value;
        _store.Store.SetMeta(SyncIdentity.MetaKeys.PublicCheck, SyncPublic.formatCache(slug, _now.AddMinutes(minutesAhead).ToUnixTimeSeconds()));

        var result = await Guard(Remote, probe).CheckPublicAsync();

        Assert.Equal(used, result.FromCache);
        Assert.Equal(used ? 0 : 1, probe.Calls);
    }
}
