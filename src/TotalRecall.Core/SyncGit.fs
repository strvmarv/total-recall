/// The pure side of the git client of git sync (milestone 1a-1): the closed set of `git` commands, the checks on
/// the values they carry, the argument lists, the configuration overrides and the environment rule. Nothing here
/// starts a process or reads the file system. No case of `GitCommand` can produce a force, delete or mirror option:
/// that is a property of this type, checked by a test over every case.
module TotalRecall.Core.SyncGit

open TotalRecall.Core.Config

// ---------------------------------------------------------------------------
// the closed command set

/// Which configuration file `ConfigGetSshCommand` reads. (Never the local configuration of whatever repository the
/// process happens to be in.)
type ConfigScope =
    | GlobalScope
    | SystemScope

/// A remote: an address (validated) or the clone's own `origin`.
type RemoteRef =
    | Url of string
    | Origin

/// What `git add -A` may be given: a fixed path, never a list of records.
type StageScope =
    | Memories
    | Manifest
    | Attributes

/// A revision, in the only shapes sync uses.
type Revision =
    | Head
    | Sha of string
    | RemoteBranch of string
    | LocalBranch of string
    | LastSynced

/// Every `git` invocation sync can make. There is no case that takes free arguments.
type GitCommand =
    | Version
    | ConfigGetSshCommand of ConfigScope
    | Init of branch: string * dir: string
    | Clone of url: string * branch: string * dir: string
    | LsRemote of RemoteRef * branch: string
    | AddOrigin of url: string
    | SetOriginUrl of url: string
    | Fetch of branch: string
    | ResetHard of Revision
    | Clean
    | IsAncestor of Revision * Revision
    | RevParse of Revision
    | UpdateLastSynced of sha: string
    | DiffTree of fromRev: Revision * toRev: Revision
    | LsTree of Revision
    | LsTreeRoot of Revision
    | CatBlob of sha: string
    | Stage of StageScope
    | HasStaged
    | Commit of message: string
    | Push of branch: string

/// The paths a command needs to become a full argument list.
type BuildContext =
    { CloneDir: string
      NoHooksDir: string
      EmptyFile: string
      Windows: bool }

// ---------------------------------------------------------------------------
// remote addresses

type RemoteKind =
    | UrlRemote
    | ScpLike
    | AbsolutePath

let private isAsciiLetter (c: char) = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
let private isAsciiDigit (c: char) = c >= '0' && c <= '9'
let private isSchemeChar (c: char) = isAsciiLetter c || isAsciiDigit c || c = '+' || c = '.' || c = '-'
let private isHostChar (c: char) = isAsciiLetter c || isAsciiDigit c || c = '.' || c = '_' || c = '-'

/// Where a scheme-like prefix `[A-Za-z][A-Za-z0-9+.-]*` ends, and what follows it: `Some (end, "://")`,
/// `Some (end, "::")` (a transport helper, which makes git run a program), or `None`.
let private schemePrefix (s: string) : (int * string) option =
    // git accepts a leading digit too (its check is `[A-Za-z0-9][A-Za-z0-9+.-]*`), so a helper such as `1ext::x` is caught
    if s.Length = 0 || not (isAsciiLetter s.[0] || isAsciiDigit s.[0]) then None
    else
        let mutable i = 1
        while i < s.Length && isSchemeChar s.[i] do
            i <- i + 1
        if s.Length >= i + 3 && s.Substring(i, 3) = "://" then Some(i, "://")
        elif s.Length >= i + 2 && s.Substring(i, 2) = "::" then Some(i, "::")
        else None

let private isDriveLetterPath (s: string) =
    s.Length >= 3
    && isAsciiLetter s.[0]
    && s.[1] = ':'
    && (s.[2] = '\\' || s.[2] = '/')

let private scpLike (s: string) : bool =
    let colon = s.IndexOf ':'
    if colon <= 0 || colon >= s.Length - 1 then false
    else
        let before = s.Substring(0, colon)
        if before.Contains "/" || before.Contains "\\" then false
        else
            let at = before.LastIndexOf '@'
            let host = if at >= 0 then before.Substring(at + 1) else before
            let userOk = at < 0 || at > 0
            userOk && host.Length >= 2 && host |> Seq.forall isHostChar

