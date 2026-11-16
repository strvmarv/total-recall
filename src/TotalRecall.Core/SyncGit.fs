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
    | ConfigGetSshCommand GlobalScope -> [ "config"; "--global"; "--includes"; "--get"; "core.sshCommand" ]
    | ConfigGetSshCommand SystemScope -> [ "config"; "--system"; "--includes"; "--get"; "core.sshCommand" ]
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

// ---------------------------------------------------------------------------
// part B: failures, redaction, the ssh command, keys, ls-remote

/// What the process runner knows about a finished call.
type FailureInput =
    { ExitCode: int option
      TimedOut: bool
      Cancelled: bool
      SpawnFailed: bool
      OutputTooLarge: bool
      Stderr: string }

type FailureKind =
    | NoGit
    | GitTooOld
    | TimedOut
    | Cancelled
    | Auth
    | RemoteNotFound
    | Network
    | NonFastForward
    | RemoteBranchMissing
    | NotARepository
    | OutputTooLarge
    | Other

/// Text the user sees is kept to the last this many UTF-16 code units.
let maxStderrKeptChars = 2048

let private ord = System.StringComparison.Ordinal

let private containsAny (text: string) (fragments: string list) =
    fragments |> List.exists (fun f -> text.Contains(f, ord))

/// `first` somewhere, and `second` somewhere after it.
let private containsInOrder (text: string) (first: string) (second: string) =
    let i = text.IndexOf(first, ord)
    i >= 0 && text.IndexOf(second, i + first.Length, ord) >= 0

/// A pure classification of a finished call. The flags are decided first (in this order); then the standard error is
/// matched, case-insensitively, against the rows of decision 12 in order, the first match winning, so a specific line
/// beats git's generic `Could not read from remote repository` trailer. `GitTooOld` is not produced here: the version
/// check decides it.
let classifyFailure (i: FailureInput) : FailureKind =
    if i.SpawnFailed then NoGit
    elif i.TimedOut then TimedOut
    elif i.Cancelled then Cancelled
    elif i.OutputTooLarge then OutputTooLarge
    else
        let t = (if isNull i.Stderr then "" else i.Stderr).ToLowerInvariant()
        if containsAny t
            [ "terminal prompts disabled"; "could not read username"; "could not read password"
              "unable to get password from user"; "authentication failed"; "permission denied ("
              "host key verification failed"; "http 401"; "http 403"; "error: 401"; "error: 403"
              "invalid username or password" ] then Auth
        elif containsAny t [ "does not appear to be a git repository"; "repository not found"; "http 404"; "error: 404" ]
             || containsInOrder t "repository '" "' not found"
             || containsInOrder t "repository '" "' does not exist" then RemoteNotFound
        elif containsInOrder t "remote branch " " not found in upstream" || t.Contains("couldn't find remote ref", ord) then
            RemoteBranchMissing
        elif (t.Contains("[rejected]", ord) && (t.Contains("non-fast-forward", ord) || t.Contains("fetch first", ord)))
             || t.Contains("updates were rejected", ord) then NonFastForward
        elif containsAny t
                [ "could not resolve host"; "connection refused"; "connection timed out"; "operation timed out"
                  "network is unreachable"; "no route to host"; "failed to connect"; "connection reset"; "early eof"
                  "rpc failed"; "kex_exchange_identification"; "connection closed by"
                  "operation too slow" ] // the last is sync's own `http.lowSpeedLimit` stall, beyond the spec's table
             || (t.Contains("unable to access", ord)
                 && (System.Text.RegularExpressions.Regex.IsMatch(t, @"curl \d") || t.Contains("couldn't connect", ord))) then Network
        elif t.Contains("not a git repository", ord) then NotARepository
        else Other

/// The major and minor of `git version 2.45.1.windows.1`, else `None`.
let parseVersion (s: string) : (int * int) option =
    if isNull s then None
    else
        let m = System.Text.RegularExpressions.Regex.Match(s, @"git version (\d+)\.(\d+)")
        if m.Success then
            match System.Int32.TryParse m.Groups.[1].Value, System.Int32.TryParse m.Groups.[2].Value with
            | (true, a), (true, b) -> Some(a, b)
            | _ -> None
        else None

/// 2.28 (`init -b`) or later.
let isSupportedVersion (major: int, minor: int) : bool = major > 2 || (major = 2 && minor >= 28)

// ---- redaction -------------------------------------------------------------

let private isAuthorityEnd (c: char) = c = '/' || c = '"' || c = '\'' || System.Char.IsWhiteSpace c

/// For the first `://` at or after `from`: `(index of ://, start of the authority, index of the last @ in it or -1)`.
let private userInfoSpan (s: string) (from: int) : (int * int * int) option =
    let i = s.IndexOf("://", from, ord)
    if i < 0 then None
    else
        let start = i + 3
        let mutable e = start
        while e < s.Length && not (isAuthorityEnd s.[e]) do
            e <- e + 1
        // the last `@` inside [start, e)
        let mutable at = -1
        for k in start .. e - 1 do
            if s.[k] = '@' then at <- k
        Some(i, start, at)

/// The text between `://` and the last `@` of the authority of the first URL in `s`, else `None`.
let userInfoOf (s: string) : string option =
    if isNull s then None
    else
        match userInfoSpan s 0 with
        | Some(_, start, at) when at > start -> Some(s.Substring(start, at - start))
        | _ -> None

