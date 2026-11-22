module TotalRecall.Core.Tests.SyncSetupTests

open Expecto
open FsCheck
open TotalRecall.Core
open TotalRecall.Core.SyncSetup

let private allRefusals =
    [ NoGit; GitTooOld; WrongMode; BadRemote; BadBranch; PublicRepo; RepoExists; NotEmpty; NothingToJoin; NotASyncRepo
      InvalidManifest; FormatTooNew; HostUnverified; Busy; NeedsAuth; Unreachable; Failed; NotBlocked; UnknownEntry
      UnreadableEntry; ChangedSinceBlocked ]

[<Tests>]
let refusalTests =
    testList "SyncSetup refusals and mode" [
        testCase "refusal names are unique kebab-case and the documented ones" <| fun _ ->
            let names = allRefusals |> List.map refusalName
            Expect.equal (List.distinct names).Length names.Length "unique"
            for n in names do
                Expect.isTrue (System.Text.RegularExpressions.Regex.IsMatch(n, "^[a-z]+(-[a-z]+)*$")) n
            Expect.equal
                names
                [ "no-git"; "git-too-old"; "wrong-mode"; "bad-remote"; "bad-branch"; "public-repo"; "repo-exists"; "not-empty"
                  "nothing-to-join"; "not-a-sync-repo"; "invalid-manifest"; "format-too-new"; "host-unverified"; "busy"
                  "needs-auth"; "unreachable"; "failed"; "not-blocked"; "unknown-entry"; "unreadable-entry"
                  "changed-since-blocked" ]
                "names"

        testCase "effectiveMode reduces like the server does and keeps the text as written" <| fun _ ->
            Expect.equal (effectiveMode None None) "local" "none"
            Expect.equal (effectiveMode (Some "git") None) "git" "git"
            Expect.equal (effectiveMode None (Some "Host=x")) "postgres" "connection string only"
            Expect.equal (effectiveMode (Some "GIT") None) "GIT" "case kept"
            Expect.equal (effectiveMode (Some "cortex") (Some "x")) "cortex" "mode wins"
            Expect.equal (effectiveMode (Some "  ") None) "  " "a blank mode is kept, as the server keeps it"
            Expect.equal (effectiveMode (Some "  ") (Some "Host=x")) "  " "a blank mode still wins"
            Expect.equal (effectiveMode None (Some " ")) "postgres" "a connection string, as the server reads it"
            Expect.isError (modeVerdict "  ") "a blank mode is refused"

        testCase "modeVerdict accepts only exactly local and git and names the mode otherwise" <| fun _ ->
            Expect.equal (modeVerdict "local") (Ok()) "local"
            Expect.equal (modeVerdict "git") (Ok()) "git"
            for m in [ "cortex"; "postgres"; "GIT"; "Local"; "foo"; "" ] do
                match modeVerdict m with
                | Error e ->
                    Expect.stringContains e (sprintf "'%s'" m) "names the mode"
                    Expect.stringContains e "storage.mode = \"local\"" "says what to do"
                | Ok() -> failtestf "%s should be refused" m
    ]

