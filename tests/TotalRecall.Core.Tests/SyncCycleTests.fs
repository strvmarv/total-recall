module TotalRecall.Core.Tests.SyncCycleTests

open System
open Expecto
open TotalRecall.Core
open TotalRecall.Core.SyncCycle

let private allKinds : SyncGit.FailureKind list =
    [ SyncGit.FailureKind.NoGit
      SyncGit.FailureKind.GitTooOld
      SyncGit.FailureKind.TimedOut
      SyncGit.FailureKind.Cancelled
      SyncGit.FailureKind.Auth
      SyncGit.FailureKind.RemoteNotFound
      SyncGit.FailureKind.Network
      SyncGit.FailureKind.NonFastForward
      SyncGit.FailureKind.RemoteBranchMissing
      SyncGit.FailureKind.NotARepository
      SyncGit.FailureKind.OutputTooLarge
      SyncGit.FailureKind.Other ]

let private at = DateTimeOffset(2026, 11, 19, 1, 2, 3, 456, TimeSpan.Zero)

[<Tests>]
let stepTests =
    testList "SyncCycle.stepName" [
        testCase "every step has its lowercase name" <| fun () ->
            let names = [ Lock; Identity; Locks; Scan; Clone; Fetch; Apply; Push; Record ] |> List.map stepName
            Expect.equal names [ "lock"; "identity"; "locks"; "scan"; "clone"; "fetch"; "apply"; "push"; "record" ] "names"
    ]

[<Tests>]
let retryTests =
    testList "SyncCycle.nextAfterPush (decision 6)" [
        testCase "at most three pushes per cycle" <| fun () ->
            Expect.equal maxAttempts 3 "three"
        testCase "success finishes at every attempt" <| fun () ->
            for attempt in 0 .. 4 do
                Expect.equal (nextAfterPush attempt Pushed) Finish (sprintf "pushed %d" attempt)
                Expect.equal (nextAfterPush attempt NothingToPush) Finish (sprintf "nothing %d" attempt)
        testCase "a failure and not-ready fail at every attempt" <| fun () ->
            for attempt in 0 .. 4 do
                Expect.equal (nextAfterPush attempt PushFailed) Fail (sprintf "failed %d" attempt)
                Expect.equal (nextAfterPush attempt NotReady) Fail (sprintf "not ready %d" attempt)
        testCase "a non-fast-forward retries until the third push" <| fun () ->
            Expect.equal (nextAfterPush 0 NonFastForward) Retry "0 counts as 1"
            Expect.equal (nextAfterPush 1 NonFastForward) Retry "first"
            Expect.equal (nextAfterPush 2 NonFastForward) Retry "second"
            Expect.equal (nextAfterPush 3 NonFastForward) Fail "third"
            Expect.equal (nextAfterPush 4 NonFastForward) Fail "beyond"
    ]

[<Tests>]
let gateTests =
    testList "SyncCycle.pushDecision (decision 5)" [
        testCase "all eight combinations" <| fun () ->
            for fetchWentOn in [ true; false ] do
                for applyDone in [ true; false ] do
                    for complete in [ true; false ] do
                        let expected =
                            if fetchWentOn && applyDone && complete then Run
                            elif fetchWentOn && applyDone then Skip "pull-incomplete"
                            else Skip "pull-not-done"
                        Expect.equal (pushDecision fetchWentOn applyDone complete) expected (sprintf "%b %b %b" fetchWentOn applyDone complete)
    ]

[<Tests>]
let damageTests =
    testList "SyncCycle.cloneDamage (decision 8)" [
        testCase "NotARepository is damage whatever the probe says" <| fun () ->
            for probe in [ Healthy; NoCommit; ProbeFailed SyncGit.FailureKind.Network ] do
                Expect.isTrue (cloneDamage SyncGit.FailureKind.NotARepository probe) (sprintf "%A" probe)
        testCase "Other and RemoteNotFound are decided by the probe" <| fun () ->
            for kind in [ SyncGit.FailureKind.Other; SyncGit.FailureKind.RemoteNotFound ] do
                Expect.isFalse (cloneDamage kind Healthy) (sprintf "%A healthy" kind)
                Expect.isTrue (cloneDamage kind NoCommit) (sprintf "%A no commit" kind)
                for probeKind in allKinds do
                    let inconclusive =
                        probeKind = SyncGit.FailureKind.Cancelled
                        || probeKind = SyncGit.FailureKind.TimedOut
                        || probeKind = SyncGit.FailureKind.OutputTooLarge
                    Expect.equal (cloneDamage kind (ProbeFailed probeKind)) (not inconclusive) (sprintf "%A probe failed with %A" kind probeKind)
        testCase "no other kind is ever damage" <| fun () ->
            let others =
                allKinds
                |> List.filter (fun k ->
                    k <> SyncGit.FailureKind.NotARepository
                    && k <> SyncGit.FailureKind.Other
                    && k <> SyncGit.FailureKind.RemoteNotFound)
            for kind in others do
                for probe in [ Healthy; NoCommit; ProbeFailed SyncGit.FailureKind.Other ] do
                    Expect.isFalse (cloneDamage kind probe) (sprintf "%A %A" kind probe)
    ]

