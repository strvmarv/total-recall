module TotalRecall.Core.Tests.SyncPushTests

open Expecto
open TotalRecall.Core
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncPush

let private m1 = (MachineId.tryCreate "4f2a9c1e").Value
let private st (n: int) : Stamp = { TimeMs = 1_700_000_000_000L + int64 n * 1000L; Counter = 0; Machine = m1 }
let private noStamps : GroupStamps = { GroupStamps.Content = None; Sticky = None; Tier = None }

// ---- candidates ----

let private row : RowFacts =
    { State = Active
      HasBase = true
      BaseTagsHash = Some "h1"
      ScannedTagsHash = Some "h1"
      Pending = noStamps }

[<Tests>]
let candidateTests =
    testList "SyncPush.isCandidate (decision 4)" [
        testCase "a row with a base and nothing pending is not a candidate" <| fun () ->
            Expect.isFalse (isCandidate row) "clean"
        testCase "a row with no base is a candidate" <| fun () ->
            Expect.isTrue (isCandidate { row with HasBase = false; BaseTagsHash = None }) "no base"
        testCase "each pending group alone makes a candidate" <| fun () ->
            Expect.isTrue (isCandidate { row with Pending = { noStamps with Content = Some(st 1) } }) "content"
            Expect.isTrue (isCandidate { row with Pending = { noStamps with Sticky = Some(st 1) } }) "sticky"
            Expect.isTrue (isCandidate { row with Pending = { noStamps with Tier = Some(st 1) } }) "tier"
        testCase "different tags hashes make a candidate, equal or a NULL one do not" <| fun () ->
            Expect.isTrue (isCandidate { row with ScannedTagsHash = Some "h2" }) "different"
            Expect.isFalse (isCandidate { row with ScannedTagsHash = None }) "scanned NULL"
            Expect.isFalse (isCandidate { row with BaseTagsHash = None }) "base unreadable"
        testCase "a forgotten or left row is never a candidate" <| fun () ->
            for s in [ Forgotten; Left ] do
                Expect.isFalse (isCandidate { row with State = s; HasBase = false }) (sprintf "%A" s)
    ]

// ---- exclusions ----

let private none : ExclusionFacts =
    { HasIssueOrSkip = false
      RejectedNamesId = false
      HasBase = true
      RecordAccepted = true
      RecordUnreadable = false
      BaseUnreadable = false
      PathOccupied = false
      BlockedElsewhere = false
      EntryUnreadable = false }

