module TotalRecall.Core.Tests.SyncStampTests

open System
open Expecto
open FsCheck
open TotalRecall.Core.SyncStamp

// Tests for TotalRecall.Core.SyncStamp: timestamps, machine ids and stamps
// (this file also holds the hybrid logical clock tests, added with the clock).

let private ts (s: string) = Timestamp.tryParse s |> Option.get
let private mid (s: string) = MachineId.tryCreate s |> Option.get
let private stamp (t: int64) (c: int) (m: string) : Stamp = { TimeMs = t; Counter = c; Machine = mid m }

/// A uniform-ish int64 in [lo, hi] (the ranges here are far below Int64.MaxValue).
let private rangeGen (lo: int64) (hi: int64) : Gen<int64> =
    let span = hi - lo + 1L
    // DoNotSize: the default int64 generator is limited to +-size (about 100), which would only reach the ends.
    Arb.generate<DoNotSize<int64>> |> Gen.map (fun (DoNotSize x) -> lo + (((x % span) + span) % span))

let private domainTime : Gen<int64> = rangeGen 0L maxTimeMs

let private machineGen : Gen<MachineId> =
    Gen.elements [ "00000001"; "4f2a9c1e"; "ffffffff"; "0000000a"; "0000000b" ] |> Gen.map mid

let private stampGen : Gen<Stamp> =
    gen {
        let! t = domainTime
        let! c = Gen.choose (0, maxCounter)
        let! m = machineGen
        return { TimeMs = t; Counter = c; Machine = m }
    }