[<Tests>]
let parseKindTests =
    testList "SyncCycle.parseKind" [
        testCase "every case name parses to its case" <| fun () ->
            for kind in allKinds do
                Expect.equal (parseKind (sprintf "%A" kind)) kind (sprintf "%A" kind)
        testCase "the pusher's Clone, empty, null, unknown and wrong-case names are Other" <| fun () ->
            for name in [ "Clone"; ""; null; "Bogus"; "network"; "NETWORK"; " Network" ] do
                Expect.equal (parseKind name) SyncGit.FailureKind.Other (sprintf "%A" name)
    ]

[<Tests>]
let textTests =
    testList "SyncCycle.okValue and errorLine (decision 10)" [
        testCase "okValue is UTC with milliseconds" <| fun () ->
            Expect.equal (okValue at) "2026-11-19T01:02:03.456Z" "utc"
            Expect.equal (okValue (DateTimeOffset(2026, 11, 19, 3, 2, 3, 456, TimeSpan.FromHours 2.0))) "2026-11-19T01:02:03.456Z" "offset input"
            Expect.equal (okValue at).Length 24 "length"
        testCase "errorLine is time, step, kind, message" <| fun () ->
            Expect.equal (errorLine at "fetch" "Network" "could not resolve host") "2026-11-19T01:02:03.456Z fetch Network: could not resolve host" "line"
        testCase "control characters, newlines, U+2028 and U+2029 become a space" <| fun () ->
            let line = errorLine at "push" "Other" "a\nb\tc\r\nd\u2028e\u2029f\u0000g"
            Expect.equal line "2026-11-19T01:02:03.456Z push Other: a b c  d e f g" "spaces"
        testCase "an empty or null message leaves no trailing space" <| fun () ->
            Expect.equal (errorLine at "scan" "Store" "") "2026-11-19T01:02:03.456Z scan Store:" "empty"
            Expect.equal (errorLine at "scan" "Store" null) "2026-11-19T01:02:03.456Z scan Store:" "null"
        testCase "a long message is cut so the whole line has 500 characters" <| fun () ->
            let line = errorLine at "apply" "Apply" (String('x', 600))
            Expect.equal line.Length 500 "length"
            Expect.stringStarts line "2026-11-19T01:02:03.456Z apply Apply: xxx" "prefix kept"
        testCase "a surrogate pair is never split" <| fun () ->
            let head = "2026-11-19T01:02:03.456Z apply Apply: "
            let padding = String('x', 500 - head.Length - 1)
            let line = errorLine at "apply" "Apply" (padding + "\U0001F600" + "tail")
            Expect.equal line.Length 499 "cut before the pair"
            Expect.isFalse (Char.IsHighSurrogate line.[line.Length - 1]) "no lone high surrogate"
            let kept = errorLine at "apply" "Apply" (String('x', 500 - head.Length - 2) + "\U0001F600" + "tail")
            Expect.equal kept.Length 500 "a pair that fits is kept"
            Expect.isTrue (Char.IsLowSurrogate kept.[kept.Length - 1]) "ends with the whole pair"
        testCase "the time prefix round-trips" <| fun () ->
            let line = errorLine at "fetch" "Auth" "x"
            let parsed = DateTimeOffset.ParseExact(line.Substring(0, 24), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal)
            Expect.equal parsed at "same instant"
        testCase "totality: random text never throws, never holds a control character, never exceeds 500" <| fun () ->
            let random = Random(20261119)
            for _ in 1 .. 2000 do
                let length = random.Next(0, 900)
                let message = String(Array.init length (fun _ -> char (random.Next(0, 0x2100))))
                let line = errorLine at "push" "Other" message
                Expect.isLessThanOrEqual line.Length 500 "length"
                Expect.isFalse (line |> Seq.exists Char.IsControl) "no control character"
                Expect.isFalse (line.Contains '\u2028' || line.Contains '\u2029') "no separators"
    ]