[<Tests>]
let exclusionTests =
    testList "SyncPush.exclusion (decision 5)" [
        testCase "a plain changed entry and a plain new entry are not excluded" <| fun () ->
            Expect.isNone (exclusion none) "changed"
            Expect.isNone (exclusion { none with HasBase = false; RecordAccepted = false }) "new"
        testCase "every reason alone" <| fun () ->
            let cases =
                [ { none with HasIssueOrSkip = true }, Issue
                  { none with RejectedNamesId = true }, RejectedRecord
                  { none with HasBase = false }, NotReconciled
                  { none with RecordAccepted = false }, RecordMissing
                  { none with RecordUnreadable = true }, RecordUnreadable
                  { none with BaseUnreadable = true }, UnreadableBase
                  { none with PathOccupied = true }, PathOccupied
                  { none with BlockedElsewhere = true }, BlockedElsewhere
                  { none with EntryUnreadable = true }, UnreadableEntry ]
            for (facts, expected) in cases do
                Expect.equal (exclusion facts) (Some expected) (exclusionName expected)
        testCase "each adjacent pair follows the order of decision 5" <| fun () ->
            let all =
                { HasIssueOrSkip = true
                  RejectedNamesId = true
                  HasBase = false
                  RecordAccepted = true
                  RecordUnreadable = true
                  BaseUnreadable = true
                  PathOccupied = true
                  BlockedElsewhere = true
                  EntryUnreadable = true }
            Expect.equal (exclusion all) (Some Issue) "issue first"
            let a = { all with HasIssueOrSkip = false }
            Expect.equal (exclusion a) (Some RejectedRecord) "rejected next"
            let b = { a with RejectedNamesId = false }
            Expect.equal (exclusion b) (Some NotReconciled) "not-reconciled next (no base, record accepted)"
            let c = { b with HasBase = true }
            Expect.equal (exclusion c) (Some RecordUnreadable) "record-unreadable next"
            let d = { c with RecordUnreadable = false }
            Expect.equal (exclusion d) (Some UnreadableBase) "unreadable-base next"
            let e = { d with BaseUnreadable = false }
            Expect.equal (exclusion e) (Some PathOccupied) "path-occupied next"
            let f = { e with PathOccupied = false }
            Expect.equal (exclusion f) (Some BlockedElsewhere) "blocked-elsewhere next"
            let g = { f with BlockedElsewhere = false }
            Expect.equal (exclusion g) (Some UnreadableEntry) "unreadable-entry last"
            Expect.isNone (exclusion { g with EntryUnreadable = false }) "none"
            Expect.equal (exclusion { { none with RecordAccepted = false } with PathOccupied = true }) (Some RecordMissing) "record-missing before path-occupied"
            Expect.equal (exclusion { none with RecordAccepted = false; BaseUnreadable = true }) (Some RecordMissing) "record-missing before unreadable-base"
        testCase "the reason words are the spec's" <| fun () ->
            Expect.equal
                ([ Issue; RejectedRecord; NotReconciled; RecordMissing; RecordUnreadable; UnreadableBase; PathOccupied; BlockedElsewhere; UnreadableEntry ]
                 |> List.map exclusionName)
                [ "issue"; "rejected-record"; "not-reconciled"; "record-missing"; "record-unreadable"; "unreadable-base"; "path-occupied"; "blocked-elsewhere"; "unreadable-entry" ]
                "names"
    ]

// ---- the path rule ----

[<Tests>]
let pathTests =
    testList "SyncPush.pathOccupied (decision 5)" [
        let id = "abcdef12"
        let own = "memories/ab/abcdef12.json"
        testCase "the record's own exact path alone is no collision" <| fun () ->
            Expect.isFalse (pathOccupied id [ own ]) "own"
        testCase "a case-folded shard directory collides" <| fun () ->
            Expect.isTrue (pathOccupied id [ "memories/AB/x.json" ]) "shard folded"
        testCase "a case variant of the record's own path collides" <| fun () ->
            Expect.isTrue (pathOccupied id [ "memories/ab/ABCDEF12.json" ]) "file folded"
        testCase "a blob at the shard directory or at memories collides" <| fun () ->
            Expect.isTrue (pathOccupied id [ "memories/ab" ]) "shard blob"
            Expect.isTrue (pathOccupied id [ "memories" ]) "memories blob"
        testCase "a path under the record's own path collides" <| fun () ->
            Expect.isTrue (pathOccupied id [ own + "/x" ]) "directory at the record"
        testCase "siblings with the same directories never collide" <| fun () ->
            Expect.isFalse (pathOccupied id [ own; "memories/ab/other.json"; "memories/cd/other.json" ]) "siblings"
        testCase "a deeper nested path under a folded shard collides" <| fun () ->
            Expect.isTrue (pathOccupied id [ "memories/AB/sub/x.json" ]) "nested"
        testCase "a root-level case variant is not compared (the listing never has it)" <| fun () ->
            Expect.isFalse (pathOccupied id [ "MEMORIES/x.json" ]) "root variant"
        testCase "an empty listing and an ineligible id are false, with no exception" <| fun () ->
            Expect.isFalse (pathOccupied id []) "empty"
            Expect.isFalse (pathOccupied "XYZ" [ "memories/xy/x.json" ]) "ineligible"
            Expect.isFalse (pathOccupied id [ null; own ]) "a null entry is no collision"
    ]

// ---- block marks ----