[<Tests>]
let stampTests =
    testList "SyncStamp" [
        testCase "Stamp.format matches the spec example" <| fun _ ->
            let s = stamp (ts "2026-11-12T09:14:03.512Z") 3 "4f2a9c1e"
            Expect.equal (Stamp.format s) "2026-11-12T09:14:03.512Z.000003.4f2a9c1e" "spec example"

        testProperty "Stamp.tryParse (Stamp.format s) = Some s over the domain" <|
            Prop.forAll (Arb.fromGen stampGen) (fun s -> Stamp.tryParse (Stamp.format s) = Some s)

        testProperty "Timestamp round-trips over the whole CreatedAtMs domain" <|
            Prop.forAll
                (Arb.fromGen (rangeGen Timestamp.minMs Timestamp.maxMs))
                (fun ms -> Timestamp.tryParse (Timestamp.format ms) = Some ms)

        testCase "Timestamp domain edges" <| fun _ ->
            Expect.equal (Timestamp.format Timestamp.minMs) "0001-01-01T00:00:00.000Z" "min"
            Expect.equal (Timestamp.format Timestamp.maxMs) "9999-12-31T23:59:59.999Z" "max"
            Expect.equal (Timestamp.format 0L) "1970-01-01T00:00:00.000Z" "epoch"
            Expect.isTrue (Timestamp.inRange 0L) "epoch in range"
            Expect.isFalse (Timestamp.inRange (Timestamp.maxMs + 1L)) "above max"
            Expect.isFalse (Timestamp.inRange (Timestamp.minMs - 1L)) "below min"
            Expect.throws (fun () -> Timestamp.format (Timestamp.maxMs + 1L) |> ignore) "format out of range throws"

        testCase "malformed stamps are None and never throw" <| fun _ ->
            let bad =
                [ "2026-13-12T09:14:03.512Z.000003.4f2a9c1e"        // month 13
                  "2026-02-30T09:14:03.512Z.000003.4f2a9c1e"        // 30 February
                  "26-11-12T09:14:03.512Z.000003.4f2a9c1e"          // two-digit year
                  "2026-11-12T09:14:03.512Z.00003.4f2a9c1e"         // five-digit counter
                  "2026-11-12T09:14:03.512Z.0000003.4f2a9c1e"       // seven-digit counter
                  "2026-11-12T09:14:03.512Z.000003.4F2A9C1E"        // upper-case machine
                  "2026-11-12T09:14:03.512Z.000003.4f2a9c1"         // short machine
                  "2026-11-12T09:14:03.512Z.000003.4f2a9c1ee"       // long machine
                  "2026-11-12T09:14:03.512Z.000003.4f2a9c1e "       // trailing space
                  " 2026-11-12T09:14:03.512Z.000003.4f2a9c1e"       // leading space
                  "2026-11-12T09:14:03.512Z.000003.4f2a9c1e!"       // trailing text
                  "2026-11-12T09:14:03.512Z.+00003.4f2a9c1e"        // sign in counter
                  "2026-11-12T09:14:03.512Z.-00003.4f2a9c1e"
                  "2026-11-12T09:14:03.512Z.00000\uFF13.4f2a9c1e"   // fullwidth digit
                  "2026-11-12T09:14:60.512Z.000003.4f2a9c1e"        // second 60
                  "2026-11-12T24:14:03.512Z.000003.4f2a9c1e"        // hour 24
                  "1969-12-31T23:59:59.999Z.000000.4f2a9c1e"        // before the epoch
                  "2026-11-12t09:14:03.512Z.000003.4f2a9c1e"        // lower-case t
                  "2026-11-12T09:14:03.512z.000003.4f2a9c1e"        // lower-case z
                  "0000-11-12T09:14:03.512Z.000003.4f2a9c1e"        // year 0
                  "" ]
            for s in bad do
                Expect.isNone (Stamp.tryParse s) (sprintf "should reject %A" s)
            Expect.isSome (Timestamp.tryParse "1969-12-31T23:59:59.999Z") "a pre-epoch timestamp is fine"
            Expect.isNone (Timestamp.tryParse "2026-11-12T09:14:03Z") "no milliseconds is not the wire form"

        testCase "calendar validity: leap years, month lengths, zero fields" <| fun _ ->
            let t (date: string) = Timestamp.tryParse (date + "T00:00:00.000Z")
            for ok in [ "2000-02-29"; "2024-02-29"; "2026-12-31"; "2026-04-30"; "0004-02-29"; "9999-12-31" ] do
                Expect.isSome (t ok) (sprintf "%s is a real date" ok)
            for bad in [ "2025-02-29"; "1900-02-29"; "2100-02-29"; "2026-04-31"; "2026-06-31"; "2026-09-31"; "2026-11-31"
                         "2026-00-10"; "2026-01-00"; "2026-01-32"; "2026-13-01" ] do
                Expect.isNone (t bad) (sprintf "%s is not a real date" bad)
            // Round trip of dates a sized generator would never reach.
            for d in [ "2000-02-29"; "2024-02-29"; "1900-03-01"; "2100-03-01"; "0001-01-01"; "9999-12-31"; "1999-12-31" ] do
                let text = d + "T23:59:59.999Z"
                Expect.equal (t d |> Option.map Timestamp.format) (Some (d + "T00:00:00.000Z")) "format inverts parse"
                Expect.equal (Timestamp.tryParse text |> Option.map Timestamp.format) (Some text) "end of day"

        testCase "tryParse never throws on null or odd input" <| fun _ ->
            Expect.isNone (Stamp.tryParse null) "null stamp"
            Expect.isNone (Timestamp.tryParse null) "null timestamp"
            Expect.isNone (MachineId.tryCreate null) "null machine"

        testProperty "tryParse never throws and any Some re-formats to the input" <|
            Prop.forAll
                (Arb.fromGen (Gen.frequency [ 4, Arb.generate<string>
                                              1, Gen.elements [ "2026-11-12T09:14:03.512Z"; "2026-11-12T09:14:03.512Z.000003.4f2a9c1e" ]
                                              2, Gen.map (fun (s: string) -> "2026-11-12T09:14:03.512Z.000003." + s) Arb.generate<string> ]))
                (fun s ->
                    let stampOk = match Stamp.tryParse s with Some st -> Stamp.format st = s | None -> true
                    let tsOk = match Timestamp.tryParse s with Some ms -> Timestamp.format ms = s | None -> true
                    stampOk && tsOk)

        testCase "MachineId.tryCreate" <| fun _ ->
            Expect.isNone (MachineId.tryCreate "4F2A9C1E") "upper case"
            Expect.isNone (MachineId.tryCreate "4f2a9c1") "short"
            Expect.isNone (MachineId.tryCreate "4f2a9c1g") "non-hex"
            Expect.equal (MachineId.tryCreate "4f2a9c1e" |> Option.map MachineId.value) (Some "4f2a9c1e") "valid"

        testCase "order: time, then counter, then machine (ordinal)" <| fun _ ->
            let a = stamp 100L 5 "00000001"
            Expect.isLessThan (Stamp.compare a (stamp 101L 0 "00000001")) 0 "later time wins"
            Expect.isLessThan (Stamp.compare a (stamp 100L 6 "00000001")) 0 "higher counter wins"
            Expect.isLessThan (Stamp.compare (stamp 100L 5 "0000000a") (stamp 100L 5 "0000000b")) 0 "machine ordinal"
            Expect.equal (Stamp.compare a a) 0 "equal to itself"

        testProperty "stamps from different machines never compare equal" <|
            Prop.forAll (Arb.fromGen (Gen.two stampGen)) (fun (a, b) ->
                a.Machine = b.Machine || Stamp.compare a b <> 0)
    ]


