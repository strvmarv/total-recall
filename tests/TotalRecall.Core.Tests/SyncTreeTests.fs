module TotalRecall.Core.Tests.SyncTreeTests

open System.Text
open Expecto
open FsCheck
open TotalRecall.Core
open TotalRecall.Core.SyncTree

let private sha = String.replicate 40 "a"
let private sha2 = String.replicate 40 "b"
let private zeros = String.replicate 40 "0"
let private goodId = "3f9c2a7e-0000-4000-8000-000000000000"
let private goodPath = "memories/3f/" + goodId + ".json"

let private utf8 (s: string) = Encoding.UTF8.GetBytes s
let private nul = [| 0uy |]
let private cat (parts: byte[] list) = Array.concat parts

/// `<mode> <type> <sha><spaces><size>\t<path>\0`, the size right-aligned the way `ls-tree -l` prints it.
let private lsLine (mode: string) (kind: string) (objectSha: string) (size: string) (path: string) : byte[] =
    cat [ utf8 (sprintf "%s %s %s %7s\t" mode kind objectSha size); utf8 path; nul ]

let private lsLineBytes (mode: string) (kind: string) (objectSha: string) (size: string) (path: byte[]) : byte[] =
    cat [ utf8 (sprintf "%s %s %s %7s\t" mode kind objectSha size); path; nul ]

let private diffRec (oldMode: string) (newMode: string) (o: string) (n: string) (status: string) (path: string) : byte[] =
    cat [ utf8 (sprintf ":%s %s %s %s %s" oldMode newMode o n status); nul; utf8 path; nul ]

