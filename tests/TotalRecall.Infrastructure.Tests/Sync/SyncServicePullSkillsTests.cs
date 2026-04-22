using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

public sealed class SyncServicePullSkillsTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection OpenAndMigrate()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    private static PluginSyncSkillDto MakeSkill(Guid id, bool isOrphaned = false) =>
        new PluginSyncSkillDto(
            Id: id,
            Name: "Test Skill",
            Description: "A test skill",
            Content: "skill content",
            Scope: "user",
            ScopeId: "user-1",
            Tags: Array.Empty<string>(),
            Source: null,
            IsOrphaned: isOrphaned,
            Version: 1,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

    // -----------------------------------------------------------------------
    // Test 1: Active (non-orphaned) skill is upserted to cache
    // -----------------------------------------------------------------------
    [Fact]
    public async Task PullSkillsAsync_ActiveSkill_IsUpsertedToCache()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();
        var skillCache = Substitute.For<ISkillCache>();

        var skillId = Guid.NewGuid();
        var skill = MakeSkill(skillId, isOrphaned: false);

        remote.GetSkillsModifiedSinceAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { skill });

        var svc = new SyncService(local, remote, syncQueue, conn, skillCache);
        await svc.PullSkillsAsync(CancellationToken.None);

        await skillCache.Received(1).UpsertAsync(
            Arg.Is<PluginSyncSkillDto>(s => s.Id == skillId),
            Arg.Any<CancellationToken>());
        await skillCache.DidNotReceive().RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Test 2: Orphaned skill is removed from cache
    // -----------------------------------------------------------------------
    [Fact]
    public async Task PullSkillsAsync_OrphanedSkill_IsRemovedFromCache()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();
        var skillCache = Substitute.For<ISkillCache>();

        var skillId = Guid.NewGuid();
        var skill = MakeSkill(skillId, isOrphaned: true);

        remote.GetSkillsModifiedSinceAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { skill });

        var svc = new SyncService(local, remote, syncQueue, conn, skillCache);
        await svc.PullSkillsAsync(CancellationToken.None);

        await skillCache.Received(1).RemoveAsync(skillId, Arg.Any<CancellationToken>());
        await skillCache.DidNotReceive().UpsertAsync(Arg.Any<PluginSyncSkillDto>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Test 3: Watermark is written after a successful pull
    // -----------------------------------------------------------------------
    [Fact]
    public async Task PullSkillsAsync_WritesWatermarkAfterPull()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();
        var skillCache = Substitute.For<ISkillCache>();

        remote.GetSkillsModifiedSinceAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PluginSyncSkillDto>());

        var svc = new SyncService(local, remote, syncQueue, conn, skillCache);

        // Watermark should be unset (MinValue) before the call
        var before = svc.GetWatermark("cortex_skills_last_pull_at");
        Assert.Equal(DateTimeOffset.MinValue, before);

        var callTime = DateTimeOffset.UtcNow;
        await svc.PullSkillsAsync(CancellationToken.None);

        // Watermark should be set to a value >= callTime
        var after = svc.GetWatermark("cortex_skills_last_pull_at");
        Assert.True(after >= callTime, $"Expected watermark >= {callTime} but got {after}");
    }

    // -----------------------------------------------------------------------
    // Test 4: CortexUnreachableException causes early return (no watermark update)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task PullSkillsAsync_OnUnreachable_ReturnsEarlyWithoutUpdatingWatermark()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();
        var skillCache = Substitute.For<ISkillCache>();

        remote.GetSkillsModifiedSinceAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Throws(new CortexUnreachableException("network error"));

        var svc = new SyncService(local, remote, syncQueue, conn, skillCache);
        await svc.PullSkillsAsync(CancellationToken.None); // should not throw

        // Watermark should remain unset
        var after = svc.GetWatermark("cortex_skills_last_pull_at");
        Assert.Equal(DateTimeOffset.MinValue, after);

        // Cache should not be touched
        await skillCache.DidNotReceive().UpsertAsync(Arg.Any<PluginSyncSkillDto>(), Arg.Any<CancellationToken>());
        await skillCache.DidNotReceive().RemoveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
