module TotalRecall.Core.Tests.SyncIdentityTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open FsCheck
open TotalRecall.Core.SyncIdentity

let private joinedInput =
    { ConfiguredRemote = Some "git@github.com:o/r.git"
      Stored = { Remote = Some "git@github.com:o/r.git"; RepoId = Some "rid"; Host = Some "h1" }
      CurrentHost = Some "h1"
      ManifestRepoId = Some "rid" }

let private notJoined reason = NotJoined reason

[<Tests>]
let tests =
    testList "SyncIdentity" [
        testCase "all inputs agreeing is joined" <| fun _ ->
            Expect.equal (evaluate joinedInput) Joined "joined"

        testCase "no usable remote is NotConfigured" <| fun _ ->
            Expect.equal (evaluate { joinedInput with ConfiguredRemote = None }) (notJoined NotConfigured) "not configured"

        testCase "no stored remote, or no stored repo id, is NeverJoined" <| fun _ ->
            Expect.equal (evaluate { joinedInput with Stored = { joinedInput.Stored with Remote = None } }) (notJoined NeverJoined) "no remote"
            Expect.equal (evaluate { joinedInput with Stored = { joinedInput.Stored with RepoId = None } }) (notJoined NeverJoined) "no repo id"

        testCase "a remote that differs in any way (case, trailing slash) is RemoteChanged" <| fun _ ->
            for other in [ "git@github.com:O/r.git"; "git@github.com:o/r.git/"; "https://github.com/o/r.git" ] do
                Expect.equal (evaluate { joinedInput with ConfiguredRemote = Some other }) (notJoined RemoteChanged) other

        testCase "a host fingerprint that differs, or is absent while one is known, is DataDirectoryMoved" <| fun _ ->
            Expect.equal (evaluate { joinedInput with CurrentHost = Some "h2" }) (notJoined DataDirectoryMoved) "differs"
            Expect.equal (evaluate { joinedInput with Stored = { joinedInput.Stored with Host = None } }) (notJoined DataDirectoryMoved) "absent"

        testCase "an unavailable current host skips the host check" <| fun _ ->
            Expect.equal (evaluate { joinedInput with CurrentHost = None }) Joined "skipped"
            Expect.equal
                (evaluate { joinedInput with CurrentHost = None; Stored = { joinedInput.Stored with Host = None } })
                Joined "skipped, nothing stored"

        testCase "a manifest repo id that differs is RepositoryReplaced; an unknown one is skipped" <| fun _ ->
            Expect.equal (evaluate { joinedInput with ManifestRepoId = Some "other" }) (notJoined RepositoryReplaced) "differs"
            Expect.equal (evaluate { joinedInput with ManifestRepoId = None }) Joined "unknown"

        testCase "a case failing two checks reports the earlier one" <| fun _ ->
            let input = { joinedInput with ConfiguredRemote = Some "x"; CurrentHost = Some "h2"; ManifestRepoId = Some "other" }
            Expect.equal (evaluate input) (notJoined RemoteChanged) "remote first"
            let input2 = { joinedInput with CurrentHost = Some "h2"; ManifestRepoId = Some "other" }
            Expect.equal (evaluate input2) (notJoined DataDirectoryMoved) "host before repo id"

        testCase "evaluate is total, and with a known host joined needs every input to agree (property)" <| fun _ ->
            let text = Gen.elements [ "a"; "b"; "c" ]
            let opt g = Gen.oneof [ gen { return None }; gen { let! v = g in return Some v } ]
            let gen =
                gen {
                    let! cfg = opt text
                    let! r = opt text
                    let! rid = opt text
                    let! h = opt text
                    let! cur = opt text
                    let! man = opt text
                    return { ConfiguredRemote = cfg; Stored = { Remote = r; RepoId = rid; Host = h }; CurrentHost = cur; ManifestRepoId = man }
                }
            Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen gen) (fun input ->
                match evaluate input with
                | Joined ->
                    input.ConfiguredRemote.IsSome
                    && input.ConfiguredRemote = input.Stored.Remote
                    && input.Stored.RepoId.IsSome
                    && (input.CurrentHost.IsNone || input.CurrentHost = input.Stored.Host)
                    && (input.ManifestRepoId.IsNone || input.ManifestRepoId = input.Stored.RepoId)
                | NotJoined _ -> true))

        testCase "agreeing inputs are joined, also when the host or the manifest id is unknown (property)" <| fun _ ->
            let text = Gen.elements [ "a"; "b"; "c" ]
            Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen (Gen.zip3 text text text)) (fun (remote, rid, host) ->
                let input =
                    { ConfiguredRemote = Some remote
                      Stored = { Remote = Some remote; RepoId = Some rid; Host = Some host }
                      CurrentHost = Some host
                      ManifestRepoId = Some rid }
                evaluate input = Joined
                && evaluate { input with CurrentHost = None } = Joined
                && evaluate { input with ManifestRepoId = None } = Joined))

        testCase "reasonText wording" <| fun _ ->
            Expect.equal (reasonText DataDirectoryMoved) "this data directory moved or was copied: run `total-recall sync join`" "moved"
            Expect.stringContains (reasonText NeverJoined) "sync init" "never"
            Expect.stringContains (reasonText RemoteChanged) "[git_sync] remote differs" "remote"
            Expect.stringContains (reasonText RepositoryReplaced) "recreated" "replaced"
            Expect.stringContains (reasonText NotConfigured) "no [git_sync] remote" "not configured"

        testCase "hostFingerprint is the first 32 hex digits of the SHA-256 of the fixed text" <| fun _ ->
            let expected =
                use sha = SHA256.Create()
                Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes "total-recall/host/v1\nm\n/a/db")).Substring(0, 32).ToLowerInvariant()
            Expect.equal (hostFingerprint "m" "/a/db") expected "value"
            Expect.equal (hostFingerprint "m" "/a/db").Length 32 "length"

        testCase "hostFingerprint differs for a different machine id or path, and allows an empty machine id" <| fun _ ->
            let a = hostFingerprint "m" "/a/db"
            Expect.notEqual a (hostFingerprint "n" "/a/db") "machine id"
            Expect.notEqual a (hostFingerprint "m" "/b/db") "path"
            Expect.notEqual (hostFingerprint "" "/a/db") a "empty id"
    ]
