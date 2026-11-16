module TotalRecall.Core.Tests.GitSyncConfigTests

open Expecto
open FsCheck
open TotalRecall.Core.Config
open TotalRecall.Core.Config.GitSyncSettings

let private absent : RawGitSync =
    { RemoteRaw = RawAbsent; BranchRaw = RawAbsent; IntervalRaw = RawAbsent; SessionStartRaw = RawAbsent
      SoftRaw = RawAbsent; MaxRaw = RawAbsent; StallRaw = RawAbsent; SessionEndRaw = RawAbsent
      SessionEndSecondsRaw = RawAbsent; DeleteGuardRaw = RawAbsent }

let private norm (r: RawGitSync) : GitSyncConfig = normalise r
let private remote (v: GitRawValue) : GitSyncConfig = norm { absent with RemoteRaw = v }
let private branch (v: GitRawValue) : GitSyncConfig = norm { absent with BranchRaw = v }
let private interval (v: GitRawValue) : GitSyncConfig = norm { absent with IntervalRaw = v }

let private hasWarningFor (key: string) (c: GitSyncConfig) =
    c.Warnings |> List.exists (fun w -> w.StartsWith("git_sync." + key + ":"))

let private long (n: int) = RawInt (int64 n)

[<Tests>]
let defaultTests =
    testList "GitSyncSettings defaults and valid values" [
        testCase "an empty section gives the documented defaults and no warnings" <| fun _ ->
            let expected : GitSyncConfig =
                { Remote = None; Branch = Some "main"; IntervalSeconds = 300; SessionStart = Blocking
                  SessionStartSoftSeconds = 10; SessionStartMaxSeconds = 20; SessionStartStallSeconds = 8
                  SessionEnd = true; SessionEndSeconds = 8; DeleteGuardPercent = 20; Warnings = [] }
            Expect.equal (norm absent) expected "defaults"
            Expect.equal (norm absent) defaults "defaults value"

        testCase "every key with a valid non-default value is returned as written" <| fun _ ->
            let r : RawGitSync =
                { RemoteRaw = RawStr "git@github.com:alice/total-recall-sync.git"; BranchRaw = RawStr "dev/sync-1.x"
                  IntervalRaw = long 60; SessionStartRaw = RawStr "background"; SoftRaw = long 5; MaxRaw = long 15
                  StallRaw = long 3; SessionEndRaw = RawBool false; SessionEndSecondsRaw = long 12; DeleteGuardRaw = long 50 }
            let c = norm r
            Expect.equal c.Remote (Some "git@github.com:alice/total-recall-sync.git") "remote"
            Expect.equal c.Branch (Some "dev/sync-1.x") "branch"
            Expect.equal (c.IntervalSeconds, c.SessionStart) (60, Background) "interval, session start"
            Expect.equal (c.SessionStartSoftSeconds, c.SessionStartMaxSeconds, c.SessionStartStallSeconds) (5, 15, 3) "limits"
            Expect.equal (c.SessionEnd, c.SessionEndSeconds, c.DeleteGuardPercent) (false, 12, 50) "end and guard"
            Expect.isEmpty c.Warnings "no warnings"

        testCase "an https remote is accepted and a remote is trimmed" <| fun _ ->
            Expect.equal (remote (RawStr "https://github.com/alice/x.git")).Remote (Some "https://github.com/alice/x.git") "https"
            Expect.equal (remote (RawStr "  git@h:o/r.git \n")).Remote (Some "git@h:o/r.git") "trimmed"
            Expect.equal (remote (RawStr "\u00a0h:o/r.git\u2028")).Remote (Some "h:o/r.git") "non-ASCII whitespace at the ends is trimmed"
            Expect.equal (remote (RawStr null)).Remote None "a null string is an empty string"
            Expect.equal (branch (RawStr null)).Branch None "a null branch is not usable"
    ]