let private okValue (r: Result<'a, string>) : 'a =
    match r with
    | Ok v -> v
    | Error e -> failtestf "expected Ok, got Error %s" e

let private isErr (r: Result<'a, string>) = match r with Error _ -> true | Ok _ -> false

[<Tests>]
let lsTreeTests =
    testList "SyncTree.parseLsTree" [
        testCase "two blobs, with different size padding" <| fun _ ->
            let buf = cat [ lsLine "100644" "blob" sha "12" "memories/3f/a.json"; lsLine "100755" "blob" sha2 "1234567" "memories/ab/b.json" ]
            let es = okValue (parseLsTree buf)
            Expect.equal es.Length 2 "two"
            Expect.equal es.[0] { Mode = "100644"; Kind = "blob"; Sha = sha; Size = Some 12L; Path = "memories/3f/a.json"; PathValid = true } "first"
            Expect.equal es.[1].Size (Some 1234567L) "size"
            Expect.equal es.[1].Mode "100755" "mode"

        testCase "paths with spaces, quotes, non-ASCII, a newline and a tab" <| fun _ ->
            for p, valid in [ "memories/3f/a b.json", true; "memories/3f/a\"b.json", true; "memories/3f/é.json", true; "memories/3f/a\nb.json", false; "memories/3f/a\tb.json", false ] do
                let es = okValue (parseLsTree (lsLine "100644" "blob" sha "1" p))
                Expect.equal es.[0].Path p p
                Expect.equal es.[0].PathValid valid (sprintf "valid %A" p)

        testCase "an invalid UTF-8 path is lossy and not valid" <| fun _ ->
            let es = okValue (parseLsTree (lsLineBytes "100644" "blob" sha "1" (Array.append (utf8 "memories/3f/") [| 0xFFuy |])))
            Expect.isFalse es.[0].PathValid "invalid"
            Expect.isTrue (es.[0].Path.StartsWith "memories/3f/") "lossy"

        testCase "a non-blob has no size" <| fun _ ->
            let es = okValue (parseLsTree (lsLine "160000" "commit" sha "-" "memories/sub"))
            Expect.equal es.[0].Size None "dash"
            Expect.equal es.[0].Kind "commit" "kind"

        testCase "SHA-256 object ids are accepted, other lengths are not" <| fun _ ->
            let long = String.replicate 64 "c"
            let es = okValue (parseLsTree (lsLine "100644" "blob" long "1" "memories/3f/a.json"))
            Expect.equal es.[0].Sha long "64 hex"
            for n in [ 39; 41; 63; 65 ] do
                Expect.isTrue (isErr (parseLsTree (lsLine "100644" "blob" (String.replicate n "c") "1" "p"))) (sprintf "%d hex" n)

        testCase "an empty buffer is an empty list" <| fun _ ->
            Expect.equal (okValue (parseLsTree [||])) [] "empty"
            Expect.equal (okValue (parseLsTree null)) [] "null"

        testCase "malformed records are errors that name the record, never exceptions" <| fun _ ->
            let good = lsLine "100644" "blob" sha "1" "memories/3f/a.json"
            let bad =
                [ "no tab", cat [ utf8 ("100644 blob " + sha + " 1 memories/x"); nul ]
                  "bad mode", lsLine "10x644" "blob" sha "1" "p"
                  "short mode", lsLine "1006" "blob" sha "1" "p"
                  "bad sha", lsLine "100644" "blob" "nothex" "1" "p"
                  "upper sha", lsLine "100644" "blob" (String.replicate 40 "A") "1" "p"
                  "bad size", lsLine "100644" "blob" sha "x" "p"
                  "missing nul", Array.take (good.Length - 1) good
                  "too few fields", Array.append (utf8 "100644 blob\tp") nul ]
            for name, b in bad do
                match parseLsTree b with
                | Error m -> if name <> "missing nul" then Expect.isTrue (m.Contains "record 0") (sprintf "%s: %s" name m)
                | Ok _ -> failtestf "%s should be an error" name
            match parseLsTree (Array.append good (lsLine "10x644" "blob" sha "1" "p")) with
            | Error m -> Expect.isTrue (m.Contains "record 1") m
            | Ok _ -> failtest "second record bad"

        testProperty "never throws on arbitrary bytes" <| fun (b: byte[]) ->
            (try parseLsTree b |> ignore; true with _ -> false)
    ]

[<Tests>]
let diffTreeTests =
    testList "SyncTree.parseDiffTree" [
        testCase "M, A, D and T" <| fun _ ->
            let buf =
                cat [ diffRec "100644" "100644" sha sha2 "M" "memories/3f/a.json"
                      diffRec "000000" "100644" zeros sha2 "A" "memories/3f/b.json"
                      diffRec "100644" "000000" sha zeros "D" "memories/3f/c.json"
                      diffRec "100644" "120000" sha sha2 "T" "memories/3f/d.json" ]
            let es = okValue (parseDiffTree buf)
            Expect.equal (es |> List.map (fun e -> e.Status)) [ Modified; Added; Deleted; TypeChanged ] "statuses"
            Expect.equal es.[0] { OldMode = "100644"; NewMode = "100644"; OldSha = sha; NewSha = sha2; Status = Modified; Path = "memories/3f/a.json"; PathValid = true } "first"
            Expect.equal es.[1].OldMode "000000" "added old mode"
            Expect.equal es.[2].NewMode "000000" "deleted new mode"
            Expect.equal es.[3].NewMode "120000" "type changed"

        testCase "PathValid and 64-hex ids come out of the diff parser" <| fun _ ->
            let long = String.replicate 64 "c"
            let es = okValue (parseDiffTree (cat [ diffRec "100644" "100644" long long "M" "memories/3f/a\nb.json"; cat [ utf8 (sprintf ":100644 100644 %s %s M" sha sha2); nul; utf8 "memories/3f/"; [| 0xFFuy |]; nul ] ]))
            Expect.equal es.[0].OldSha long "64 hex"
            Expect.isFalse es.[0].PathValid "newline"
            Expect.isFalse es.[1].PathValid "invalid utf-8"
            Expect.isTrue (isErr (parseDiffTree (diffRec "100644" "100644" (String.replicate 41 "c") sha "M" "p"))) "41 hex"

        testCase "empty buffer" <| fun _ ->
            Expect.equal (okValue (parseDiffTree [||])) [] "empty"
            Expect.equal (okValue (parseDiffTree null)) [] "null"

        testCase "malformed buffers are errors" <| fun _ ->
            let cases =
                [ "no status", cat [ utf8 (sprintf ":100644 100644 %s %s" sha sha2); nul; utf8 "p"; nul ]
                  "rename status", diffRec "100644" "100644" sha sha2 "R100" "p"
                  "copy status", diffRec "100644" "100644" sha sha2 "C" "p"
                  "unmerged", diffRec "100644" "100644" sha sha2 "U" "p"
                  "no path", cat [ utf8 (sprintf ":100644 100644 %s %s M" sha sha2); nul ]
                  "no colon", cat [ utf8 (sprintf "100644 100644 %s %s M" sha sha2); nul; utf8 "p"; nul ]
                  "bad sha", diffRec "100644" "100644" "zz" sha2 "M" "p"
                  "bad mode", diffRec "1006" "100644" sha sha2 "M" "p"
                  "missing nul", Array.take 20 (diffRec "100644" "100644" sha sha2 "M" "p") ]
            for name, b in cases do
                Expect.isTrue (isErr (parseDiffTree b)) name

        testProperty "never throws on arbitrary bytes" <| fun (b: byte[]) ->
            (try parseDiffTree b |> ignore; true with _ -> false)
    ]

let private entry mode kind (size: int64 option) path : TreeEntry =
    { Mode = mode; Kind = kind; Sha = sha; Size = size; Path = path; PathValid = true }

let private good = entry "100644" "blob" (Some 1234L) goodPath

[<Tests>]
let classifyEntryTests =
    testList "SyncTree.classifyRecordEntry" [
        testCase "a good record file is accepted" <| fun _ ->
            Expect.equal (classifyRecordEntry good) (Accept(goodId, sha, 1234L)) "accept"
            Expect.equal (classifyRecordEntry { good with Mode = "100755" }) (Accept(goodId, sha, 1234L)) "executable"

        testCase "the size limit" <| fun _ ->
            Expect.equal (classifyRecordEntry { good with Size = Some(int64 SyncValidation.maxFileBytes) }) (Accept(goodId, sha, int64 SyncValidation.maxFileBytes)) "exactly"
            Expect.equal (classifyRecordEntry { good with Size = Some(int64 SyncValidation.maxFileBytes + 1L) }) (Reject(goodPath, TooLarge)) "one more"
            Expect.equal (classifyRecordEntry { good with Size = None }) (Reject(goodPath, NotARegularFile)) "no size"

        testCase "modes and kinds" <| fun _ ->
            Expect.equal (classifyRecordEntry { good with Mode = "120000" }) (Reject(goodPath, Symlink)) "symlink"
            Expect.equal (classifyRecordEntry { good with Mode = "160000"; Kind = "commit" }) (Reject(goodPath, Submodule)) "submodule"
            Expect.equal (classifyRecordEntry { good with Mode = "100664" }) (Reject(goodPath, NotARegularFile)) "odd mode"
            Expect.equal (classifyRecordEntry { good with Kind = "tree" }) (Reject(goodPath, NotARegularFile)) "tree"
            Expect.equal (classifyRecordEntry { good with Mode = "040000"; Kind = "tree" }) (Reject(goodPath, NotARegularFile)) "tree mode"

        testCase "ids and shards" <| fun _ ->
            let p1 = "memories/3f/x.json"
            Expect.equal (classifyRecordEntry { good with Path = p1 }) (Reject(p1, BadId)) "too short"
            for stem in [ "..%2f"; "ab"; "3F9C2A7E-0000-4000-8000-000000000000"; "-3f9c2a7e" ] do
                let p = "memories/3f/" + stem + ".json"
                Expect.equal (classifyRecordEntry { good with Path = p }) (Reject(p, BadId)) stem
            let wrong = "memories/4f/" + goodId + ".json"
            Expect.equal (classifyRecordEntry { good with Path = wrong }) (Reject(wrong, BadShard)) "shard mismatch"
            let both = "memories/4f/x.json"
            Expect.equal (classifyRecordEntry { good with Path = both }) (Reject(both, BadId)) "id before shard (Interpretation 11i)"

        testCase "path shapes" <| fun _ ->
            for p in
                [ "memories/3f/" + goodId + ".txt"
                  "memories/3f/sub/" + goodId + ".json"
                  "notes/3f/" + goodId + ".json"
                  "memories/../" + goodId + ".json"
                  "memories/./" + goodId + ".json"
                  "memories/3f/../" + goodId + ".json"
                  "/memories/3f/" + goodId + ".json"
                  "memories/3f/" + goodId + ".json/"
                  "memories//" + goodId + ".json"
                  "memories/3f/.json"
                  "memories/3f/" + goodId + "\u0001.json"
                  "memories/3f/" + goodId + "\u007f.json"
                  "memories/3/" + goodId + ".json"
                  "memories/3fx/" + goodId + ".json"
                  "memories/3F/" + goodId + ".json"
                  "memories/G3/" + goodId + ".json"
                  "memories/g3/" + goodId + ".json"
                  "memories"
                  "" ] do
                Expect.equal (classifyRecordEntry { good with Path = p }) (Reject(p, BadPath)) (sprintf "%A" p)
            Expect.equal (classifyRecordEntry { good with PathValid = false }) (Reject(goodPath, BadPath)) "invalid utf-8"

        testCase "the reason order: BadPath, mode, BadId, BadShard, TooLarge" <| fun _ ->
            let badPath = "memories/3f/sub/x.json"
            Expect.equal (classifyRecordEntry (entry "120000" "blob" (Some 1L) badPath)) (Reject(badPath, BadPath)) "path first"
            let badIdPath = "memories/3f/x.json"
            Expect.equal (classifyRecordEntry (entry "120000" "blob" (Some 1L) badIdPath)) (Reject(badIdPath, Symlink)) "mode before id"
            Expect.equal (classifyRecordEntry (entry "100644" "blob" (Some(int64 SyncValidation.maxFileBytes + 1L)) badIdPath)) (Reject(badIdPath, BadId)) "id before size"
            let badShard = "memories/4f/" + goodId + ".json"
            Expect.equal (classifyRecordEntry (entry "100644" "blob" (Some(int64 SyncValidation.maxFileBytes + 1L)) badShard)) (Reject(badShard, BadShard)) "shard before size"

        testProperty "never throws" <| fun (path: string) (mode: string) (kind: string) (size: int64 option) (valid: bool) ->
            let e = { Mode = mode; Kind = kind; Sha = sha; Size = size; Path = path; PathValid = valid }
            (try classifyRecordEntry e |> ignore; true with _ -> false)

        testCase "RejectReason.describe" <| fun _ ->
            Expect.equal
                ([ Symlink; Submodule; NotARegularFile; BadPath; BadShard; BadId; TooLarge ] |> List.map RejectReason.describe)
                [ "symlink"; "submodule"; "not-a-regular-file"; "bad-path"; "bad-shard"; "bad-id"; "too-large" ]
                "wire words"
    ]

let private diffEntry status oldMode newMode path : DiffEntry =
    { OldMode = oldMode; NewMode = newMode; OldSha = sha; NewSha = sha2; Status = status; Path = path; PathValid = true }

[<Tests>]
let classifyDiffTests =
    testList "SyncTree.classifyDiffEntry" [
        testCase "added and modified record files change" <| fun _ ->
            Expect.equal (classifyDiffEntry (diffEntry Added "000000" "100644" goodPath)) (Changed(goodId, sha2, Added)) "A"
            Expect.equal (classifyDiffEntry (diffEntry Modified "100644" "100755" goodPath)) (Changed(goodId, sha2, Modified)) "M"

        testCase "a type change to a regular file changes, to a symlink is rejected" <| fun _ ->
            Expect.equal (classifyDiffEntry (diffEntry TypeChanged "120000" "100644" goodPath)) (Changed(goodId, sha2, TypeChanged)) "T to file"
            Expect.equal (classifyDiffEntry (diffEntry TypeChanged "100644" "120000" goodPath)) (RejectedDiff(goodPath, Symlink)) "T to symlink"

        testCase "a deletion at a good path is gone, at a bad path rejected" <| fun _ ->
            Expect.equal (classifyDiffEntry (diffEntry Deleted "100644" "000000" goodPath)) (Gone goodId) "D"
            let bad = "memories/3f/sub/x.json"
            Expect.equal (classifyDiffEntry (diffEntry Deleted "100644" "000000" bad)) (RejectedDiff(bad, BadPath)) "D bad"
            let badShard = "memories/4f/" + goodId + ".json"
            Expect.equal (classifyDiffEntry (diffEntry Deleted "100644" "000000" badShard)) (RejectedDiff(badShard, BadShard)) "D shard"

        testCase "a submodule and a wrong shard are rejected" <| fun _ ->
            Expect.equal (classifyDiffEntry (diffEntry Added "000000" "160000" goodPath)) (RejectedDiff(goodPath, Submodule)) "submodule"
            let badShard = "memories/4f/" + goodId + ".json"
            Expect.equal (classifyDiffEntry (diffEntry Modified "100644" "100644" badShard)) (RejectedDiff(badShard, BadShard)) "shard"
            Expect.equal (classifyDiffEntry (diffEntry Modified "100644" "100664" goodPath)) (RejectedDiff(goodPath, NotARegularFile)) "odd mode"
            Expect.equal (classifyDiffEntry { diffEntry Modified "100644" "100644" goodPath with PathValid = false }) (RejectedDiff(goodPath, BadPath)) "invalid path"
    ]