[<Tests>]
let markTests =
    testList "SyncPush.markAction (decision 8)" [
        testCase "every combination of reason, violation and candidate" <| fun () ->
            let tooLarge = Some SyncValidation.TooLarge
            let table =
                [ // reason, violation, candidate, expected
                  None, None, true, KeepMark
                  None, None, false, KeepMark
                  None, tooLarge, true, SetMark "too-large"
                  None, tooLarge, false, KeepMark
                  Some "too-large", None, true, ClearMark
                  Some "too-large", None, false, ClearMark
                  Some "too-large", tooLarge, true, SetMark "too-large"
                  Some "too-large", tooLarge, false, KeepMark
                  Some "secret", None, true, KeepMark
                  Some "secret", None, false, KeepMark
                  Some "secret", tooLarge, true, KeepMark
                  Some "secret", tooLarge, false, KeepMark ]
            for (reason, violation, candidate, expected) in table do
                Expect.equal (markAction reason violation candidate) expected (sprintf "%A %A %b" reason violation candidate)
        testCase "blockName names every Block case, and only the first five are the writer's" <| fun () ->
            let names =
                [ SyncValidation.TooLarge; SyncValidation.TooManyTags; SyncValidation.TagTooLong
                  SyncValidation.IneligibleId; SyncValidation.NotEncodable; SyncValidation.ClockExhausted ]
                |> List.map blockName
            Expect.equal names [ "too-large"; "too-many-tags"; "tag-too-long"; "ineligible-id"; "not-encodable"; "clock-exhausted" ] "names"
            Expect.equal (List.filter isWriterReason names) writerReasons "writer reasons"
    ]

// ---- what is written ----

let private baseRecord : SyncRecord =
    { Id = "abcdef12"
      Content = "old text"
      EntryType = Decision
      Tags = [ "a"; "b" ]
      Source = Some ClaudeCode
      CreatedAtMs = 1_700_000_000_000L
      Project = Some "p"
      Scope = Some "s"
      Placement = { Placement.Tier = Warm; TierStamp = st 5; Sticky = false; StickyStamp = st 5 }
      ContentStamp = st 5
      Origin = None
      WrittenBy = None }

let private localEntry : LocalEntry =
    { LocalEntry.Content =
        { Text = "new text"; Project = Some "p"; Scope = Some "s"; EntryType = Decision; Source = Some ClaudeCode; CreatedAtMs = 1_700_000_000_000L }
      Tags = [ "a"; "b" ]
      Tier = Warm
      Sticky = false }

[<Tests>]
let sourceTests =
    testList "SyncPush.sourceChange (decision 6)" [
        testCase "the same tool is kept, a different one is set" <| fun () ->
            Expect.equal (sourceChange (Some ClaudeCode) (Some ClaudeCode) false) KeepSource "same"
            Expect.equal (sourceChange (Some Cursor) (Some ClaudeCode) false) (SetSource Cursor) "differs"
        testCase "a local tool over a record with none is set (also over an unparsed source)" <| fun () ->
            Expect.equal (sourceChange (Some Cursor) None false) (SetSource Cursor) "record none"
            Expect.equal (sourceChange (Some Cursor) None true) (SetSource Cursor) "record unparsed"
        testCase "no local tool clears a parsed source, but never an unparsed one" <| fun () ->
            Expect.equal (sourceChange None (Some ClaudeCode) false) ClearSource "clear"
            Expect.equal (sourceChange None (Some ClaudeCode) true) KeepSource "unparsed text present"
            Expect.equal (sourceChange None None true) KeepSource "unparsed only"
            Expect.equal (sourceChange None None false) KeepSource "neither"
    ]