[<Tests>]
let remoteTests =
    testList "GitSyncSettings remote" [
        testCase "an empty or blank remote is not set, without a warning" <| fun _ ->
            for v in [ ""; "   "; "\t\n" ] do
                let c = remote (RawStr v)
                Expect.isNone c.Remote "not set"
                Expect.isEmpty c.Warnings "no warning"

        testCase "a remote that cannot be used is not set, with a warning" <| fun _ ->
            for v in [ "-oProxyCommand=x"; "a b"; "a\tb"; "a\u0000b"; "\u007fx"; "-"; "a\u00a0b"; "a\u2028b" ] do
                let c = remote (RawStr v)
                Expect.isNone c.Remote (sprintf "%A" v)
                Expect.equal c.Warnings [ "git_sync.remote: not a usable repository address; treated as not set" ] (sprintf "%A" v)

        testCase "a remote of the wrong type is not set, with a warning" <| fun _ ->
            Expect.equal (remote (RawInt 5L)).Warnings [ "git_sync.remote: expected a string, got an integer; treated as not set" ] "int"
            Expect.equal (remote (RawBad "an array")).Warnings [ "git_sync.remote: expected a string, got an array; treated as not set" ] "array"
            Expect.equal (remote (RawBool true)).Remote None "bool"

        testCase "a warning never echoes the remote (it may carry credentials)" <| fun _ ->
            let c = remote (RawStr "https://user:secret-token@host/x y")
            Expect.isNone c.Remote "unusable"
            Expect.isNonEmpty c.Warnings "a warning"
            for w in c.Warnings do
                Expect.isFalse (w.Contains "secret-token" || w.Contains "user") w
    ]

[<Tests>]
let branchTests =
    testList "GitSyncSettings branch" [
        testCase "an absent branch is main" <| fun _ ->
            Expect.equal (branch RawAbsent).Branch (Some "main") "main"

        testCase "valid branch names are kept" <| fun _ ->
            for b in [ "main"; "dev/sync-1.x"; "a"; "A1"; "x_y"; "feature/a-b.c"; String.replicate 100 "a" ] do
                let c = branch (RawStr b)
                Expect.equal c.Branch (Some b) b
                Expect.isEmpty c.Warnings "no warning"

        testCase "a branch named HEAD is refused, names that only contain it are kept" <| fun _ ->
            let c = branch (RawStr "HEAD")
            Expect.isNone c.Branch "HEAD"
            Expect.equal c.Warnings [ "git_sync.branch: not a usable branch name; treated as not set" ] "one warning"
            for b in [ "a/HEAD"; "HEAD/a"; "head"; "HEADS"; "HEAD1" ] do
                Expect.equal (branch (RawStr b)).Branch (Some b) b

        testCase "invalid branch names are not set, never main, with a warning" <| fun _ ->
            let bad =
                [ ""; "-x"; "a b"; "a..b"; "a//b"; "a/"; "a."; "x.lock"; "a.lock/b"; "feature/.x"; ".x"; "_x"; "\u00e9"
                  String.replicate 101 "a"; "a\nb" ]
            for b in bad do
                let c = branch (RawStr b)
                Expect.isNone c.Branch (sprintf "%A" b)
                Expect.equal c.Warnings [ "git_sync.branch: not a usable branch name; treated as not set" ] (sprintf "%A" b)

        testCase "a branch of the wrong type is not set" <| fun _ ->
            let c = branch (RawInt 1L)
            Expect.isNone c.Branch "int"
            Expect.equal c.Warnings [ "git_sync.branch: expected a string, got an integer; treated as not set" ] "warning"
    ]

