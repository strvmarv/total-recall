using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>The branch-name rule of the settings never accepts a name git itself refuses.</summary>
public sealed class BranchRuleTests
{
    private static readonly string[] Names =
    [
        "main", "dev", "a", "A1", "a/b", "a.b", "a-b", "a_b", "x.y.z", "feature/a-b.c", "HEAD", "head", "a/HEAD", "HEAD/a",
        "HEADS", "a..b", "a//b", "a/", "a.", ".a", "a.lock", "a/.b", "a.lock/b", "-a", "a b", "@", "a@b", "a@{b", "a~1",
        "a^", "a:b", "a?", "a*", "a[", "a\\b", "a/b/c/d", "0", "9a", "a.b/c.d", "_a", "a/-b", "feature/x.lock", "x.lock",
        "refs/heads/main", "a/b.", "a/b/", "a,b", "a;b", "a'b", "a\"b", "\u00e9", "a\tb", "a\nb",
        new string('a', 100), new string('a', 101), "a/" + new string('b', 97), "1.0", "v1.0.0", "release/1.x", "..", ".", "/", "@{",
    ];

    [GitFact]
    public void EveryNameTheRuleAcceptsIsAcceptedByGit()
    {
        using var env = new GitTestEnvironment();
        var accepted = 0;
        foreach (var name in Names)
        {
            if (!TotalRecall.Core.Config.GitSyncSettings.isUsableBranch(name))
                continue;
            accepted++;
            var (code, _, error) = env.TryRunGit(env.Root, "check-ref-format", "--branch", name);
            Assert.True(code == 0, $"git refuses the branch name '{name}': {error}");
        }
        Assert.True(accepted > 20, $"only {accepted} names were accepted by the rule: the table is not exercising it");
    }

    [Fact]
    public void HeadIsRefused() => Assert.False(TotalRecall.Core.Config.GitSyncSettings.isUsableBranch("HEAD"));
}