[<Tests>]
let planTests =
    testList "SyncPush.planChanged (decision 6)" [
        testCase "a pending content stamp above the record's writes the content group whole" <| fun () ->
            let p = planChanged baseRecord false localEntry { noStamps with Content = Some(st 9) }
            Expect.equal p.Content (Some { Stamp = st 9; Project = Some "p"; Scope = Some "s"; Source = KeepSource }) "content"
            Expect.isNone p.Tags "tags equal"
            Expect.isNone p.Sticky "no sticky"
            Expect.isNone p.Tier "no tier"
            Expect.isEmpty p.Stale "nothing stale"
        testCase "the content group clears an absent project or scope and sets a new one" <| fun () ->
            let cleared =
                planChanged baseRecord false
                    { localEntry with Content = { localEntry.Content with Project = None; Scope = Some "" } }
                    { noStamps with Content = Some(st 9) }
            Expect.equal (cleared.Content |> Option.map (fun c -> c.Project, c.Scope)) (Some(None, None)) "cleared"
            let set =
                planChanged { baseRecord with Project = None; Scope = None } false
                    { localEntry with Content = { localEntry.Content with Project = Some "q"; Scope = Some "t" } }
                    { noStamps with Content = Some(st 9) }
            Expect.equal (set.Content |> Option.map (fun c -> c.Project, c.Scope)) (Some(Some "q", Some "t")) "set"
        testCase "the content group carries the source change" <| fun () ->
            let src (l: LocalEntry) (r: SyncRecord) (unparsed: bool) =
                (planChanged r unparsed l { noStamps with Content = Some(st 9) }).Content |> Option.map (fun c -> c.Source)
            let withSource v = { localEntry with Content = { localEntry.Content with Source = v } }
            Expect.equal (src (withSource (Some Cursor)) baseRecord false) (Some(SetSource Cursor)) "differs"
            Expect.equal (src (withSource None) baseRecord false) (Some ClearSource) "no local tool"
            Expect.equal (src (withSource None) baseRecord true) (Some KeepSource) "unparsed text kept"
            Expect.equal (src (withSource (Some ClaudeCode)) baseRecord false) (Some KeepSource) "same"
        testCase "sticky and tier groups are written with their pending stamps" <| fun () ->
            let p = planChanged baseRecord false { localEntry with Sticky = true; Tier = Hot } { noStamps with Sticky = Some(st 8); Tier = Some(st 7) }
            Expect.equal p.Sticky (Some(st 8)) "sticky"
            Expect.equal p.Tier (Some(st 7)) "tier"
            Expect.isNone p.Content "no content"
        testCase "tags that differ are written sorted and unique, equal tags are not written" <| fun () ->
            let p = planChanged baseRecord false { localEntry with Tags = [ "z"; "a"; "z" ] } noStamps
            Expect.equal p.Tags (Some [ "a"; "z" ]) "sorted unique"
            Expect.isNone (planChanged baseRecord false { localEntry with Tags = [ "b"; "a"; "a" ] } noStamps).Tags "same set"
        testCase "a stale group is named and not written, the others are" <| fun () ->
            let p =
                planChanged baseRecord false { localEntry with Tags = [ "x" ] } { noStamps with Content = Some(st 5); Sticky = Some(st 4); Tier = Some(st 9) }
            Expect.isNone p.Content "equal stamp is stale"
            Expect.isNone p.Sticky "older stamp is stale"
            Expect.equal p.Tier (Some(st 9)) "tier written"
            Expect.equal p.Tags (Some [ "x" ]) "tags written"
            Expect.equal p.Stale [ "content"; "sticky" ] "stale names"
        testCase "every group stale, or nothing pending, is nothing to write" <| fun () ->
            Expect.isTrue (nothingToWrite (planChanged baseRecord false localEntry noStamps)) "nothing pending"
            Expect.isTrue (nothingToWrite (planChanged baseRecord false localEntry { noStamps with Content = Some(st 1); Sticky = Some(st 1); Tier = Some(st 1) })) "all stale"
            Expect.isFalse (nothingToWrite (planChanged baseRecord false { localEntry with Tags = [ "q" ] } noStamps)) "tags"
        testCase "pushedStamps holds the stamps of the written groups only" <| fun () ->
            let p = planChanged baseRecord false localEntry { noStamps with Content = Some(st 9); Sticky = Some(st 1) }
            Expect.equal (pushedStamps p) { noStamps with Content = Some(st 9) } "only content"
    ]