/// Replaces each (non-empty) literal in `secrets` with `***`, then the user-info of every `scheme://user[:pass]@`.
let redact (secrets: string list) (text: string) : string =
    if isNull text then ""
    else
        let afterSecrets =
            secrets
            |> List.filter (fun x -> not (System.String.IsNullOrEmpty x))
            // longest first, so a secret that contains another is replaced whole
            |> List.sortWith (fun a b -> if a.Length <> b.Length then compare b.Length a.Length else System.String.CompareOrdinal(a, b))
            |> List.fold (fun (acc: string) x -> acc.Replace(x, "***", ord)) text
        let sb = System.Text.StringBuilder()
        let mutable pos = 0
        let mutable go = true
        while go do
            match userInfoSpan afterSecrets pos with
            | None ->
                sb.Append(afterSecrets, pos, afterSecrets.Length - pos) |> ignore
                go <- false
            | Some(i, start, at) ->
                if at >= start then
                    sb.Append(afterSecrets, pos, start - pos).Append("***") |> ignore
                    pos <- at // the `@` itself is copied by the next round
                else
                    sb.Append(afterSecrets, pos, start - pos) |> ignore
                    pos <- start
        sb.ToString()

/// Splits on `\n`, drops one trailing `\r` from each line, keeps for each line only the text after its last `\r` (a
/// progress rewrite), redacts each line, joins them and keeps the last `maxStderrKeptChars` code units.
let cleanStderr (secrets: string list) (raw: string) : string =
    if System.String.IsNullOrEmpty raw then ""
    else
        let lines =
            raw.Split('\n')
            |> Array.map (fun line ->
                let l = if line.EndsWith("\r", ord) then line.Substring(0, line.Length - 1) else line
                let k = l.LastIndexOf '\r'
                let kept = if k >= 0 then l.Substring(k + 1) else l
                redact secrets kept)
        let joined = System.String.Join("\n", lines)
        if joined.Length > maxStderrKeptChars then
            let start = joined.Length - maxStderrKeptChars
            // never start on the second half of a surrogate pair
            let start = if System.Char.IsLowSurrogate joined.[start] then start + 1 else start
            joined.Substring start
        else
            joined

// ---- the ssh command -------------------------------------------------------

type SshSource =
    { EnvCommand: string option
      ConfigCommand: string option
      GitSsh: string option
      Variant: string option }

let private present (o: string option) : string option =
    match o with
    | Some s when not (System.String.IsNullOrWhiteSpace s) -> Some(s.Trim())
    | _ -> None

/// The program of a command line: its first token, honouring a leading double quote, as a lower-case base name.
let private programOf (command: string) : string =
    let c = command.TrimStart()
    let token =
        if c.StartsWith("\"", ord) || c.StartsWith("'", ord) then
            let close = c.IndexOf(c.[0], 1)
            if close > 0 then c.Substring(1, close - 1) else c.Substring 1
        else
            let sp = c.IndexOfAny [| ' '; '\t' |]
            if sp >= 0 then c.Substring(0, sp) else c
    let baseName = token.Replace('\\', '/').Split('/') |> Array.last
    baseName.ToLowerInvariant()

let private isPuttyName (name: string) = name.Contains("plink", ord) || name.Contains("putty", ord)

/// The `GIT_SSH_COMMAND` value: the user's command (the process's own `GIT_SSH_COMMAND`, else `core.sshCommand`,
/// else `ssh`) with `-o BatchMode=yes -o ConnectTimeout=8` appended, or `-batch` for a plink/putty program. `None`
/// (leave the environment alone) when only `GIT_SSH` names a program: it does not take OpenSSH options.
let composeSshCommand (src: SshSource) : string option =
    let command = present src.EnvCommand |> Option.orElse (present src.ConfigCommand)
    match command, present src.GitSsh with
    | None, Some _ -> None
    | _ ->
        let baseCommand = defaultArg command "ssh"
        let variantPutty =
            match present src.Variant with
            | Some v -> isPuttyName (v.ToLowerInvariant())
            | None -> false
        if variantPutty || isPuttyName (programOf baseCommand) then Some(baseCommand + " -batch")
        else Some(baseCommand + " -o BatchMode=yes -o ConnectTimeout=8")

// ---- keys ------------------------------------------------------------------

let private hex12 (s: string) : string =
    use sha = System.Security.Cryptography.SHA256.Create()
    let bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes s)
    System.String.Concat(bytes |> Array.take 6 |> Array.map (fun b -> b.ToString "x2"))

/// First 12 lowercase hex characters of the SHA-256 of the remote's UTF-8 bytes.
let cloneKey (remote: string) : string = hex12 remote

/// The same over the database file name, lower-cased invariantly first on a case-insensitive file system.
let dbKey (caseInsensitive: bool) (fileName: string) : string =
    hex12 (if caseInsensitive then fileName.ToLowerInvariant() else fileName)

// ---- ls-remote -------------------------------------------------------------

/// The sha of the line whose ref equals `refs/heads/<branch>` (`ls-remote` matches by suffix, so
/// `refs/heads/foo/refs/heads/main` is also listed for `main`).
let parseLsRemote (branch: string) (output: string) : string option =
    if System.String.IsNullOrEmpty output then None
    else
        let wanted = "refs/heads/" + branch
        output.Split('\n')
        |> Array.tryPick (fun raw ->
            let line = raw.TrimEnd '\r'
            let tab = line.IndexOf '\t'
            if tab <= 0 then None
            else
                let sha = line.Substring(0, tab)
                let r = line.Substring(tab + 1)
                if r = wanted && isSha sha then Some sha else None)