/// The shape of a repository address, or `None` when it is not one sync will pass to git: a transport helper
/// (`ext::`, `fd::`), a relative local path (git would resolve it against the directory the process runs in), a
/// drive-letter path off Windows (git sends it through ssh to a host named `C`), or anything `isUsableRemote` refuses.
let classifyRemote (windows: bool) (s: string) : RemoteKind option =
    if isNull s || not (GitSyncSettings.isUsableRemote s) then None
    else
        match schemePrefix s with
        | Some(_, "::") -> None
        | Some(_, _) -> Some UrlRemote
        | None ->
            if s.StartsWith("/", System.StringComparison.Ordinal) || s.StartsWith("\\\\", System.StringComparison.Ordinal) then
                Some AbsolutePath
            elif windows && isDriveLetterPath s then Some AbsolutePath
            elif scpLike s then Some ScpLike
            else None

/// `Ok` for an address sync may use. The message never carries the address (it may hold credentials).
let validateRemote (windows: bool) (s: string) : Result<unit, string> =
    match classifyRemote windows s with
    | Some _ -> Ok()
    | None -> Error "not a usable repository address"

// ---------------------------------------------------------------------------
// validation of the other values

let private isLowerHex (c: char) = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

let private isSha (s: string) : bool =
    not (isNull s) && (s.Length = 40 || s.Length = 64) && s |> Seq.forall isLowerHex

let private hasNul (s: string) = s.Contains "\u0000"

let private checkBranch (b: string) : Result<unit, string> =
    if GitSyncSettings.isUsableBranch b then Ok() else Error "not a usable branch name"

let private checkRevision (r: Revision) : Result<unit, string> =
    match r with
    | Head
    | LastSynced -> Ok()
    | Sha s -> if isSha s then Ok() else Error "not an object id"
    | RemoteBranch b
    | LocalBranch b -> checkBranch b

// `IsPathRooted` (not `IsPathFullyQualified`) so that a POSIX-looking path is accepted on Windows too; it also accepts
// the drive-relative `C:x` and `\x`, which the callers never produce (`GitSyncPaths` builds absolute paths).
let private checkDir (d: string) : Result<unit, string> =
    if System.String.IsNullOrEmpty d || hasNul d then Error "not a usable directory"
    elif not (System.IO.Path.IsPathRooted d) then Error "a directory must be an absolute path"
    else Ok()

/// A clone directory also must not be a filesystem root (`/`, `C:\`): `--work-tree=` would be empty.
let private checkCloneDir (d: string) : Result<unit, string> =
    match checkDir d with
    | Error e -> Error e
    | Ok() ->
        let root = d.TrimEnd('/', '\\')
        if root.Length = 0 || root.EndsWith(":", System.StringComparison.Ordinal) then Error "a clone directory must not be a root"
        else Ok()

let private checkUrl (windows: bool) (u: string) : Result<unit, string> = validateRemote windows u

/// `Ok` when `a` is `Ok` and `b ()` is `Ok`; the first `Error` otherwise (`b` runs only after an `Ok`).
let private andThen (b: unit -> Result<unit, string>) (a: Result<unit, string>) : Result<unit, string> =
    match a with
    | Ok() -> b ()
    | Error e -> Error e

let private validate (ctx: BuildContext) (cmd: GitCommand) : Result<unit, string> =
    match cmd with
    | Version
    | ConfigGetSshCommand _
    | Clean
    | HasStaged
    | Stage _ -> Ok()
    | Init(b, d) -> checkBranch b |> andThen (fun () -> checkDir d)
    | Clone(u, b, d) -> checkUrl ctx.Windows u |> andThen (fun () -> checkBranch b) |> andThen (fun () -> checkDir d)
    | LsRemote(Origin, b) -> checkBranch b
    | LsRemote(Url u, b) -> checkUrl ctx.Windows u |> andThen (fun () -> checkBranch b)
    | AddOrigin u
    | SetOriginUrl u -> checkUrl ctx.Windows u
    | Fetch b
    | Push b -> checkBranch b
    | ResetHard r
    | RevParse r
    | LsTree r
    | LsTreeRoot r -> checkRevision r
    | IsAncestor(a, b)
    | DiffTree(a, b) -> checkRevision a |> andThen (fun () -> checkRevision b)
    | UpdateLastSynced s
    | CatBlob s -> if isSha s then Ok() else Error "not an object id"
    | Commit m -> if System.String.IsNullOrEmpty m || hasNul m then Error "not a usable commit message" else Ok()