[<Tests>]
let newRecordTests =
    testList "SyncPush.newRecord (decision 7)" [
        let all = { GroupStamps.Content = Some(st 3); Sticky = Some(st 4); Tier = Some(st 5) }
        testCase "every field comes from the entry, the stamps and the label" <| fun () ->
            let origin = Some { Origin.Tool = "claude-code"; Hash = "abc" }
            match newRecord "abcdef12" localEntry all "box" origin with
            | Ok r ->
                Expect.equal r.Id "abcdef12" "id"
                Expect.equal r.Content "new text" "content"
                Expect.equal r.EntryType Decision "type"
                Expect.equal r.Tags [ "a"; "b" ] "tags"
                Expect.equal r.Source (Some ClaudeCode) "source"
                Expect.equal r.CreatedAtMs 1_700_000_000_000L "created"
                Expect.equal (r.Project, r.Scope) (Some "p", Some "s") "project and scope"
                Expect.equal r.Placement { Placement.Tier = Warm; TierStamp = st 5; Sticky = false; StickyStamp = st 4 } "placement"
                Expect.equal r.ContentStamp (st 3) "content stamp"
                Expect.equal r.Origin origin "origin"
                Expect.equal r.WrittenBy (Some "box") "written by"
            | Error e -> failtest e
        testCase "absent project, scope, source and origin stay absent, an empty scope is none" <| fun () ->
            let l = { localEntry with Content = { localEntry.Content with Project = None; Scope = Some ""; Source = None } }
            match newRecord "abcdef12" l all "box" None with
            | Ok r ->
                Expect.equal (r.Project, r.Scope, r.Source, r.Origin) (None, None, None, None) "absent"
            | Error e -> failtest e
        testCase "a sticky entry is written hot and sticky, whatever tier it is stored in" <| fun () ->
            match newRecord "abcdef12" { localEntry with Sticky = true; Tier = Warm } all "box" None with
            | Ok r -> Expect.equal (r.Placement.Tier, r.Placement.Sticky) (Hot, true) "pin"
            | Error e -> failtest e
        testCase "a missing stamp of any group is missing-stamp" <| fun () ->
            for stamps in [ { all with Content = None }; { all with Sticky = None }; { all with Tier = None } ] do
                Expect.equal (newRecord "abcdef12" localEntry stamps "box" None) (Error "missing-stamp") "missing"
        testCase "tags are normalised" <| fun () ->
            match newRecord "abcdef12" { localEntry with Tags = [ "z"; "a"; "z" ] } all "box" None with
            | Ok r -> Expect.equal r.Tags [ "a"; "z" ] "sorted unique"
            | Error e -> failtest e
    ]

[<Tests>]
let afterPushTests =
    testList "SyncPush.afterPush (decision 12)" [
        let all = { GroupStamps.Content = Some(st 3); Sticky = Some(st 4); Tier = Some(st 5) }
        let text = SyncApply.PushingStamps.encode all
        testCase "an equal pending stamp is cleared, a different one is kept" <| fun () ->
            let r = afterPush all text { noStamps with Content = Some(st 3); Sticky = Some(st 99) }
            Expect.equal r.Pending { noStamps with Sticky = Some(st 4); Tier = Some(st 5) } "pending"
        testCase "a group the push did not write is untouched in both columns" <| fun () ->
            let r = afterPush all text { noStamps with Content = Some(st 3) }
            Expect.equal r.Pending.Tier (Some(st 5)) "tier pending"
            Expect.equal (SyncApply.PushingStamps.decode (defaultArg r.PushingText null)) { noStamps with Sticky = Some(st 4); Tier = Some(st 5) } "pushing"
        testCase "pushing_stamps becomes none when every group was pushed" <| fun () ->
            Expect.equal (afterPush all text all).PushingText None "none"
            Expect.equal (afterPush noStamps None noStamps).PushingText None "nothing at all"
    ]