[<Tests>]
let numberTests =
    testList "GitSyncSettings numbers and words" [
        testCase "interval: 0 disables the timer without a warning" <| fun _ ->
            let c = interval (RawInt 0L)
            Expect.equal c.IntervalSeconds 0 "disabled"
            Expect.isEmpty c.Warnings "no warning"

        testCase "interval: 1 to 29 is raised to 30 with a warning" <| fun _ ->
            for n in [ 1; 29 ] do
                let c = interval (long n)
                Expect.equal c.IntervalSeconds 30 "raised"
                Expect.equal c.Warnings [ sprintf "git_sync.interval_seconds: %d is below the minimum of 30; using 30" n ] "warning"

        testCase "interval: 30, 300 and 86400 are kept" <| fun _ ->
            for n in [ 30; 300; 86400 ] do
                let c = interval (long n)
                Expect.equal c.IntervalSeconds n "kept"
                Expect.isEmpty c.Warnings "no warning"

        testCase "interval: negative and too large take the default with a warning" <| fun _ ->
            for n in [ -1L; 86401L ] do
                let c = interval (RawInt n)
                Expect.equal c.IntervalSeconds 300 "default"
                Expect.equal c.Warnings [ sprintf "git_sync.interval_seconds: %d is outside 0 or 30 to 86400; using 300" n ] "warning"

        testCase "interval: wrong types take the default with a warning" <| fun _ ->
            for v, t in [ RawStr "5", "a string"; RawBool true, "a boolean"; RawBad "a float", "a float" ] do
                let c = interval v
                Expect.equal c.IntervalSeconds 300 "default"
                Expect.equal c.Warnings [ sprintf "git_sync.interval_seconds: expected an integer, got %s; using 300" t ] "warning"

        testCase "session_start accepts the three words, case-sensitively" <| fun _ ->
            let s (v: GitRawValue) = (norm { absent with SessionStartRaw = v }).SessionStart
            Expect.equal (s (RawStr "blocking")) Blocking "blocking"
            Expect.equal (s (RawStr "background")) Background "background"
            Expect.equal (s (RawStr "off")) Off "off"
            let bad = norm { absent with SessionStartRaw = RawStr "Blocking" }
            Expect.equal bad.SessionStart Blocking "default"
            Expect.equal bad.Warnings [ "git_sync.session_start: not one of \"blocking\", \"background\", \"off\"; using \"blocking\"" ] "warning"
            Expect.equal (norm { absent with SessionStartRaw = RawInt 3L }).Warnings [ "git_sync.session_start: expected a string, got an integer; using \"blocking\"" ] "type"

        testCase "the three session limits and the session-end seconds: 1 to 3600" <| fun _ ->
            let cases : (string * (GitRawValue -> RawGitSync) * (GitSyncConfig -> int) * int) list =
                [ "session_start_soft_seconds", (fun v -> { absent with SoftRaw = v; MaxRaw = long 3600 }), (fun c -> c.SessionStartSoftSeconds), 10
                  "session_start_max_seconds", (fun v -> { absent with MaxRaw = v }), (fun c -> c.SessionStartMaxSeconds), 20
                  "session_start_stall_seconds", (fun v -> { absent with StallRaw = v }), (fun c -> c.SessionStartStallSeconds), 8
                  "session_end_seconds", (fun v -> { absent with SessionEndSecondsRaw = v }), (fun c -> c.SessionEndSeconds), 8 ]
            for key, make, get, dflt in cases do
                for n in [ 0L; 3601L ] do
                    let c = norm (make (RawInt n))
                    Expect.equal (get c) dflt (key + " default")
                    Expect.equal c.Warnings [ sprintf "git_sync.%s: %d is outside 1 to 3600; using %d" key n dflt ] (key + " warning")
                for n in [ 1; 3600 ] do
                    let c = norm (make (long n))
                    Expect.equal (get c) n (key + " kept")
                    Expect.isFalse (hasWarningFor key c) (key + " no warning")
                let t = norm (make (RawStr "x"))
                Expect.equal (get t) dflt (key + " wrong type")
                Expect.isTrue (hasWarningFor key t) (key + " type warning")

        testCase "delete_guard_percent: 0 to 100" <| fun _ ->
            let g (v: GitRawValue) = norm { absent with DeleteGuardRaw = v }
            for n in [ -1L; 101L ] do
                Expect.equal (g (RawInt n)).DeleteGuardPercent 20 "default"
                Expect.equal (g (RawInt n)).Warnings [ sprintf "git_sync.delete_guard_percent: %d is outside 0 to 100; using 20" n ] "warning"
            for n in [ 0; 100 ] do
                Expect.equal (g (long n)).DeleteGuardPercent n "kept"
                Expect.isEmpty (g (long n)).Warnings "no warning"

        testCase "session_end keeps a boolean and defaults otherwise" <| fun _ ->
            Expect.equal (norm { absent with SessionEndRaw = RawBool false }).SessionEnd false "false"
            let c = norm { absent with SessionEndRaw = RawStr "yes" }
            Expect.isTrue c.SessionEnd "default"
            Expect.equal c.Warnings [ "git_sync.session_end: expected a boolean, got a string; using true" ] "warning"
    ]

