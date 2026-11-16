/// The identity rule of git sync (milestone 1a-2): whether this machine and data directory are the ones that
/// joined the configured repository. Pure: reading `_meta`, the host and the repository's manifest is in
/// `TotalRecall.Infrastructure.GitSync`.
module TotalRecall.Core.SyncIdentity

open System
open System.Security.Cryptography
open System.Text

/// The `_meta` keys of the sync identity (the verbs of milestone 1a-3 write them; the guard reads them).
module MetaKeys =
    [<Literal>]
    let Remote = "git_sync_remote"
    [<Literal>]
    let RepoId = "git_sync_repo_id"
    [<Literal>]
    let Machine = "git_sync_machine"
    [<Literal>]
    let Host = "git_sync_host"
    [<Literal>]
    let PublicCheck = "git_sync_public_check"
    /// The commit this machine last synced (written by the sync cycle, milestone 1b).
    [<Literal>]
    let LastSha = "git_sync_last_sha"
    [<Literal>]
    let LastOk = "git_sync_last_ok"
    [<Literal>]
    let LastError = "git_sync_last_error"
    [<Literal>]
    let Arrivals = "git_sync_arrivals"

/// What `_meta` holds about the repository this machine joined; an empty value is `None`.
type StoredIdentity =
    { Remote: string option
      RepoId: string option
      Host: string option }

/// Everything the rule looks at. `CurrentHost = None` means the host fingerprint is unavailable, which skips the
/// host check; `ManifestRepoId = None` means the repository's `repo_id` is not known yet, which skips that check.
type IdentityInput =
    { ConfiguredRemote: string option
      Stored: StoredIdentity
      CurrentHost: string option
      ManifestRepoId: string option }

type NotJoinedReason =
    | NotConfigured
    | NeverJoined
    | RemoteChanged
    | DataDirectoryMoved
    | RepositoryReplaced

type JoinState =
    | Joined
    | NotJoined of NotJoinedReason

/// The first check that fails decides, so the reason is stable: no usable remote; nothing joined (no stored remote
/// or no stored repo id); a configured remote that is not the stored text (ordinal); a host fingerprint that is
/// absent or different; a known manifest `repo_id` that differs from the stored one. Total.
let evaluate (input: IdentityInput) : JoinState =
    match input.ConfiguredRemote with
    | None -> NotJoined NotConfigured
    | Some configured ->
        match input.Stored.Remote, input.Stored.RepoId with
        | None, _
        | _, None -> NotJoined NeverJoined
        | Some storedRemote, Some storedRepoId ->
            if not (String.Equals(configured, storedRemote, StringComparison.Ordinal)) then NotJoined RemoteChanged
            else
                let hostMoved =
                    match input.CurrentHost with
                    | None -> false
                    | Some current ->
                        match input.Stored.Host with
                        | None -> true
                        | Some stored -> not (String.Equals(current, stored, StringComparison.Ordinal))
                if hostMoved then NotJoined DataDirectoryMoved
                else
                    match input.ManifestRepoId with
                    | Some manifestId when not (String.Equals(manifestId, storedRepoId, StringComparison.Ordinal)) ->
                        NotJoined RepositoryReplaced
                    | _ -> Joined

/// The wording of each reason, for `session_start`, `status` and the verbs.
let reasonText (reason: NotJoinedReason) : string =
    match reason with
    | NotConfigured -> "git sync has no [git_sync] remote configured"
    | NeverJoined -> "this machine has not joined a sync repository: run `total-recall sync init` or `sync join`"
    | RemoteChanged -> "[git_sync] remote differs from the repository this machine joined: run `total-recall sync join`"
    | DataDirectoryMoved -> "this data directory moved or was copied: run `total-recall sync join`"
    | RepositoryReplaced -> "the sync repository was recreated (a different repo id): run `total-recall sync join`"

/// `SHA-256("total-recall/host/v1\n" + osMachineId + "\n" + canonicalPath)`, the first 32 lowercase hex digits.
/// `osMachineId` is the operating system's machine id, or the empty text when there is none. Never the hostname.
let hostFingerprint (osMachineId: string) (canonicalPath: string) : string =
    let text = "total-recall/host/v1\n" + osMachineId + "\n" + canonicalPath
    use sha = SHA256.Create()
    let hash = sha.ComputeHash(UTF8Encoding(false).GetBytes text)
    Convert.ToHexString(hash).Substring(0, 32).ToLowerInvariant()
