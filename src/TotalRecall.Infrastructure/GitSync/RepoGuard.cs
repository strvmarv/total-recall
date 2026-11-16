using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

public enum ManifestReadKind { NoClone, NoManifest, Invalid, Unreadable, Newer, Ok }

/// <summary>What reading the repository's manifest from the local clone found (spec decision 3).</summary>
public sealed record ManifestRead(ManifestReadKind Kind, ParsedManifest? Manifest, int NewerVersion, string? Reason)
{
    internal static ManifestRead NoClone() => new(ManifestReadKind.NoClone, null, 0, null);
    internal static ManifestRead NoManifest() => new(ManifestReadKind.NoManifest, null, 0, "the repository has no manifest.json");
    internal static ManifestRead Invalid(string reason) => new(ManifestReadKind.Invalid, null, 0, reason);
    internal static ManifestRead Unreadable(string reason) => new(ManifestReadKind.Unreadable, null, 0, reason);
}

/// <summary>
/// The identity check: the <see cref="SyncIdentity.JoinState"/>, the manifest outcome and whether the host
/// fingerprint could be verified. A caller reports <c>format-too-new</c> before <c>not-joined</c>.
/// </summary>
public sealed record GuardVerdict(SyncIdentity.JoinState Join, ManifestRead Manifest, bool HostUnverified)
{
    /// <summary>
    /// True only when the state is joined, the host fingerprint was available and the manifest is the expected
    /// <c>v1</c> one or there is no clone yet (the cycle clones, then evaluates again with the fetched manifest).
    /// </summary>
    public bool CanSync =>
        Join.IsJoined && !HostUnverified && Manifest.Kind is ManifestReadKind.NoClone or ManifestReadKind.Ok;
}

public sealed partial class RepoGuard
{
    /// <summary>
    /// Reads <c>manifest.json</c> at <paramref name="revision"/> (default <c>HEAD</c>) of the existing clone. Nothing is
    /// fetched. The cycle runs the guard after its fetch and before <c>reset --hard</c>, when <c>HEAD</c> is still the
    /// old commit, so it passes <c>refs/remotes/origin/&lt;branch&gt;</c>.
    /// </summary>
    public async Task<ManifestRead> ReadManifestAsync(SyncGit.Revision? revision = null, CancellationToken cancellationToken = default)
    {
        if (_client is null) return ManifestRead.NoClone();
        var dotGit = Path.Combine(_client.CloneDirectory, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit)) return ManifestRead.NoClone();

        var listing = await _client.LsTreeRootAsync(revision ?? SyncGit.Revision.Head, cancellationToken).ConfigureAwait(false);
        if (!listing.IsOk) return ManifestRead.Unreadable(Describe("could not list the repository root", listing.Failure));

        var entry = listing.Value.FirstOrDefault(e => e.Path == "manifest.json");
        if (entry is null) return ManifestRead.NoManifest();
        if (entry.Kind != "blob" || (entry.Mode != "100644" && entry.Mode != "100755"))
            return ManifestRead.Invalid("manifest.json is not a regular file");

        var blob = await _client.CatBlobAsync(entry.Sha, SyncManifest.MaxLookBytes, cancellationToken).ConfigureAwait(false);
        if (!blob.IsOk) return ManifestRead.Unreadable(Describe("could not read manifest.json", blob.Failure));

        var parse = ManifestFile.Parse(blob.Value);
        return parse.Kind switch
        {
            ManifestParseKind.Ok => new ManifestRead(ManifestReadKind.Ok, parse.Manifest, 0, null),
            ManifestParseKind.Newer => new ManifestRead(ManifestReadKind.Newer, null, parse.NewerVersion, null),
            _ => ManifestRead.Invalid(parse.Reason ?? "manifest.json is not valid"),
        };
    }

    /// <summary>
    /// Decides whether this machine and data directory are the ones that joined the configured repository (spec decision 4).
    /// Runs no network command. With no remote configured it answers <c>NotConfigured</c> without touching the client.
    /// </summary>
    public async Task<GuardVerdict> EvaluateIdentityAsync(SyncGit.Revision? revision = null, CancellationToken cancellationToken = default)
    {
        var fingerprint = _host.Fingerprint;
        var remote = ConfiguredRemote;
        var manifest = remote is null ? ManifestRead.NoClone() : await ReadManifestAsync(revision, cancellationToken).ConfigureAwait(false);

        var input = new SyncIdentity.IdentityInput(
            Opt(remote),
            new GitSyncIdentityStore(_meta).Read(),
            Opt(fingerprint),
            manifest.Kind == ManifestReadKind.Ok ? Opt(manifest.Manifest!.RepoId) : FSharpOption<string>.None);

        return new GuardVerdict(SyncIdentity.evaluate(input), manifest, HostUnverified: fingerprint is null);
    }

    private static FSharpOption<string> Opt(string? value) =>
        value is null ? FSharpOption<string>.None : FSharpOption<string>.Some(value);

    private static string Describe(string what, GitResult failure) =>
        $"{what}: {failure.Failure?.ToString() ?? "failed"}" + (string.IsNullOrWhiteSpace(failure.StderrText) ? "" : $" ({failure.StderrText.Trim()})");
}