// ---------------------------------------------------------------------------
// argument lists

let private revText (r: Revision) : string =
    match r with
    | Head -> "HEAD"
    | Sha s -> s
    | RemoteBranch b -> "refs/remotes/origin/" + b
    | LocalBranch b -> "refs/heads/" + b
    | LastSynced -> "refs/total-recall/last-synced"

let private stagePath (s: StageScope) : string =
    match s with
    | Memories -> "memories"
    | Manifest -> "manifest.json"
    | Attributes -> ".gitattributes"

/// The argument list of a command, after the repository selector and the overrides. Precondition: the command
/// passed `build`'s validation (use `build`).
let commandArgs (cmd: GitCommand) : string list =
    match cmd with
    | Version -> [ "--version" ]
    | ConfigGetSshCommand GlobalScope -> [ "config"; "--global"; "--get"; "core.sshCommand" ]
    | ConfigGetSshCommand SystemScope -> [ "config"; "--system"; "--get"; "core.sshCommand" ]
    | Init(b, d) -> [ "init"; "-b"; b; "--template="; "--"; d ]
    | Clone(u, b, d) -> [ "clone"; "--depth=1"; "--no-tags"; "--single-branch"; "--branch"; b; "--template="; "--"; u; d ]
    | LsRemote(Origin, b) -> [ "ls-remote"; "--"; "origin"; "refs/heads/" + b ]
    | LsRemote(Url u, b) -> [ "ls-remote"; "--"; u; "refs/heads/" + b ]
    | AddOrigin u -> [ "remote"; "add"; "origin"; "--"; u ]
    | SetOriginUrl u -> [ "remote"; "set-url"; "origin"; "--"; u ]
    // the only `+` anywhere: it forces the tracking ref of the disposable clone, never a ref of the remote
    | Fetch b -> [ "fetch"; "--no-tags"; "--progress"; "origin"; "+refs/heads/" + b + ":refs/remotes/origin/" + b ]
    | ResetHard r -> [ "reset"; "--hard"; revText r ]
    | Clean -> [ "clean"; "-fd" ]
    | IsAncestor(a, b) -> [ "merge-base"; "--is-ancestor"; revText a; revText b ]
    | RevParse r -> [ "rev-parse"; "--verify"; "--quiet"; revText r + "^{commit}" ]
    | UpdateLastSynced s -> [ "update-ref"; revText LastSynced; s ]
    | DiffTree(a, b) -> [ "diff-tree"; "-r"; "-z"; "--no-renames"; "--raw"; revText a; revText b; "--"; "memories" ]
    | LsTree r -> [ "ls-tree"; "-r"; "-z"; "-l"; revText r; "--"; "memories" ]
    | LsTreeRoot r -> [ "ls-tree"; "-z"; "-l"; revText r ]
    | CatBlob s -> [ "cat-file"; "blob"; s ]
    | Stage s -> [ "add"; "-A"; "--"; stagePath s ]
    | HasStaged -> [ "diff"; "--cached"; "--quiet"; "--exit-code" ]
    | Commit m -> [ "commit"; "--no-verify"; "-m"; m ]
    // exactly this and nothing else: no `+`, no force option, no delete or mirror form
    | Push b -> [ "push"; "origin"; "HEAD:refs/heads/" + b ]

/// True for a command that works on the clone and so carries `--git-dir`/`--work-tree`.
let isCloneScoped (cmd: GitCommand) : bool =
    match cmd with
    | Version
    | ConfigGetSshCommand _
    | Init _
    | Clone _
    | LsRemote(Url _, _) -> false
    | _ -> true

/// True for a command that carries the configuration overrides (all but the two that only read the user's own
/// configuration).
let takesOverrides (cmd: GitCommand) : bool =
    match cmd with
    | Version
    | ConfigGetSshCommand _ -> false
    | _ -> true

