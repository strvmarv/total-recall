using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>Reads the identity the setup commands store in <c>_meta</c> (written by <c>GitSyncStateStore.WriteIdentity</c>; this class only reads).</summary>
public sealed class GitSyncIdentityStore(IMetaStore meta)
{
    public SyncIdentity.StoredIdentity Read() => new(
        Opt(meta.GetMeta(SyncIdentity.MetaKeys.Remote)),
        Opt(meta.GetMeta(SyncIdentity.MetaKeys.RepoId)),
        Opt(meta.GetMeta(SyncIdentity.MetaKeys.Host)));

    private static FSharpOption<string> Opt(string? value) =>
        string.IsNullOrEmpty(value) ? FSharpOption<string>.None : FSharpOption<string>.Some(value);
}