[<Tests>]
let softAboveMaxTests =
    testList "GitSyncSettings soft above max" [
        testCase "an explicit valid soft above max is lowered with a warning" <| fun _ ->
            let c = norm { absent with SoftRaw = long 30; MaxRaw = long 20 }
            Expect.equal (c.SessionStartSoftSeconds, c.SessionStartMaxSeconds) (20, 20) "lowered"
            Expect.equal c.Warnings [ "git_sync.session_start_soft_seconds: 30 is above session_start_max_seconds; using 20" ] "warning"

        testCase "an absent soft with a small max is lowered silently" <| fun _ ->
            let c = norm { absent with MaxRaw = long 5 }
            Expect.equal c.SessionStartSoftSeconds 5 "lowered"
            Expect.isEmpty c.Warnings "silent"

        testCase "a bad soft with a small max gets only its own warning, naming the value used" <| fun _ ->
            let c = norm { absent with SoftRaw = RawStr "x"; MaxRaw = long 5 }
            Expect.equal c.SessionStartSoftSeconds 5 "lowered"
            Expect.equal c.Warnings [ "git_sync.session_start_soft_seconds: expected an integer, got a string; using 5" ] "one warning"

        testCase "soft equal to max is unchanged" <| fun _ ->
            let c = norm { absent with SoftRaw = long 5; MaxRaw = long 5 }
            Expect.equal c.SessionStartSoftSeconds 5 "same"
            Expect.isEmpty c.Warnings "none"
    ]

[<Tests>]
let warningOrderTests =
    testList "GitSyncSettings warnings" [
        testCase "several bad keys give one line each, in the table's key order, soft-above-max at the soft slot" <| fun _ ->
            let r : RawGitSync =
                { RemoteRaw = RawStr "a b"; BranchRaw = RawStr "-x"; IntervalRaw = long 5; SessionStartRaw = RawStr "x"
                  SoftRaw = long 30; MaxRaw = long 20; StallRaw = long 0; SessionEndRaw = RawStr "x"
                  SessionEndSecondsRaw = long 0; DeleteGuardRaw = long 101 }
            let keys = (norm r).Warnings |> List.map (fun w -> w.Substring(0, w.IndexOf ':'))
            Expect.equal keys
                [ "git_sync.remote"; "git_sync.branch"; "git_sync.interval_seconds"; "git_sync.session_start"
                  "git_sync.session_start_soft_seconds"; "git_sync.session_start_stall_seconds"; "git_sync.session_end"
                  "git_sync.session_end_seconds"; "git_sync.delete_guard_percent" ] "order"
            for w in (norm r).Warnings do
                Expect.isFalse (w.Contains "\n") "one line"

        testCase "a bad max sits between soft and stall in the order" <| fun _ ->
            let r : RawGitSync = { absent with SoftRaw = long 0; MaxRaw = long 0; StallRaw = long 0 }
            let keys = (norm r).Warnings |> List.map (fun w -> w.Substring(0, w.IndexOf ':'))
            Expect.equal keys [ "git_sync.session_start_soft_seconds"; "git_sync.session_start_max_seconds"; "git_sync.session_start_stall_seconds" ] "order"
    ]

// ------------------------------------------------------------------------------------------------
// Totality: any raw input gives a usable configuration