// ---- the hybrid logical clock ----

let private clockGen : Gen<Clock> =
    gen {
        let! t = domainTime
        let! c = Gen.choose (0, maxCounter)
        return { ClockMs = t; ClockCounter = c }
    }

/// The one stamp/clock position `issue` cannot move past (its own test covers it).
let private notTop (t: int64) (c: int) = not (t = maxTimeMs && c = maxCounter)

/// Times clustered around `now` (ties, one millisecond either side, the drift limit and one past it)
/// plus fully random ones, and counters at the interesting values, so the counter rules are reached.
let private nearTime (now: int64) : Gen<int64> =
    Gen.frequency [ 6, Gen.elements [ 0L; 1L; -1L; driftLimitMs; driftLimitMs + 1L; -driftLimitMs ] |> Gen.map (fun d -> max 0L (min maxTimeMs (now + d)))
                    1, domainTime ]

let private counterGen : Gen<int> =
    Gen.frequency [ 4, Gen.elements [ 0; 1; 7; 9; 999_998; 999_999 ]
                    1, Gen.choose (0, maxCounter) ]

let private clockNear (now: int64) : Gen<Clock option> =
    Gen.frequency [ 1, Gen.constant None
                    5, gen {
                        let! t = nearTime now
                        let! c = counterGen
                        return Some { ClockMs = t; ClockCounter = c } } ]
    |> Gen.filter (function Some c -> notTop c.ClockMs c.ClockCounter | None -> true)

let private baseNear (now: int64) : Gen<Stamp option> =
    Gen.frequency [ 1, Gen.constant None
                    5, gen {
                        let! t = nearTime now
                        let! c = counterGen
                        let! m = machineGen
                        return Some { TimeMs = t; Counter = c; Machine = m } } ]
    |> Gen.filter (function Some b -> notTop b.TimeMs b.Counter | None -> true)

let private issueInputs : Gen<int64 * Clock option * Stamp option> =
    gen {
        let! now = domainTime
        let! clock = clockNear now
        let! baseS = baseNear now
        return now, clock, baseS
    }

let private pos (c: Clock) = (c.ClockMs, c.ClockCounter)

let private ok (r: Result<IssueResult, ClockError>) =
    match r with
    | Ok v -> v
    | Error e -> failwithf "expected Ok, got Error %A" e