[<Tests>]
let addressTests =
    testList "SyncSetup addresses" [
        testCase "AddressTable: refused addresses" <| fun _ ->
            for a in [ "https://token@host/o/r"; "https://user:pass@host/o/r"; "http://u@h/x"; "git://u@h/x"
                       "file://tok:pw@host/tmp/x.git"; "file://tok@host/x"; "ssh://git:ghp_x@host/o/r"; "git+ssh://u:pw@h/x"
                       "https://user:p'ss@host/o/r"; "https://user:pa?ss@host/x"; "https://user:pa/ss@host/x"
                       "https://host/o/r?token=x"; "https://host/o/r#frag"; "https://@host/x"; "https://:@host/x"
                       "https://a@b@host/x"; "ssh://host:abc/x"; "ssh://git@host:abc/x"; "https://user:pa]ss/x@h/y"
                       "ssh://u@v:pa/ss@h/x" ] do
                Expect.isSome (addressRefusal a) (sprintf "should refuse %s" a)

        testCase "AddressTable: accepted addresses" <| fun _ ->
            for a in [ "git@host:o/r"; "ssh://git@host/o/r"; "ssh://git@host:2222/o/r"; "https://host/o/repo@v2.git"
                       "host:team/repo@2.git"; "https://host:8080/o/r"; "https://[::1]:8080/o/r"; "ssh://git@[::1]/o/r"
                       "file:///tmp/x"; "file:///C:/x"; "C:/x"; "/abs/path"; "./rel"; "host:o/r:x"; "https://host:/x"
                       "https://[::1]/o/r"; "ssh://git@[::1]:22/o/r"; "" ] do
                Expect.isNone (addressRefusal a) (sprintf "should accept %s" a)

        testCase "addressUserInfo reads the text before the last @ of the authority, or the scp login" <| fun _ ->
            let cases =
                [ "https://token@host/o/r", Some "token"
                  "https://user:pass@host/o/r", Some "user:pass"
                  "https://a@b@host/x", Some "a@b"
                  "https://user:p'ss@host/o/r", Some "user:p'ss"
                  "https://@host/x", Some ""
                  "https://:@host/x", Some ":"
                  "https://user:pa?ss@host/x", None
                  "https://host/o/repo@v2.git", None
                  "ssh://git@host/o/r", Some "git"
                  "ssh://git@[::1]/o/r", Some "git"
                  "git@host:o/r", Some "git"
                  "host:team/repo@2.git", None
                  "user:pw@host:o/r", None
                  "/tmp/a@b", None
                  "C:/x", None
                  "file:///tmp/x", None
                  "", None ]
            for a, expected in cases do
                Expect.equal (addressUserInfo a) expected a

        testCase "displayRemote hides user-info and query" <| fun _ ->
            let cases =
                [ "https://tok@h/x", "https://***@h/x"
                  "https://u:pw@h/x", "https://***@h/x"
                  "https://ghp_AAAA:x-oauth-basic@github.com/o/r", "https://***@github.com/o/r"
                  "ssh://git:pw@h/x", "ssh://***@h/x"
                  "ssh://git@h/x", "ssh://git@h/x"
                  "git@h:o/r", "git@h:o/r"
                  "file://tok@h/x", "file://***@h/x"
                  "HTTPS://tok@h/x", "HTTPS://***@h/x"
                  "ftp://tok@h/x", "ftp://***@h/x"
                  "https://h/o/r?token=x", "https://h/o/r?***"
                  "https://h/o/r#frag", "https://h/o/r#***"
                  "https://user:pa?ss@h/x", "https://***"
                  "https://user:pa/ss@h/x", "https://***"
                  "/tmp/repo", "/tmp/repo"
                  "C:/x", "C:/x"
                  "https://h:8080/o/r", "https://h:8080/o/r"
                  "https://[::1]:8080/o/r", "https://[::1]:8080/o/r" ]
            for a, expected in cases do
                Expect.equal (displayRemote a) expected a

        testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 500 } "displayRemote never shows a secret in the user-info or query" (fun (secret: NonEmptyString) ->
            let s = secret.Get |> String.filter System.Char.IsAsciiLetterOrDigit
            if s.Length < 6 then true
            else
                let secrets = "TOK" + s
                [ sprintf "https://%s@h/x" secrets
                  sprintf "https://u:%s@h/x" secrets
                  sprintf "http://%s:x@h/x" secrets
                  sprintf "file://%s@h/x" secrets
                  sprintf "git://%s@h/x" secrets
                  sprintf "ssh://git:%s@h/x" secrets
                  sprintf "https://h/o/r?token=%s" secrets
                  sprintf "https://h/o/r#%s" secrets
                  sprintf "https://u:%s?x@h/x" secrets
                  sprintf "https://u:%s/x@h/x" secrets
                  sprintf "https://u:%s]x/y@h/z" secrets
                  sprintf "ssh://u@v:%s/x@h/y" secrets ]
                |> List.forall (fun a -> not ((displayRemote a).Contains secrets)))

        testProperty "the address functions are total" <| fun (s: string) ->
            addressUserInfo s |> ignore
            addressRefusal s |> ignore
            displayRemote s |> ignore
            true
    ]

