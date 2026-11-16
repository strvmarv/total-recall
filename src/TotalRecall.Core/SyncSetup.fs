/// The pure rules of the git sync setup commands (milestone 1a-3): the refusal names, the storage-mode verdict,
/// which addresses are refused and how an address is printed, whether a joined repository is the one this machine
/// already knew, the ids a new repository and machine get, how to read an `ls-remote` listing, and how `sync
/// status` is laid out. Reading the database, the config and the clone, and every git call, are in
/// `TotalRecall.Infrastructure.GitSync`.
module TotalRecall.Core.SyncSetup

open System
open System.Text

// ---------------------------------------------------------------------------
// refusals

/// Why a setup command stopped. `refusalName` gives the stable text milestone 4 maps to a code.
type Refusal =
    | NoGit
    | GitTooOld
    | WrongMode
    | BadRemote
    | BadBranch
    | PublicRepo
    | RepoExists
    | NotEmpty
    | NothingToJoin
    | NotASyncRepo
    | InvalidManifest
    | FormatTooNew
    | HostUnverified
    | Busy
    | NeedsAuth
    | Unreachable
    | Failed

let refusalName (r: Refusal) : string =
    match r with
    | NoGit -> "no-git"
    | GitTooOld -> "git-too-old"
    | WrongMode -> "wrong-mode"
    | BadRemote -> "bad-remote"
    | BadBranch -> "bad-branch"
    | PublicRepo -> "public-repo"
    | RepoExists -> "repo-exists"
    | NotEmpty -> "not-empty"
    | NothingToJoin -> "nothing-to-join"
    | NotASyncRepo -> "not-a-sync-repo"
    | InvalidManifest -> "invalid-manifest"
    | FormatTooNew -> "format-too-new"
    | HostUnverified -> "host-unverified"
    | Busy -> "busy"
    | NeedsAuth -> "needs-auth"
    | Unreachable -> "unreachable"
    | Failed -> "failed"

// ---------------------------------------------------------------------------
// storage mode

/// The mode the server would run in: the configured mode, else `postgres` when only a connection string is set,
/// else `local`. The text is kept as written (no case folding).
let effectiveMode (mode: string option) (connectionString: string option) : string =
    match mode with
    | Some m when not (String.IsNullOrWhiteSpace m) -> m
    | _ ->
        match connectionString with
        | Some c when not (String.IsNullOrWhiteSpace c) -> "postgres"
        | _ -> "local"

/// `Ok` only for exactly `local` or `git`; anything else (`cortex`, `postgres`, another text, another letter case)
/// is an error that names the mode and says what to do.
let modeVerdict (mode: string) : Result<unit, string> =
    if mode = "local" || mode = "git" then Ok()
    else
        Error(
            sprintf
                "storage mode is '%s'; set `storage.mode = \"local\"` in config.toml first, then run this command again (memories already in another store are not moved)"
                mode
        )

// ---------------------------------------------------------------------------
// addresses

type private Shape =
    /// `scheme://rest`: the scheme as written, the authority (up to the first `/`, `?` or `#`) and what follows it.
    | Url of scheme: string * authority: string * tail: string
    /// Anything else: an scp-like address, a path, a drive path.
    | Plain of string

let private parse (address: string) : Shape =
    let a = if isNull address then "" else address
    let sep = a.IndexOf("://", StringComparison.Ordinal)
    let schemeOk =
        sep > 0
        && Char.IsAsciiLetter a.[0]
        && a.Substring(0, sep) |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '+' || c = '-' || c = '.')
    if schemeOk then
        let rest = a.Substring(sep + 3)
        let stop = rest.IndexOfAny [| '/'; '?'; '#' |]
        let authority, tail = if stop < 0 then rest, "" else rest.Substring(0, stop), rest.Substring stop
        Url(a.Substring(0, sep), authority, tail)
    else Plain a

let private noUserInfoOnScheme =
    set [ "http"; "https"; "git"; "file"; "ftp"; "ftps" ]

let private refusesAnyUserInfo (scheme: string) =
    noUserInfoOnScheme.Contains(scheme.ToLowerInvariant())

