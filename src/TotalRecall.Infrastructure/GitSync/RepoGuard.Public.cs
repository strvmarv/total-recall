using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>The answer of the public-repository check. <c>Notice</c> is text for the caller to show, once per guard; the guard never prints.</summary>
public sealed record PublicCheckResult(SyncPublic.PublicVerdict Verdict, string? Notice, bool FromCache);

/// <summary>
/// The checks that say whether a repository and a data directory may be synced: the identity half
/// (<see cref="EvaluateIdentityAsync"/>) and the public-repository half (<see cref="CheckPublicAsync"/>). Nothing here
/// fetches, clones or writes to the repository. The settings are fixed at construction (the running server keeps the
/// configuration it loaded).
/// </summary>
public sealed partial class RepoGuard
{
    private readonly Core.Config.GitSyncConfig _settings;
    private readonly IMetaStore _meta;
    private readonly GitClient? _client;
    private readonly HostIdentity _host;
    private readonly IPublicRepoProbe _probe;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _noticeLock = new();
    private bool _skippedNoticed;
    private bool _unknownNoticed;

    public RepoGuard(
        Core.Config.GitSyncConfig settings,
        IMetaStore meta,
        GitClient? client,
        HostIdentity host,
        IPublicRepoProbe probe,
        Func<DateTimeOffset> clock)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _client = client;
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    private string? ConfiguredRemote =>
        FSharpOption<string>.get_IsSome(_settings.Remote) ? _settings.Remote.Value : null;

    /// <summary>
    /// Asks GitHub, without credentials, whether the configured remote is public (spec decisions 7 to 9). There is no
    /// memo of the verdict: every call goes through the 24-hour cache of private answers and, on a miss, the probe.
    /// Only the notices are once per guard.
    /// </summary>
    public async Task<PublicCheckResult> CheckPublicAsync(CancellationToken cancellationToken = default)
    {
        var remote = ConfiguredRemote;
        if (remote is null)
            return Skipped("no remote is configured");

        var parsed = SyncPublic.parseGitHubSlug(remote);
        if (!FSharpOption<SyncPublic.GitHubSlug>.get_IsSome(parsed))
            return Skipped("the remote is not a github.com address");
        var slug = parsed.Value;

        var nowUnix = _clock().ToUnixTimeSeconds();
        var cached = SyncPublic.parseCache(_meta.GetMeta(SyncIdentity.MetaKeys.PublicCheck) ?? "");
        if (FSharpOption<Tuple<string, long>>.get_IsSome(cached)
            && cached.Value.Item1 == SyncPublic.slugKey(slug)
            && SyncPublic.isFresh(nowUnix, cached.Value.Item2))
        {
            return new PublicCheckResult(SyncPublic.PublicVerdict.Private, null, FromCache: true);
        }

        var status = await _probe.GetStatusAsync(slug.Owner, slug.Repo, cancellationToken).ConfigureAwait(false);
        var verdict = SyncPublic.ofStatus(status is { } code ? FSharpOption<int>.Some(code) : FSharpOption<int>.None);

        if (verdict.IsPrivate)
        {
            _meta.SetMeta(SyncIdentity.MetaKeys.PublicCheck, SyncPublic.formatCache(slug, nowUnix));
            return new PublicCheckResult(verdict, null, FromCache: false);
        }

        if (verdict is SyncPublic.PublicVerdict.Unknown unknown)
            return new PublicCheckResult(verdict, Once(ref _unknownNoticed, SyncPublic.unknownNotice(unknown.Item)), FromCache: false);

        return new PublicCheckResult(verdict, null, FromCache: false); // a public answer is never cached
    }

    private PublicCheckResult Skipped(string reason) =>
        new(SyncPublic.PublicVerdict.NewSkipped(reason), Once(ref _skippedNoticed, SyncPublic.skippedNotice(reason)), FromCache: false);

    private string? Once(ref bool flag, string notice)
    {
        lock (_noticeLock)
        {
            if (flag) return null;
            flag = true;
            return notice;
        }
    }
}
