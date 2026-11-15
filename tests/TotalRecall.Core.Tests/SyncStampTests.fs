module TotalRecall.Core.Tests.SyncStampTests

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
    Arb.generate<int64> |> Gen.map (fun x -> lo + (((x % span) + span) % span))

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