[<Tests>]
let resultTests =
    testList "SyncPush published, message and label" [
        testCase "publishedAfterPush follows decision 11" <| fun () ->
            for kind in [ SyncGit.FailureKind.Cancelled; SyncGit.FailureKind.TimedOut; SyncGit.FailureKind.Network; SyncGit.FailureKind.OutputTooLarge ] do
                Expect.equal (publishedAfterPush kind "") Unknown (sprintf "%A" kind)
            for kind in
                [ SyncGit.FailureKind.NoGit; SyncGit.FailureKind.GitTooOld; SyncGit.FailureKind.Auth; SyncGit.FailureKind.RemoteNotFound
                  SyncGit.FailureKind.RemoteBranchMissing; SyncGit.FailureKind.NotARepository; SyncGit.FailureKind.NonFastForward ] do
                Expect.equal (publishedAfterPush kind "") No (sprintf "%A" kind)
        testCase "Other is unknown unless the host refused the push" <| fun () ->
            let other = SyncGit.FailureKind.Other
            Expect.equal (publishedAfterPush other "the remote end hung up unexpectedly") Unknown "hung up"
            Expect.equal (publishedAfterPush other " ! [remote rejected] main -> main (pre-receive hook declined)") No "remote rejected"
            Expect.equal (publishedAfterPush other " ! [REJECTED] main") No "rejected, any case"
            Expect.equal (publishedAfterPush other "remote: error: hook declined to update refs/heads/main") No "hook declined"
            Expect.equal (publishedAfterPush other null) Unknown "null stderr"
        testCase "commitMessage" <| fun () ->
            Expect.equal (commitMessage 2 3 0 "box") "sync: box +2 ~3 -0" "message"
        testCase "commitMessage_counts_removals" <| fun () ->
            Expect.equal (commitMessage 0 0 1 "box") "sync: box +0 ~0 -1" "a removal only"
            Expect.equal (commitMessage 1 2 3 "box") "sync: box +1 ~2 -3" "all three counts"
        testCase "removable wants a local deletion with an accepted record, no occupied path and no rejected path" <| fun () ->
            Expect.isTrue (removable true true false false) "removable"
            Expect.isFalse (removable false true false false) "not a local deletion"
            Expect.isFalse (removable true false false false) "no accepted record"
            Expect.isFalse (removable true true true false) "a path in the way"
            Expect.isFalse (removable true true false true) "a rejected path names the id"
        testCase "machineLabel removes what is not allowed and falls back to unknown" <| fun () ->
            Expect.equal (machineLabel "Joe's PC") "JoesPC" "quote and space"
            Expect.equal (machineLabel "ünï") "n" "non-ASCII"
            Expect.equal (machineLabel "éü") "unknown" "nothing left"
            Expect.equal (machineLabel "a\nb") "ab" "newline"
            Expect.equal (machineLabel "") "unknown" "empty"
            Expect.equal (machineLabel "   ") "unknown" "blank"
            Expect.equal (machineLabel null) "unknown" "null"
            Expect.equal (machineLabel "a.b_c-d") "a.b_c-d" "allowed"
            Expect.equal (machineLabel (String.replicate 41 "a")) (String.replicate 40 "a") "cut at 40"
        testProperty "machineLabel is total, at most 40 characters, allowed characters only" <| fun (s: string) ->
            let l = machineLabel s
            l.Length >= 1 && l.Length <= 40 && l |> Seq.forall (fun c -> System.Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '-')
        testProperty "afterPush never invents a stamp" <| fun (a: bool) (b: bool) (c: bool) ->
            let pick (flag: bool) n = if flag then Some(st n) else None
            let pending = { GroupStamps.Content = pick a 1; Sticky = pick b 2; Tier = pick c 3 }
            let r = afterPush pending (SyncApply.PushingStamps.encode pending) { GroupStamps.Content = pick c 1; Sticky = pick a 2; Tier = pick b 9 }
            [ r.Pending.Content, pending.Content; r.Pending.Sticky, pending.Sticky; r.Pending.Tier, pending.Tier ]
            |> List.forall (fun (after, before) -> after = None || after = before)
    ]

