module TotalRecall.Core.Tests.SyncPublicTests

open Expecto
open TotalRecall.Core.SyncPublic

let private or' = { Owner = "o"; Repo = "r" }

[<Tests>]
let tests =
    testList "SyncPublic" [
        testCase "parseGitHubSlug accepts the GitHub shapes of decision 7" <| fun _ ->
            for remote in [ "https://github.com/o/r"; "https://github.com/o/r.git"; "https://github.com/o/r/"
                            "http://GitHub.com/o/r"; "git://github.com/o/r.git"; "ssh://git@github.com/o/r.git"
                            "ssh://git@github.com:22/o/r.git"; "git+ssh://git@github.com/o/r"; "ssh+git://git@github.com/o/r"
                            "git@github.com:o/r.git"; "https://user:tok3n@github.com/o/r"; "HTTPS://github.com/o/r"; "https://github.com:/o/r" ] do
                Expect.equal (parseGitHubSlug remote) (Some or') remote

        testCase "parseGitHubSlug refuses every other shape" <| fun _ ->
            for remote in [ "https://github.com@evil.com/o/r"; "https://evil@github.com.evil.com/o/r"; "git@work-github:o/r.git"
                            "https://gitlab.com/o/r"; "/srv/git/r"; "C:\\r"; "https://github.com/o"; "https://github.com/o/r/extra"
                            "https://github.com/o/.git"; "https://github.com/o/.."; "https://github.com/o/."; "https://github.com//r"
                            "https://github.com/o/r%"; "https://ssh.github.com/o/r"; ""; "   "; "ftp://github.com/o/r"
                            "https://github.com:abc/o/r"; "https://github.com:4a/o/r"; "https://github.com"; "github.com/o/r"
                            "https://evil.com#@github.com/o/r"; "git://evil.com#@github.com/o/r"; "https://evil.com?x@github.com/o/r"
                            "https://evil.com\\@github.com/o/r"; "https://evil.com\t@github.com/o/r"; "git@github.com%40evil.com:o/r.git" ] do
                Expect.equal (parseGitHubSlug remote) None remote

        testCase "ofStatus maps 200, 404 and everything else" <| fun _ ->
            Expect.equal (ofStatus (Some 200)) Public "200"
            Expect.equal (ofStatus (Some 404)) Private "404"
            for status in [ Some 301; Some 401; Some 403; Some 429; Some 500; None ] do
                match ofStatus status with
                | Unknown reason -> Expect.isNotEmpty reason (sprintf "%A" status)
                | other -> failtestf "%A gave %A" status other

        testCase "slugKey is 16 lowercase hex digits, and case-insensitive on the slug" <| fun _ ->
            let key = slugKey or'
            Expect.equal key.Length 16 "length"
            Expect.isTrue (key |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) "hex"
            Expect.equal (slugKey { Owner = "O"; Repo = "R" }) key "case"
            Expect.notEqual (slugKey { Owner = "o"; Repo = "r2" }) key "different repo"

        testCase "formatCache and parseCache round trip; anything else does not parse" <| fun _ ->
            let value = formatCache or' 1700000000L
            Expect.equal value (sprintf "v1|%s|1700000000" (slugKey or')) "format"
            Expect.equal (parseCache value) (Some(slugKey or', 1700000000L)) "round trip"
            for bad in [ ""; "v2|abc|1"; "v1|abc"; "v1|abc|x"; "v1||1"; "v1|abc|-1"; "v1|abc|1|2" ] do
                Expect.equal (parseCache bad) None bad

        testCase "isFresh: 24 hours exactly is fresh, one second more is not" <| fun _ ->
            Expect.isTrue (isFresh 100000L (100000L - 86400L)) "24 h"
            Expect.isFalse (isFresh 100000L (100000L - 86401L)) "24 h + 1 s"

        testCase "isFresh: 4 minutes in the future is used, 6 minutes is ignored" <| fun _ ->
            Expect.isTrue (isFresh 100000L (100000L + 240L)) "4 min"
            Expect.isTrue (isFresh 100000L (100000L + 300L)) "5 min"
            Expect.isFalse (isFresh 100000L (100000L + 301L)) "5 min + 1 s"
            Expect.isFalse (isFresh 100000L (100000L + 360L)) "6 min"

        testCase "the notices are not empty, hold no address, and the skipped one says to keep the repository private" <| fun _ ->
            let unknown = unknownNotice "GitHub answered 403"
            let skipped = skippedNotice "not a github.com address"
            Expect.isNotEmpty unknown "unknown"
            Expect.stringContains skipped "private" "skipped"
            Expect.isFalse (unknown.Contains "://" || skipped.Contains "://") "no URL"
    ]
