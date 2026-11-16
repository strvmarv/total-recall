module TotalRecall.Core.Tests.SyncManifestTests

open System
open System.Text
open Expecto
open FsCheck
open TotalRecall.Core.SyncManifest

let private id32 = "0123456789abcdef0123456789abcdef"

[<Tests>]
let tests =
    testList "SyncManifest" [
        testCase "total-recall/v1 is supported" <| fun _ ->
            Expect.equal (classifyFormat "total-recall/v1") Supported "v1"

        testCase "a newer format is Newer with its version (decision 2)" <| fun _ ->
            Expect.equal (classifyFormat "total-recall/v2") (Newer 2) "v2"
            Expect.equal (classifyFormat "total-recall/v10") (Newer 10) "v10"
            Expect.equal (classifyFormat "total-recall/v999999999") (Newer 999999999) "nine digits"

        testCase "anything else is unrecognised, not newer" <| fun _ ->
            for f in [ "total-recall/v1000000000"; "total-recall/v0"; "total-recall/v01"; "total-recall/v-1"
                       "total-recall/v1.0"; ""; "Total-Recall/v2"; "other/v2"; "total-recall/v"; "total-recall/v2 "
                       "total-recall/v٢" ] do
                Expect.equal (classifyFormat f) Unrecognised (sprintf "%A" f)

        testCase "a repo id is exactly 32 lowercase hex digits" <| fun _ ->
            Expect.isTrue (isValidRepoId id32) "valid"
            for bad in [ id32.Substring 1; id32 + "0"; id32.ToUpperInvariant(); "g" + id32.Substring 1; "" ] do
                Expect.isFalse (isValidRepoId bad) bad

        testCase "render writes the single line of decision 1, byte for byte" <| fun _ ->
            let bytes = render id32 "total-recall 4.7.0" "2026-11-15T10:00:00Z"
            let expected =
                "{\"format\":\"total-recall/v1\",\"repo_id\":\"" + id32
                + "\",\"created_by\":\"total-recall 4.7.0\",\"created_at\":\"2026-11-15T10:00:00Z\"}\n"
            Expect.equal bytes (Encoding.UTF8.GetBytes expected) "bytes"
            Expect.isFalse (bytes.Length >= 3 && bytes.[0] = 0xEFuy && bytes.[1] = 0xBBuy) "no BOM"

        testCase "render escapes quote, backslash and controls, and writes the rest as UTF-8" <| fun _ ->
            let bytes = render id32 "a\"b\\c\n\t\u0001\u00e9" "2026-11-15T10:00:00Z"
            let text = Encoding.UTF8.GetString bytes
            Expect.stringContains text "\"created_by\":\"a\\\"b\\\\c\\n\\t\\u0001\u00e9\"" "escaped"

        testCase "render refuses a lone surrogate, a bad id and a bad timestamp" <| fun _ ->
            let throws f = Expect.throwsT<ArgumentException> f "ArgumentException"
            let lone = "a" + string (char 0xD800) + "b"
            throws (fun () -> render id32 lone "2026-11-15T10:00:00Z" |> ignore)
            throws (fun () -> render "nope" "x" "2026-11-15T10:00:00Z" |> ignore)
            throws (fun () -> render id32 "x" "2026-11-15 10:00:00" |> ignore)
            throws (fun () -> render id32 "x" "2026-11-15T10:00:00.000Z" |> ignore)

        testCase "classifyFormat never throws (property)" <| fun _ ->
            Check.QuickThrowOnFailure(fun (s: string) -> classifyFormat (if isNull s then "" else s) |> ignore)
    ]