[<Tests>]
let stagedTests =
    testList "SyncPush staged set" [
        let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes(s.Replace('|', '\000'))
        testCase "parseStagedSet reads status and path pairs" <| fun () ->
            Expect.equal
                (parseStagedSet (bytes "A|memories/ab/x.json|M|memories/cd/y.json|"))
                (Ok [ { Status = "A"; Path = "memories/ab/x.json" }; { Status = "M"; Path = "memories/cd/y.json" } ])
                "two"
            Expect.equal (parseStagedSet (bytes "")) (Ok []) "empty"
        testCase "parseStagedSet refuses malformed output" <| fun () ->
            Expect.isError (parseStagedSet (bytes "A|memories/ab/x.json|M|")) "an odd number of fields"
            Expect.isError (parseStagedSet (bytes "A|")) "one field"
            Expect.isError (parseStagedSet (bytes "R100|a|b|")) "rename shape (odd fields)"
            Expect.isError (parseStagedSet (bytes "R100|a|")) "a multi-letter status"
            Expect.isError (parseStagedSet (bytes "a|memories/ab/x.json|")) "lower-case status"
            Expect.isError (parseStagedSet [| 0xFFuy; 0uy |]) "not UTF-8"
        testCase "stagedSetMatches wants exactly the written paths as additions or modifications" <| fun () ->
            let a = { Status = "A"; Path = "memories/ab/x.json" }
            let m = { Status = "M"; Path = "memories/cd/y.json" }
            Expect.isTrue (stagedSetMatches [ a.Path; m.Path ] [] [ m; a ]) "both, any order"
            Expect.isFalse (stagedSetMatches [ a.Path; m.Path ] [] [ a ]) "a missing path"
            Expect.isFalse (stagedSetMatches [ a.Path ] [] [ a; m ]) "an extra path"
            Expect.isFalse (stagedSetMatches [ a.Path ] [] [ { a with Status = "D" } ]) "a deletion"
            Expect.isFalse (stagedSetMatches [ a.Path; a.Path ] [] [ a; a ]) "a duplicated written path"
            Expect.isTrue (stagedSetMatches [] [] []) "nothing"
        testCase "stagedSetMatches_accepts_a_deleted_removal_path_and_rejects_a_deleted_unexpected_path" <| fun () ->
            let a = { Status = "A"; Path = "memories/ab/x.json" }
            let d = { Status = "D"; Path = "memories/ef/z.json" }
            Expect.isTrue (stagedSetMatches [] [ d.Path ] [ d ]) "a removal only"
            Expect.isTrue (stagedSetMatches [ a.Path ] [ d.Path ] [ d; a ]) "a written and a removed path, any order"
            Expect.isFalse (stagedSetMatches [ a.Path ] [] [ a; d ]) "a deleted path nobody removed"
            Expect.isFalse (stagedSetMatches [] [ d.Path ] []) "a removal that is not staged"
            Expect.isFalse (stagedSetMatches [] [ d.Path; d.Path ] [ d; d ]) "a duplicated removed path"
            Expect.isFalse (stagedSetMatches [ d.Path ] [ d.Path ] [ d ]) "a path both written and removed"
            Expect.isFalse (stagedSetMatches [ a.Path ] [] [ { a with Status = "D" } ]) "a written path staged as a deletion"
        testCase "stagedSetMatches_rejects_a_removed_path_staged_as_modified" <| fun () ->
            let d = { Status = "D"; Path = "memories/ef/z.json" }
            Expect.isFalse (stagedSetMatches [] [ d.Path ] [ { d with Status = "M" } ]) "modified"
            Expect.isFalse (stagedSetMatches [] [ d.Path ] [ { d with Status = "A" } ]) "added"
    ]