[<Tests>]
let clockTests =
    let m = mid "4f2a9c1e"
    let T = ts "2026-11-12T10:00:00.000Z"
    testList "SyncStamp.Clock" [
        testProperty "issue is strictly greater than the clock and the base, and the clock never goes backwards" <|
            Prop.forAll (Arb.fromGen issueInputs) (fun (now, clock, baseS) ->
                let r = ok (Clock.issue m now clock baseS)
                let stampPos = (r.Stamp.TimeMs, r.Stamp.Counter)
                let aboveClock = match clock with Some c -> stampPos > pos c | None -> true
                let aboveBase = match baseS with Some b -> Stamp.compare r.Stamp b > 0 | None -> true
                let clockOk =
                    match clock, r.Clock with
                    | Some c, Some c2 -> pos c2 >= pos c
                    | Some _, None -> false
                    | None, _ -> true
                // FarFuture is exactly "the stamp is past now + the drift limit", and then the clock stays put.
                let farOk = r.FarFuture = (r.Stamp.TimeMs > now + driftLimitMs)
                let heldOk =
                    if r.FarFuture then r.Clock = clock
                    else r.Clock = Some { ClockMs = r.Stamp.TimeMs; ClockCounter = r.Stamp.Counter }
                aboveClock && aboveBase && clockOk && Stamp.inDomain r.Stamp && farOk && heldOk)

        testCase "slow clock: an unpin by a slow machine still sorts after the pin it saw" <| fun _ ->
            let a = mid "0000000a"
            let b = mid "0000000b"
            let pin = (ok (Clock.issue a (T + 500L) None None)).Stamp   // A pins at 10:00:00.500
            let seen = Clock.observe (T + 200L) None pin                // B's clock reads 10:00:00.200
            Expect.isFalse seen.FarFuture "not far future"
            let unpin = (ok (Clock.issue b (T + 200L) seen.Clock (Some pin))).Stamp
            Expect.isGreaterThan (Stamp.compare unpin pin) 0 "unpin after pin"

        testCase "counter rules" <| fun _ ->
            let at t c : Clock = { ClockMs = t; ClockCounter = c }
            // neither clock nor base has the winning time
            Expect.equal (ok (Clock.issue m T None None)).Stamp.Counter 0 "fresh"
            Expect.equal (ok (Clock.issue m (T + 5L) (Some (at T 9)) None)).Stamp.Counter 0 "now is later than the clock"
            // the clock has it
            Expect.equal (ok (Clock.issue m T (Some (at T 7)) None)).Stamp.Counter 8 "clock counter + 1"
            // the base has it
            Expect.equal (ok (Clock.issue m T None (Some (stamp T 4 "0000000a")))).Stamp.Counter 5 "base counter + 1"
            // both have it: the larger wins
            Expect.equal (ok (Clock.issue m T (Some (at T 7)) (Some (stamp T 9 "0000000a")))).Stamp.Counter 10 "larger + 1"
            // the clock's counter is the larger one: a base with a smaller counter must not win
            Expect.equal (ok (Clock.issue m T (Some (at T 9)) (Some (stamp T 7 "0000000a")))).Stamp.Counter 10 "clock larger"
            // the base is later than now and the clock
            let r = ok (Clock.issue m T (Some (at T 7)) (Some (stamp (T + 3L) 2 "0000000a")))
            Expect.equal (r.Stamp.TimeMs, r.Stamp.Counter) (T + 3L, 3) "base time wins"

        testCase "counter overflow advances the time by one millisecond" <| fun _ ->
            let top = 999_999
            let clk : Clock = { ClockMs = T; ClockCounter = top }
            for c, b in [ Some clk, None
                          None, Some (stamp T top "0000000a")
                          Some clk, Some (stamp T top "0000000a") ] do
                let r = ok (Clock.issue m T c b)
                Expect.equal (r.Stamp.TimeMs, r.Stamp.Counter) (T + 1L, 0) "next millisecond, counter restarts"

        testCase "top of the range" <| fun _ ->
            Expect.equal (Clock.issue m maxTimeMs None (Some (stamp maxTimeMs 999_999 "0000000a"))) (Error Exhausted) "exhausted"
            Expect.equal (Clock.issue m 0L (Some { ClockMs = maxTimeMs; ClockCounter = 999_999 }) None) (Error Exhausted) "exhausted by the clock"
            let r = ok (Clock.issue m T None (Some (stamp maxTimeMs 0 "0000000a")))
            Expect.equal (r.Stamp.TimeMs, r.Stamp.Counter) (maxTimeMs, 1) "one below the top still issues"
            Expect.isTrue r.FarFuture "and it is far in the future"
            // a wild wall clock is clamped, not trusted
            let w = ok (Clock.issue m Int64.MaxValue None None)
            Expect.equal w.Stamp.TimeMs maxTimeMs "now is clamped to the top"
            let n = ok (Clock.issue m Int64.MinValue None None)
            Expect.equal n.Stamp.TimeMs 0L "now is clamped to the bottom"

        testCase "overflow can land exactly on the last millisecond" <| fun _ ->
            let r = ok (Clock.issue m T (Some { ClockMs = maxTimeMs - 1L; ClockCounter = 999_999 }) None)
            Expect.equal (r.Stamp.TimeMs, r.Stamp.Counter) (maxTimeMs, 0) "last millisecond, counter 0, not Exhausted"

        testCase "a clock ahead of now but within the limit is adopted" <| fun _ ->
            let ahead : Clock = { ClockMs = T + 3_600_000L; ClockCounter = 2 }
            let r = ok (Clock.issue m T (Some ahead) None)
            Expect.isFalse r.FarFuture "an hour ahead is within the limit"
            Expect.equal r.Clock (Some { ClockMs = T + 3_600_000L; ClockCounter = 3 }) "moves with the stamp"

        testCase "guardStartup clamps a wild wall clock" <| fun _ ->
            let c : Clock = { ClockMs = 5L; ClockCounter = 1 }
            Expect.equal (Clock.guardStartup Int64.MinValue (Some c)).Reset false "now clamps to 0; a clock 5 ms ahead is within the limit"
            Expect.equal (Clock.guardStartup Int64.MinValue (Some { ClockMs = driftLimitMs + 1L; ClockCounter = 0 })) { Clock = Some { ClockMs = 0L; ClockCounter = 0 }; Reset = true } "reset to the clamped now"
            Expect.equal (Clock.guardStartup Int64.MaxValue (Some { ClockMs = maxTimeMs; ClockCounter = 3 })).Reset false "now clamps to the top, nothing is ahead"

        testCase "drift: exactly 24 hours ahead is adopted, one millisecond more is not (issue)" <| fun _ ->
            let now = T
            let edge = stamp (now + driftLimitMs) 2 "0000000a"
            let r1 = ok (Clock.issue m now None (Some edge))
            Expect.isFalse r1.FarFuture "at the limit"
            Expect.equal r1.Clock (Some { ClockMs = now + driftLimitMs; ClockCounter = 3 }) "clock moves"
            let far = stamp (now + driftLimitMs + 1L) 2 "0000000a"
            let own : Clock = { ClockMs = now; ClockCounter = 4 }
            let r2 = ok (Clock.issue m now (Some own) (Some far))
            Expect.isTrue r2.FarFuture "past the limit"
            Expect.equal r2.Clock (Some own) "the input clock is returned unchanged"
            Expect.isGreaterThan (Stamp.compare r2.Stamp far) 0 "but the new stamp still supersedes the base"
            Expect.equal (ok (Clock.issue m now None (Some far))).Clock None "a missing clock stays missing"

        testCase "drift: observe" <| fun _ ->
            let now = T
            let own : Clock = { ClockMs = now; ClockCounter = 4 }
            let edge = Clock.observe now (Some own) (stamp (now + driftLimitMs) 2 "0000000a")
            Expect.isFalse edge.FarFuture "at the limit"
            Expect.equal edge.Clock (Some { ClockMs = now + driftLimitMs; ClockCounter = 2 }) "adopted"
            let far = Clock.observe now (Some own) (stamp (now + driftLimitMs + 1L) 2 "0000000a")
            Expect.isTrue far.FarFuture "past the limit"
            Expect.equal far.Clock (Some own) "unchanged"
            Expect.equal (Clock.observe now None (stamp (now + driftLimitMs + 1L) 2 "0000000a")).Clock None "stays missing"
            // an older stamp never moves the clock back
            let older = Clock.observe now (Some own) (stamp (now - 10L) 9 "0000000a")
            Expect.equal older.Clock (Some own) "not backwards"
            // an equal time with a higher counter moves it up
            let up = Clock.observe now (Some own) (stamp now 6 "0000000a")
            Expect.equal up.Clock (Some { ClockMs = now; ClockCounter = 6 }) "counter moves up"

        testCase "guardStartup" <| fun _ ->
            let year = 365L * 86_400_000L
            let ahead = Clock.guardStartup T (Some { ClockMs = T + year; ClockCounter = 5 })
            Expect.isTrue ahead.Reset "a year ahead is reset"
            Expect.equal ahead.Clock (Some { ClockMs = T; ClockCounter = 0 }) "to the wall clock"
            let edge : Clock = { ClockMs = T + driftLimitMs; ClockCounter = 5 }
            let same = Clock.guardStartup T (Some edge)
            Expect.isFalse same.Reset "exactly 24 hours ahead is left alone"
            Expect.equal same.Clock (Some edge) "unchanged"
            Expect.equal (Clock.guardStartup T None) { Clock = None; Reset = false } "no clock, no change"

        testProperty "serialize and tryDeserialize round-trip" <|
            Prop.forAll (Arb.fromGen clockGen) (fun c -> Clock.tryDeserialize (Clock.serialize c) = Some c)

        testCase "tryDeserialize rejects malformed text" <| fun _ ->
            for bad in [ ""; "12"; "-1.0"; "1.1000000"; "1. 2"; " 1.2"; "1.2 "; "1.-2"; "+1.2"; "01.2"; "1.02"; "1.2.3"; ".2"; "1."
                         "253402300800000.0"; "1.\uFF12" ] do
                Expect.isNone (Clock.tryDeserialize bad) (sprintf "should reject %A" bad)
            Expect.isNone (Clock.tryDeserialize null) "null"
            Expect.equal (Clock.tryDeserialize "0.0") (Some { ClockMs = 0L; ClockCounter = 0 }) "zero"
            Expect.equal (Clock.tryDeserialize "253402300799999.999999") (Some { ClockMs = maxTimeMs; ClockCounter = 999_999 }) "top"
            Expect.isNone (Clock.tryDeserialize "1.1000000") "counter above the maximum"
    ]