/// The text before the last `@` of the authority, and the host part after it.
let private splitAuthority (authority: string) : (string * string) option =
    let at = authority.LastIndexOf '@'
    if at < 0 then None else Some(authority.Substring(0, at), authority.Substring(at + 1))

/// The host part of an authority (after the last `@`, or all of it): the text after its last `:` outside
/// `[...]`, or None when there is no such colon. Brackets count only when the host starts with `[`.
let private afterLastColon (authority: string) : string option =
    let host =
        match splitAuthority authority with
        | Some(_, h) -> h
        | None -> authority
    let from = if host.StartsWith "[" then max 0 (host.LastIndexOf ']') else 0
    let colon = host.LastIndexOf ':'
    if colon < from then None else Some(host.Substring(colon + 1))

/// An authority whose host part has, after its last `:` (outside `[...]`), text that is not all digits: it may
/// hold a password that a `?`, `#` or `/` cut off (`https://user:pa?ss@host/x` splits at the `?` into `user:pa`;
/// `ssh://u@v:pa/ss@h/x` into `u@v:pa`). An empty port is accepted.
let private suspectPort (authority: string) : bool =
    match afterLastColon authority with
    | Some p -> p.Length > 0 && not (p |> Seq.forall Char.IsAsciiDigit)
    | None -> false

/// The user-info of an address: for a `scheme://` address the text before the last `@` of its authority (the
/// authority ends at the first `/`, `?` or `#`; a quote or any other character does not end it), for an scp-like
/// address the text before the first `@` when that `@` comes before the first `:`, for a path none. This is not
/// `SyncGit.userInfoOf`, which redacts git's standard error and so scans free text.
let addressUserInfo (address: string) : string option =
    match parse address with
    | Url(_, authority, _) -> splitAuthority authority |> Option.map fst
    | Plain a ->
        let at = a.IndexOf '@'
        let colon = a.IndexOf ':'
        if at >= 0 && colon > at then Some(a.Substring(0, at)) else None

let private passwordText = "an address must not hold a password or token: use an ssh key or a git credential helper"

/// Why an address must not be stored: it carries a credential, a query or a fragment. `None` when it is fine.
let addressRefusal (address: string) : string option =
    match parse address with
    | Plain _ -> None
    | Url(scheme, authority, tail) ->
        match splitAuthority authority with
        | Some(userInfo, _) when refusesAnyUserInfo scheme || userInfo.Contains ':' -> Some passwordText
        | _ ->
            if tail.IndexOfAny [| '?'; '#' |] >= 0 then Some "an address must not contain a query or fragment"
            elif suspectPort authority then Some passwordText
            else None

/// How an address is printed by any command. A user-info that holds a `:`, and any user-info on `http`, `https`,
/// `git`, `file`, `ftp` or `ftps`, is shown as `***` whole (a token can be the user name); a plain ssh login stays
/// as written; `?` and `#` and what follows them are shown as `?***` and `#***`; an authority that may hold a
/// password is shown as `<scheme>://***`; an scp-like address and a path are shown as written.
let displayRemote (address: string) : string =
    match parse address with
    | Plain a -> a
    | Url(scheme, authority, tail) ->
        if suspectPort authority then scheme + "://***"
        else
            let shownAuthority =
                match splitAuthority authority with
                | Some(userInfo, host) when refusesAnyUserInfo scheme || userInfo.Contains ':' -> "***@" + host
                | _ -> authority
            let cut = tail.IndexOfAny [| '?'; '#' |]
            let shownTail = if cut < 0 then tail else tail.Substring(0, cut) + string tail.[cut] + "***"
            scheme + "://" + shownAuthority + shownTail

// ---------------------------------------------------------------------------
// the same repository, ids