[<Tests>]
let identityTests =
    testList "SyncSetup identity and ids" [
        testCase "isSameRepository needs the same id and the same previous branch" <| fun _ ->
            Expect.isTrue (isSameRepository (Some "a") "a" (Some "main") "main") "same"
            Expect.isFalse (isSameRepository (Some "a") "b" (Some "main") "main") "different id"
            Expect.isFalse (isSameRepository None "a" (Some "main") "main") "nothing stored"
            Expect.isFalse (isSameRepository (Some "a") "a" None "main") "refused previous branch"
            Expect.isFalse (isSameRepository (Some "a") "a" (Some "dev") "main") "different branch"

        testCase "keepMachineId keeps it only with a stored machine and an equal stored host" <| fun _ ->
            Expect.isTrue (keepMachineId (Some "m") (Some "h") "h") "kept"
            Expect.isFalse (keepMachineId (Some "m") (Some "h") "other") "moved"
            Expect.isFalse (keepMachineId None (Some "h") "h") "no machine"
            Expect.isFalse (keepMachineId (Some "m") None "h") "no host"

        testCase "newRepoId and newMachineId are lowercase hex of the right length and deterministic" <| fun _ ->
            let id = newRepoId (Array.init 16 byte)
            Expect.equal id "000102030405060708090a0b0c0d0e0f" "repo id"
            Expect.equal (newRepoId (Array.init 16 byte)) id "deterministic"
            let m = newMachineId [| 0xdeuy; 0xaduy; 0xbeuy; 0xefuy |]
            Expect.equal m "deadbeef" "machine id"
            Expect.isSome (SyncStamp.MachineId.tryCreate m) "usable as a stamp machine id"
            Expect.throws (fun () -> newRepoId (Array.zeroCreate 15) |> ignore) "short repo id"
            Expect.throws (fun () -> newRepoId null |> ignore) "null repo id"
            Expect.throws (fun () -> newMachineId (Array.zeroCreate 5) |> ignore) "long machine id"

        testProperty "any 4 bytes make a usable machine id" <| fun (a: byte) (b: byte) (c: byte) (d: byte) ->
            (SyncStamp.MachineId.tryCreate (newMachineId [| a; b; c; d |])).IsSome
    ]

[<Tests>]
let remoteShapeTests =
    testList "SyncSetup.classifyRefs" [
        testCase "empty, branch present and other refs" <| fun _ ->
            Expect.equal (classifyRefs [] "main") Empty "none"
            Expect.equal (classifyRefs [ "HEAD" ] "main") Empty "only HEAD"
            Expect.equal (classifyRefs [ "HEAD"; "refs/heads/main" ] "main") BranchPresent "branch"
            Expect.equal (classifyRefs [ "refs/heads/dev" ] "main") OtherRefs "other branch"
            Expect.equal (classifyRefs [ "refs/tags/v1" ] "main") OtherRefs "tag"
            Expect.equal (classifyRefs [ "refs/pull/1/head" ] "main") OtherRefs "pull ref"
            Expect.equal (classifyRefs [ "refs/heads/foo/refs/heads/main" ] "main") OtherRefs "suffix is not the branch"
    ]

[<Tests>]
let statusTests =
    testList "SyncSetup.renderStatus" [
        let facts =
            { Mode = "git"
              Git = "2.56.1"
              ConfiguredRemote = Known "https://SECRETTOKEN@h/o/r"
              ConfiguredBranch = Known "main"
              StoredRemote = Known "https://u:SECRETTOKEN@h/o/r"
              RepoId = Known "abc"
              MachineId = Known "01234567"
              Host = Matches
              Join = "joined"
              Clone = "/d/repo" }
        testCase "every label is present, in order, and no secret is printed" <| fun _ ->
            let lines = renderStatus facts
            let labels = lines |> List.map (fun l -> l.Substring(0, l.IndexOf ':'))
            Expect.equal
                labels
                [ "storage mode"; "git"; "configured remote"; "configured branch"; "recorded remote"; "repository id"; "machine id"
                  "host check"; "join state"; "clone"; "last sync" ]
                "labels"
            Expect.isFalse (lines |> List.exists (fun l -> l.Contains "SECRETTOKEN")) "no secret"
            Expect.equal (List.last lines) "last sync: never (this build does not run the sync cycle)" "last line"

        testCase "missing and unknown facts print as such" <| fun _ ->
            let lines =
                renderStatus
                    { facts with
                        ConfiguredRemote = Missing
                        ConfiguredBranch = Missing
                        StoredRemote = Missing
                        RepoId = Unknown "no such table"
                        MachineId = Missing
                        Host = NotRecorded }
            Expect.contains lines "configured remote: not set" "not set"
            Expect.contains lines "configured branch: not set" "branch"
            Expect.contains lines "recorded remote: not recorded" "recorded"
            Expect.contains lines "repository id: unknown (no such table)" "unknown"
            Expect.contains lines "machine id: not recorded" "machine"
            Expect.contains lines "host check: not recorded" "host"

        testCase "the host check words" <| fun _ ->
            for h, w in [ Matches, "matches"; Differs, "differs"; Unavailable, "unavailable"; NotRecorded, "not recorded"; HostUnknown "no such table", "unknown (no such table)" ] do
                Expect.contains (renderStatus { facts with Host = h }) ("host check: " + w) w
    ]