let private genRaw : Gen<GitRawValue> =
    let strings =
        Gen.oneof
            [ Arb.generate<string> |> Gen.map (fun s -> if isNull s then "" else s)
              Gen.elements [ ""; " "; "main"; "git@h:o/r.git"; "-x"; "a b"; "a\u0000"; "a..b"; "x.lock"; "dev/x"; "blocking"; "off"; "Blocking"; String.replicate 150 "a" ] ]
    Gen.oneof
        [ Gen.constant RawAbsent
          Gen.constant (RawBad "a float")
          strings |> Gen.map RawStr
          Gen.elements [ 0L; 1L; 29L; 30L; 86400L; 86401L; 3600L; 3601L; 100L; 101L; -1L; System.Int64.MinValue; System.Int64.MaxValue ] |> Gen.map RawInt
          Arb.generate<int64> |> Gen.map RawInt
          Arb.generate<bool> |> Gen.map RawBool ]

let private genRawGitSync : Gen<RawGitSync> =
    gen {
        let! a = genRaw
        let! b = genRaw
        let! c = genRaw
        let! d = genRaw
        let! e = genRaw
        let! f = genRaw
        let! g = genRaw
        let! h = genRaw
        let! i = genRaw
        let! j = genRaw
        return
            { RemoteRaw = a; BranchRaw = b; IntervalRaw = c; SessionStartRaw = d; SoftRaw = e; MaxRaw = f
              StallRaw = g; SessionEndRaw = h; SessionEndSecondsRaw = i; DeleteGuardRaw = j }
    }

let private inRange (lo: int) (hi: int) (n: int) = n >= lo && n <= hi

/// Strings that are mostly near-valid names, so both outcomes of the rules are exercised.
let private genText : Gen<string> =
    let alphabet = [| 'a'; 'b'; 'Z'; '0'; '.'; '_'; '/'; '-'; ' '; '\t'; '\u00a0'; '\u0000'; '\u00e9'; ':'; '@' |]
    Gen.oneof
        [ Gen.elements [ ""; "main"; "dev/sync-1.x"; "a.lock"; "feature/.x"; "-x"; "a b"; "git@h:o/r.git"; " h:o/r.git " ]
          Gen.listOfLength 3 (Gen.elements alphabet) |> Gen.map (fun cs -> System.String(List.toArray cs))
          Gen.choose (1, 12) |> Gen.map (fun n -> System.String('a', n))
          Gen.sized (fun n -> Gen.listOfLength (min n 12) (Gen.elements alphabet)) |> Gen.map (fun cs -> System.String(List.toArray cs)) ]

[<Tests>]
let totalityTests =
    testList "GitSyncSettings totality" [
        testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 2000 }
            "normalise never throws and every field is usable"
            (Prop.forAll (Arb.fromGen genRawGitSync) (fun r ->
                let c = norm r
                (c.IntervalSeconds = 0 || inRange 30 86400 c.IntervalSeconds)
                && inRange 1 3600 c.SessionStartSoftSeconds && inRange 1 3600 c.SessionStartMaxSeconds
                && inRange 1 3600 c.SessionStartStallSeconds && inRange 1 3600 c.SessionEndSeconds
                && c.SessionStartSoftSeconds <= c.SessionStartMaxSeconds
                && inRange 0 100 c.DeleteGuardPercent
                && c.Warnings |> List.forall (fun w -> not (w.Contains "\n"))))

        // An independent statement of the spec's rules (not the predicates normalise uses), asserted both ways.
        testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 2000 }
            "remote and branch follow the spec's rules in both directions"
            (Prop.forAll (Arb.fromGen genText) (fun s ->
                let branchOk =
                    s <> "HEAD"
                    && System.Text.RegularExpressions.Regex.IsMatch(s, "^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$")
                    && not (s.Contains "..") && not (s.Contains "//") && not (s.EndsWith "/") && not (s.EndsWith ".")
                    && s.Split('/') |> Array.forall (fun p -> not (p.StartsWith ".") && not (p.EndsWith ".lock"))
                let t = s.Trim()
                let remoteOk = t <> "" && not (t.StartsWith "-") && not (t |> Seq.exists (fun c -> System.Char.IsWhiteSpace c || System.Char.IsControl c))
                (branch (RawStr s)).Branch = (if branchOk then Some s else None)
                && (remote (RawStr s)).Remote = (if remoteOk then Some t else None)))
    ]
