module TotalRecall.Core.Tests.SyncValidationTests

open Expecto
open TotalRecall.Core.SyncValidation

// An F# string literal "\uD800" is turned into U+FFFD by the compiler, so lone surrogates are built from chars.
let private hi = string (char 0xD800)
let private lo = string (char 0xDC00)

[<Tests>]
let idAndTextTests =
    testList "SyncValidation ids, paths and text" [
        testCase "eligible ids" <| fun _ ->
            for id in [ "3f9c2a7e"; String.replicate 8 "a"; String.replicate 64 "0"; "3f9c2a7e-1111-2222-3333-444455556666"; "0-------" ] do
                Expect.isTrue (isEligibleId id) (sprintf "%A" id)

        testCase "ineligible ids" <| fun _ ->
            let bad =
                [ null; ""; "3f9c2a7"; String.replicate 65 "a"; "3F9C2A7E"; "-3f9c2a7e"; "3f9c2a7e\n"; "3f9c2a7e\u0000"
                  "3f9c 2a7e"; "3f9c2a7\u0663"; "../etc/pass"; "3f9c2a7g"; "3f9c2a7e/"; "3f9c2a7e." ]
            for id in bad do
                Expect.isFalse (isEligibleId id) (sprintf "%A" id)

        testCase "recordPath and idOfPath" <| fun _ ->
            Expect.equal (recordPath "3f9c2a7e-1111") "memories/3f/3f9c2a7e-1111.json" "path"
            Expect.equal (shard "3f9c2a7e-1111") "3f" "shard"
            Expect.equal (idOfPath "memories/3f/3f9c2a7e-1111.json") (Some "3f9c2a7e-1111") "round trip"
            for bad in [ null; ""; "../../x"; "a"; "3F9C2A7E" ] do
                Expect.throws (fun () -> recordPath bad |> ignore) (sprintf "recordPath refuses %A" bad)
            for p in [ null; ""; "memories/aa/3f9c2a7e-1111.json"; "memories/3f/x/3f9c2a7e-1111.json"; "memories/../x.json"
                       "memories/3f/3f9c2a7e-1111.JSON"; "memories/3f/3f9c2a7e-1111.json.bak"; "other/3f/3f9c2a7e-1111.json"
                       "memories\\3f\\3f9c2a7e-1111.json"; "/memories/3f/3f9c2a7e-1111.json"; "memories/3f/3F9C2A7E-1111.json"
                       "memories/3f/short.json"; "memories/3f/.json" ] do
                Expect.isNone (idOfPath p) (sprintf "%A" p)

        testCase "hasLoneSurrogate" <| fun _ ->
            Expect.isFalse (hasLoneSurrogate "plain \U0001F600 text") "a valid pair"
            Expect.isFalse (hasLoneSurrogate "") "empty"
            Expect.isTrue (hasLoneSurrogate ("a" + hi + "b")) "lone high"
            Expect.isTrue (hasLoneSurrogate ("a" + lo)) "lone low"
            Expect.isTrue (hasLoneSurrogate (lo + hi)) "reversed pair"

        testCase "sanitizeText truncates at 64 code units" <| fun _ ->
            Expect.equal (sanitizeText (String.replicate 64 "a")) (String.replicate 64 "a") "64 kept"
            Expect.equal (sanitizeText (String.replicate 65 "a")) (String.replicate 64 "a") "65 cut to 64"
            Expect.equal (sanitizeText null) "" "null"

        testCase "sanitizeText never cuts a surrogate pair and replaces lone ones" <| fun _ ->
            let straddle = String.replicate 63 "a" + "\U0001F600" + "tail"
            Expect.equal (sanitizeText straddle) (String.replicate 63 "a") "the pair is dropped whole and the text ends"
            Expect.equal (sanitizeText (String.replicate 62 "a" + "\U0001F600")) (String.replicate 62 "a" + "\U0001F600") "a pair that fits is kept"
            Expect.equal (sanitizeText ("a" + hi + "b")) "a?b" "lone high"
            Expect.equal (sanitizeText ("a" + lo + "b")) "a?b" "lone low"

        testCase "sanitizeText replaces control characters" <| fun _ ->
            Expect.equal (sanitizeText (String.replicate 63 "a" + hi)) (String.replicate 63 "a" + "?") "a lone high surrogate as the last character"
            Expect.equal (sanitizeText (String.replicate 63 "a" + hi + "tail")) (String.replicate 63 "a" + "?" + "") "a lone surrogate at the cut is replaced, then the limit stops the text"
            Expect.equal (sanitizeText "\u0085\u00A0") "?\u00A0" "U+0085 is Cc, U+00A0 is not"
            Expect.equal (sanitizeText "a\u0007b\u007Fc\u009Bd\u2028e\u2029f\ng\u0000") "a?b?c?d?e?f?g?" "Cc, U+2028, U+2029"
            Expect.equal (sanitizeText "é中😀") "é中😀" "printable text is untouched"
    ]
