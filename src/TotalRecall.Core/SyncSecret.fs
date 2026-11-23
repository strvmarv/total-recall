/// Git sync, secret block (#161, decisions D1 and D3): the pure key and decisions of the secret check. A memory the
/// scanner finds a secret in is held back from the push; the hold is a mark on its `git_sync_entry` row
/// (`blocked_reason` = `secret-detected`, `blocked_hash` = the key) and lifts when the memory's pushed fields change
/// and a new scan finds nothing, or when the user allows the current fields (`allowed_hash` = the key).
/// Pure and total: no I/O.
module TotalRecall.Core.SyncSecret

open System.Text
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncScan

/// The `blocked_reason` of a row held back because a secret was found.
[<Literal>]
let blockReason = "secret-detected"

/// SHA-256 (lowercase hex) of `total-recall/scan/secret/v1\n`, the text, the project and the scope (each as
/// `contentHash` encodes an optional string), then the normalised tags as `tagsHash` encodes them (4-byte big-endian
/// count, each tag length-prefixed). The entry type, the source tool and `created_at` are not part of it: they are not
/// what a secret check reads.
let secretKey (content: ContentFields) (tags: string list) : string =
    let tags = normaliseTags tags
    let buf = ResizeArray<byte>()
    addBytes buf (Encoding.UTF8.GetBytes "total-recall/scan/secret/v1\n")
    addField buf (Some content.Text)
    addField buf content.Project
    addField buf content.Scope
    addBytes buf (lengthBytes tags.Length)
    for t in tags do
        let bytes = Encoding.UTF8.GetBytes t
        addBytes buf (lengthBytes bytes.Length)
        addBytes buf bytes
    sha256Hex buf

/// What the push may conclude from the row alone, before it runs the detector.
type Precheck =
    /// The user allowed these exact fields: push, no scan.
    | Allowed
    /// Already held back for these exact fields: stay held back, no scan.
    | StillBlocked
    /// Neither: run the detector.
    | NeedsScan

/// `Allowed` when `allowedHash` is the key; else `StillBlocked` when the row's reason is `secret-detected` and
/// `blockedHash` is the key; else `NeedsScan`. A writer's reason (such as `too-large`) never counts as a secret block.
let precheck (reason: string option) (blockedHash: string option) (allowedHash: string option) (key: string) : Precheck =
    if allowedHash = Some key then Allowed
    elif reason = Some blockReason && blockedHash = Some key then StillBlocked
    else NeedsScan

/// What to do to the row's block mark.
type MarkChange =
    /// Store reason `secret-detected` and `blocked_hash` = the key, replacing any other reason.
    | SetBlock of key: string
    /// Clear the reason and `blocked_hash`: they were a secret block.
    | ClearBlock
    /// Leave the mark as it is (none, or a writer's reason).
    | KeepMark

/// After a detector run: a hit sets the block whatever the reason was; no hit lifts a secret block and leaves any
/// other reason alone.
let afterScan (reason: string option) (hit: bool) (key: string) : MarkChange =
    if hit then SetBlock key
    elif reason = Some blockReason then ClearBlock
    else KeepMark

/// After the fields were allowed: a secret block is lifted, any other reason stays.
let afterAllowed (reason: string option) : MarkChange =
    if reason = Some blockReason then ClearBlock else KeepMark