/// The `-c key=value` overrides every git call carries, flattened as `"-c"; "key=value"; …`. The user's global
/// configuration is written for interactive use and would break background commits, prompt, sign, run hooks,
/// rewrite bytes or hide files; these values stop that. `core.fsmonitor=` is empty on purpose (before git 2.37 any
/// non-empty value, `false` included, names a hook program).
let overrides (noHooksDir: string) (emptyFile: string) : string list =
    [ "core.autocrlf=false"
      "core.symlinks=false"
      "core.longpaths=true"
      "commit.gpgsign=false"
      "core.hooksPath=" + noHooksDir
      "user.name=total-recall"
      "user.email=noreply@total-recall.local"
      "credential.interactive=never"
      "core.quotepath=false"
      "protocol.ext.allow=never"
      "protocol.fd.allow=never"
      "core.askPass="
      "core.attributesFile=" + emptyFile
      "core.excludesFile=" + emptyFile
      "core.fsmonitor="
      "push.gpgSign=false"
      "http.lowSpeedLimit=1000"
      "http.lowSpeedTime=20"
      "gc.auto=0"
      "maintenance.auto=false" ]
    |> List.collect (fun kv -> [ "-c"; kv ])

/// The whole argument list for a command: the explicit repository selector for a clone-scoped command (so a missing
/// or damaged `.git` is an error and git never searches upwards into a parent repository), then the overrides,
/// then the subcommand. `Error` names the problem and never echoes an address.
let build (ctx: BuildContext) (cmd: GitCommand) : Result<string list, string> =
    let cloneOk = if isCloneScoped cmd then checkCloneDir ctx.CloneDir else Ok()
    let overridesOk =
        if takesOverrides cmd then checkDir ctx.NoHooksDir |> andThen (fun () -> checkDir ctx.EmptyFile) else Ok()
    match cloneOk |> andThen (fun () -> overridesOk) |> andThen (fun () -> validate ctx cmd) with
    | Error e -> Error e
    | Ok() ->
        let selector =
            if isCloneScoped cmd then
                let root = ctx.CloneDir.TrimEnd('/', '\\')
                [ "--git-dir=" + root + "/.git"; "--work-tree=" + root ]
            else
                []
        let ov = if takesOverrides cmd then overrides ctx.NoHooksDir ctx.EmptyFile else []
        Ok(selector @ ov @ commandArgs cmd)

// ---------------------------------------------------------------------------
// the environment

let private allowedGitNames =
    set [ "GIT_SSH"
          "GIT_SSH_COMMAND"
          "GIT_SSH_VARIANT"
          "GIT_PROXY_COMMAND"
          "GIT_CONFIG_GLOBAL"
          "GIT_CONFIG_SYSTEM"
          "GIT_CONFIG_NOSYSTEM"
          "GIT_EXEC_PATH" ]

/// A process environment variable survives the scrub when it is not a `GIT_` variable, or is one of the few sync
/// needs: the ssh command and variant, the proxy and TLS settings, the user's choice of configuration files and the
/// git install's helper path. Everything else that starts with `GIT_` (repository selectors, injected configuration,
/// author and committer identity, tracing) is dropped. Names are compared without regard to case.
let isAllowedEnvName (name: string) : bool =
    let upper = name.ToUpperInvariant()
    if not (upper.StartsWith("GIT_", System.StringComparison.Ordinal)) then true
    else allowedGitNames.Contains upper || upper.StartsWith("GIT_SSL_", System.StringComparison.Ordinal)

/// Variables removed whatever their name (an askpass program for ssh would prompt).
let removedEnvironmentNames : string list = [ "SSH_ASKPASS" ]

/// The process environment without the variables above, in its original order.
let scrubEnvironment (env: (string * string) list) : (string * string) list =
    env
    |> List.filter (fun (name, _) ->
        isAllowedEnvName name
        && not (removedEnvironmentNames |> List.exists (fun r -> System.String.Equals(r, name, System.StringComparison.OrdinalIgnoreCase))))

/// Set after the scrub: no terminal prompt, and English messages for the failure classifier.
let fixedEnvironment : (string * string) list =
    [ "GIT_TERMINAL_PROMPT", "0"; "LC_ALL", "C"; "LANGUAGE", "C" ]
