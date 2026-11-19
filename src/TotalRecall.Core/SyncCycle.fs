/// Git sync, milestone 1b-i-4: the pure rules of the sync cycle. The cycle runs the scan, the fetch, the apply and the
/// push in order under the sync lock. This module decides, without touching anything: how many pushes one cycle may make
/// and what follows a push (`nextAfterPush`), whether the push may run after the pull (`pushDecision`), whether a failed
/// git call means the local clone is damaged (`cloneDamage`, `parseKind`) and the text of the two `_meta` values the cycle
/// records (`okValue`, `errorLine`). Pure and total: no I/O, no clock.
module TotalRecall.Core.SyncCycle

open System
open System.Globalization
open System.Text

/// The steps of a cycle; `Locks` and `Record` exist only as progress names.
type Step =
    | Lock
    | Identity
    | Locks
    | Scan
    | Clone
    | Fetch
    | Apply
    | Push
    | Record

let stepName (step: Step) : string =
    match step with
    | Lock -> "lock"
    | Identity -> "identity"
    | Locks -> "locks"
    | Scan -> "scan"
    | Clone -> "clone"
    | Fetch -> "fetch"
    | Apply -> "apply"
    | Push -> "push"
    | Record -> "record"

/// At most this many pushes in one cycle (decision 6).
let maxAttempts = 3

/// What the push step reported, reduced to what the retry rule needs.
type PushKind =
    | Pushed
    | NothingToPush
    | NotReady
    | NonFastForward
    | PushFailed

type Next =
    | Finish
    | Fail
    | Retry

/// `attempt` is the number of pushes made so far, this one included (1-based; zero or less counts as 1). Only a
/// non-fast-forward is retried, and only while fewer than `maxAttempts` pushes were made.
let nextAfterPush (attempt: int) (kind: PushKind) : Next =
    let made = max 1 attempt
    match kind with
    | Pushed
    | NothingToPush -> Finish
    | NotReady
    | PushFailed -> Fail
    | NonFastForward -> if made < maxAttempts then Retry else Fail

type PushGate =
    | Run
    | Skip of reason: string

/// The push runs only after a fetch that went on, an apply that returned `Done` and a complete pull (decision 5). An
/// incomplete pull is a skip, not an error. The other combinations end the cycle earlier; `pull-not-done` only keeps
/// the function total.
let pushDecision (fetchWentOn: bool) (applyDone: bool) (complete: bool) : PushGate =
    if fetchWentOn && applyDone && complete then Run
    elif fetchWentOn && applyDone then Skip "pull-incomplete"
    else Skip "pull-not-done"

/// The answer of the local probe `rev-parse --verify HEAD^{commit}` on the clone.
type Probe =
    | Healthy
    | NoCommit
    | ProbeFailed of SyncGit.FailureKind

let private inconclusive (kind: SyncGit.FailureKind) : bool =
    match kind with
    | SyncGit.FailureKind.Cancelled
    | SyncGit.FailureKind.TimedOut
    | SyncGit.FailureKind.OutputTooLarge -> true
    | _ -> false

/// Whether a failed git call means the local clone is damaged (decision 8). `NotARepository` always is. `Other` is
/// damage or a transient server error, and `RemoteNotFound` is what the fetcher's first clone-scoped call
/// (`ls-remote -- origin`) says when the clone's `.git` is invalid, so both are decided by the probe: damage only when
/// the probe finds no commit or fails for a reason that is not a limit or a cancel. A network, authentication, timeout
/// or cancelled failure never is.
let cloneDamage (kind: SyncGit.FailureKind) (probe: Probe) : bool =
    match kind with
    | SyncGit.FailureKind.NotARepository -> true
    | SyncGit.FailureKind.Other
    | SyncGit.FailureKind.RemoteNotFound ->
        match probe with
        | Healthy -> false
        | NoCommit -> true
        | ProbeFailed k -> not (inconclusive k)
    | _ -> false

/// The failure kind a part reported by name. Case-sensitive; null, empty, an unknown name and the pusher's `"Clone"`
/// give `Other`. (`FailureKind` is a union, not an enum.)
let parseKind (name: string) : SyncGit.FailureKind =
    match name with
    | "NoGit" -> SyncGit.FailureKind.NoGit
    | "GitTooOld" -> SyncGit.FailureKind.GitTooOld
    | "TimedOut" -> SyncGit.FailureKind.TimedOut
    | "Cancelled" -> SyncGit.FailureKind.Cancelled
    | "Auth" -> SyncGit.FailureKind.Auth
    | "RemoteNotFound" -> SyncGit.FailureKind.RemoteNotFound
    | "Network" -> SyncGit.FailureKind.Network
    | "NonFastForward" -> SyncGit.FailureKind.NonFastForward
    | "RemoteBranchMissing" -> SyncGit.FailureKind.RemoteBranchMissing
    | "NotARepository" -> SyncGit.FailureKind.NotARepository
    | "OutputTooLarge" -> SyncGit.FailureKind.OutputTooLarge
    | _ -> SyncGit.FailureKind.Other

/// `git_sync_last_ok`: UTC, `yyyy-MM-ddTHH:mm:ss.fffZ`, invariant culture (24 characters).
let okValue (now: DateTimeOffset) : string =
    now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

/// The longest line `git_sync_last_error` holds, in UTF-16 code units.
let maxErrorLineChars = 500

/// `git_sync_last_error`: `<time> <step> <kind>: <message>` on one line. Control characters and U+2028 / U+2029 in the
/// message, the step and the kind become a space; the line is cut to 500 code units without splitting a surrogate pair; an empty or null
/// message leaves `<time> <step> <kind>:` with no trailing space.
let errorLine (now: DateTimeOffset) (step: string) (kind: string) (message: string) : string =
    let oneLine (text: string) =
        let cleaned = StringBuilder()
        if not (isNull text) then
            for c in text do
                cleaned.Append(if Char.IsControl c || c = '\u2028' || c = '\u2029' then ' ' else c) |> ignore
        cleaned.ToString()
    let head = sprintf "%s %s %s:" (okValue now) (oneLine step) (oneLine kind)
    let body = oneLine message
    let line = if body.Length = 0 then head else head + " " + body
    if line.Length <= maxErrorLineChars then line
    else
        let cut = if Char.IsHighSurrogate line.[maxErrorLineChars - 1] then maxErrorLineChars - 1 else maxErrorLineChars
        line.Substring(0, cut)