/// A joined repository is the one this machine already knew when its manifest `repo_id` equals the stored one and
/// the branch equals the previously configured branch (`None`: the configured branch was refused, so it is
/// different). Anything else, nothing stored included, is a different repository.
let isSameRepository (storedId: string option) (manifestId: string) (previousBranch: string option) (branch: string) : bool =
    match storedId, previousBranch with
    | Some id, Some previous -> String.Equals(id, manifestId, StringComparison.Ordinal) && String.Equals(previous, branch, StringComparison.Ordinal)
    | _ -> false

/// The machine id is kept only with a stored machine id and a stored host equal to the current host; a machine id
/// copied with the data directory to another machine is not kept.
let keepMachineId (storedMachine: string option) (storedHost: string option) (currentHost: string) : bool =
    match storedMachine, storedHost with
    | Some _, Some h -> String.Equals(h, currentHost, StringComparison.Ordinal)
    | _ -> false

let private toHex (bytes: byte[]) : string =
    let sb = StringBuilder(bytes.Length * 2)
    for b in bytes do
        sb.Append(b.ToString "x2") |> ignore
    sb.ToString()

/// 16 random bytes as the 32 lowercase hex digits of a repository id.
let newRepoId (bytes: byte[]) : string =
    if isNull bytes || bytes.Length <> 16 then invalidArg "bytes" "a repository id takes 16 bytes"
    toHex bytes

/// 4 random bytes as the 8 lowercase hex digits of a machine id.
let newMachineId (bytes: byte[]) : string =
    if isNull bytes || bytes.Length <> 4 then invalidArg "bytes" "a machine id takes 4 bytes"
    toHex bytes

// ---------------------------------------------------------------------------
// what a remote holds

type RemoteShape =
    /// Nothing but, at most, the ref `HEAD`.
    | Empty
    /// `refs/heads/<branch>` exists.
    | BranchPresent
    /// Refs exist, but not that branch (another branch, tags, pull refs).
    | OtherRefs

/// Reads an `ls-remote` listing's ref names for the branch to initialise.
let classifyRefs (refs: string list) (branch: string) : RemoteShape =
    let real = refs |> List.filter (fun r -> r <> "HEAD")
    if List.isEmpty real then Empty
    elif List.contains ("refs/heads/" + branch) real then BranchPresent
    else OtherRefs

// ---------------------------------------------------------------------------
// status

type HostCheck =
    | Matches
    | Differs
    | Unavailable
    | NotRecorded
    /// The check could not be made (the database could not be read); carries the reason.
    | HostUnknown of string

/// One fact of `sync status`: read, absent, or not readable (with the reason).
type Fact =
    | Known of string
    | Missing
    | Unknown of string

type StatusFacts =
    { Mode: string
      Git: string
      ConfiguredRemote: Fact
      ConfiguredBranch: Fact
      StoredRemote: Fact
      RepoId: Fact
      MachineId: Fact
      Host: HostCheck
      Join: string
      Clone: string }

/// The lines of `sync status`, one labelled fact each, in a fixed order; addresses go through `displayRemote`.
let renderStatus (facts: StatusFacts) : string list =
    let show (absent: string) (render: string -> string) (f: Fact) =
        match f with
        | Known v -> render v
        | Missing -> absent
        | Unknown r -> sprintf "unknown (%s)" r
    let plain = id
    let host =
        match facts.Host with
        | Matches -> "matches"
        | Differs -> "differs"
        | Unavailable -> "unavailable"
        | NotRecorded -> "not recorded"
        | HostUnknown r -> sprintf "unknown (%s)" r
    [ sprintf "storage mode: %s" facts.Mode
      sprintf "git: %s" facts.Git
      sprintf "configured remote: %s" (show "not set" displayRemote facts.ConfiguredRemote)
      sprintf "configured branch: %s" (show "not set" plain facts.ConfiguredBranch)
      sprintf "recorded remote: %s" (show "not recorded" displayRemote facts.StoredRemote)
      sprintf "repository id: %s" (show "not recorded" plain facts.RepoId)
      sprintf "machine id: %s" (show "not recorded" plain facts.MachineId)
      sprintf "host check: %s" host
      sprintf "join state: %s" facts.Join
      sprintf "clone: %s" facts.Clone
      "last sync: never (this build does not run the sync cycle)" ]
