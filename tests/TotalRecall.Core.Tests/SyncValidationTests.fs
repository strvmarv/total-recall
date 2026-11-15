module TotalRecall.Core.Tests.SyncValidationTests

open Expecto
open FsCheck
open TotalRecall.Core
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
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


// ---- validate and checkWritable ----

let private path = "memories/3f/3f9c2a7e-1111.json"
let private stampText = "2026-11-12T09:14:03.512Z.000003.4f2a9c1e"

let private goodRaw : RawSyncRecord =
    { Format = Value "total-recall/memory/v1"
      Id = Value "3f9c2a7e-1111"
      Content = Value "Use bun, not npm, in this repo."
      EntryType = Value "correction"
      CreatedAt = Value "2026-11-12T09:14:03.000Z"
      UpdatedAt = Value "2026-11-12T09:14:03.512Z"
      Tags = Value [ "tooling" ]
      Tier = Value "warm"
      Sticky = Value false
      TierStamp = Value stampText
      StickyStamp = Value stampText
      ContentStamp = Value stampText
      Source = Value "claude-code"
      Project = Value "owner/repo"
      Scope = Absent
      WrittenBy = Value "laptop"
      Origin = Absent }

let private run (raw: RawSyncRecord) = validate path 1000 raw
let private rejected (raw: RawSyncRecord) =
    match run raw with
    | Error r -> r
    | Ok v -> failwithf "expected a rejection, got %A" v
let private accepted (raw: RawSyncRecord) =
    match run raw with
    | Ok v -> v
    | Error r -> failwithf "expected acceptance, got %A" r

let private longText = String.replicate 100 "x" + "\n" + string (char 0xD800)

[<Tests>]
let validateTests =
    testList "SyncValidation.validate" [
        testCase "a good record is accepted and mapped" <| fun _ ->
            let v = accepted goodRaw
            Expect.equal v.Notes [] "no notes"
            Expect.equal v.StoredTier Warm "stored tier"
            Expect.equal v.Record.Id "3f9c2a7e-1111" "id"
            Expect.equal v.Record.EntryType Correction "entry type"
            Expect.equal v.Record.Source (Some ClaudeCode) "source"
            Expect.equal v.Record.Project (Some "owner/repo") "project"
            Expect.equal v.Record.WrittenBy (Some "laptop") "written_by"
            Expect.equal v.Record.CreatedAtMs (Timestamp.tryParse "2026-11-12T09:14:03.000Z" |> Option.get) "created"
            Expect.equal v.Record.Placement.Tier Warm "tier"
            Expect.equal v.Record.ContentStamp (Stamp.tryParse stampText |> Option.get) "content stamp"

        testCase "rules are applied in the spec's order" <| fun _ ->
            Expect.equal (validate path 1_048_577 { goodRaw with Format = Value "other" }) (Error Oversize) "oversize first"
            Expect.equal (rejected { goodRaw with Format = Value "total-recall/memory/v2"; Id = Absent }) (UnknownFormat "total-recall/memory/v2") "unknown format before a missing id"
            Expect.equal (rejected { goodRaw with Format = Absent }) (MissingField "format") "format absent"
            Expect.equal (rejected { goodRaw with Format = Mistyped }) (WrongType "format") "format mistyped"
            Expect.equal (rejected { goodRaw with Id = Absent }) (MissingField "id") "id absent"
            Expect.equal (rejected { goodRaw with Id = Value "3F9C2A7E-1111" }) BadId "bad id"
            Expect.equal (rejected { goodRaw with Id = Value "3f9c2a7e-2222" }) IdPathMismatch "id vs path"
            Expect.equal (rejected { goodRaw with Id = Value "aa9c2a7e-1111" }) IdPathMismatch "id vs shard"
            // each required field absent and mistyped, in the spec's order: the first failing one wins
            Expect.equal (rejected { goodRaw with Content = Absent }) (MissingField "content") "content"
            Expect.equal (rejected { goodRaw with EntryType = Mistyped }) (WrongType "entry_type") "entry_type mistyped"
            Expect.equal (rejected { goodRaw with EntryType = Value "Correction" }) (BadEntryType "Correction") "entry type"
            Expect.equal (rejected { goodRaw with Tags = Absent }) (MissingField "tags") "tags"
            Expect.equal (rejected { goodRaw with Tier = Absent }) (MissingField "ext.total-recall.placement.tier") "tier"
            Expect.equal (rejected { goodRaw with Tier = Value "pinned" }) (BadTier "pinned") "bad tier"
            Expect.equal (rejected { goodRaw with Sticky = Absent }) (MissingField "ext.total-recall.placement.sticky") "sticky"
            Expect.equal (rejected { goodRaw with Sticky = Mistyped }) (WrongType "ext.total-recall.placement.sticky") "sticky mistyped"
            Expect.equal (rejected { goodRaw with CreatedAt = Absent }) (MissingField "created_at") "created_at"
            Expect.equal (rejected { goodRaw with CreatedAt = Value "2026-11-12T09:14:03Z" }) (BadTimestamp "created_at") "created_at form"
            Expect.equal (rejected { goodRaw with UpdatedAt = Value "nope" }) (BadTimestamp "updated_at") "updated_at"
            Expect.equal (rejected { goodRaw with ContentStamp = Absent }) (MissingField "ext.total-recall.content_stamp") "content stamp"
            Expect.equal (rejected { goodRaw with ContentStamp = Value "x" }) (BadStamp "ext.total-recall.content_stamp") "content stamp form"
            Expect.equal (rejected { goodRaw with TierStamp = Value "x" }) (BadStamp "ext.total-recall.placement.tier_stamp") "tier stamp"
            Expect.equal (rejected { goodRaw with StickyStamp = Absent }) (MissingField "ext.total-recall.placement.sticky_stamp") "sticky stamp"
            // two bad fields: the earlier one in the order wins
            Expect.equal (rejected { goodRaw with Content = Absent; Tier = Absent; StickyStamp = Absent }) (MissingField "content") "content before tier"
            Expect.equal (rejected { goodRaw with Tier = Value "x"; Sticky = Absent }) (BadTier "x") "tier before sticky"
            Expect.equal (rejected { goodRaw with UpdatedAt = Value "x"; ContentStamp = Value "x" }) (BadTimestamp "updated_at") "timestamps before stamps"

        testCase "a null where a string or list is expected is a rejection, not an exception" <| fun _ ->
            Expect.equal (rejected { goodRaw with Tags = Value [ null ] }) BadTags "null tag"
            Expect.equal (rejected { goodRaw with Tags = Value Unchecked.defaultof<string list> }) BadTags "null list"
            Expect.equal (rejected { goodRaw with Format = Value null }) (UnknownFormat "") "null format"
            Expect.equal (rejected { goodRaw with Id = Value null }) BadId "null id"
            Expect.equal (rejected { goodRaw with EntryType = Value null }) (BadEntryType "") "null entry type"
            Expect.equal (rejected { goodRaw with Tier = Value null }) (BadTier "") "null tier"
            Expect.equal (rejected { goodRaw with CreatedAt = Value null }) (BadTimestamp "created_at") "null timestamp"
            Expect.equal (rejected { goodRaw with ContentStamp = Value null }) (BadStamp "ext.total-recall.content_stamp") "null stamp"
            Expect.equal (validate null 10 goodRaw) (Error IdPathMismatch) "a null path is a rejection"

        testCase "the full order, one fix at a time" <| fun _ ->
            let allBad : RawSyncRecord =
                { Format = Value "x"; Id = Value "x"; Content = Absent; EntryType = Value "x"; CreatedAt = Value "x"; UpdatedAt = Value "x"
                  Tags = Absent; Tier = Value "x"; Sticky = Absent; TierStamp = Value "x"; StickyStamp = Value "x"; ContentStamp = Value "x"
                  Source = Mistyped; Project = Mistyped; Scope = Mistyped; WrittenBy = Mistyped; Origin = Mistyped }
            let chain =
                [ (fun r -> { r with Format = goodRaw.Format }), UnknownFormat "x"
                  (fun r -> { r with Id = goodRaw.Id }), BadId
                  (fun r -> { r with Content = goodRaw.Content }), MissingField "content"
                  (fun r -> { r with EntryType = goodRaw.EntryType }), BadEntryType "x"
                  (fun r -> { r with Tags = goodRaw.Tags }), MissingField "tags"
                  (fun r -> { r with Tier = goodRaw.Tier }), BadTier "x"
                  (fun r -> { r with Sticky = goodRaw.Sticky }), MissingField "ext.total-recall.placement.sticky"
                  (fun r -> { r with CreatedAt = goodRaw.CreatedAt }), BadTimestamp "created_at"
                  (fun r -> { r with UpdatedAt = goodRaw.UpdatedAt }), BadTimestamp "updated_at"
                  (fun r -> { r with ContentStamp = goodRaw.ContentStamp }), BadStamp "ext.total-recall.content_stamp"
                  (fun r -> { r with TierStamp = goodRaw.TierStamp }), BadStamp "ext.total-recall.placement.tier_stamp"
                  (fun r -> { r with StickyStamp = goodRaw.StickyStamp }), BadStamp "ext.total-recall.placement.sticky_stamp"
                  (fun r -> { r with Source = goodRaw.Source }), WrongType "source"
                  (fun r -> { r with Project = goodRaw.Project }), WrongType "ext.total-recall.project"
                  (fun r -> { r with Scope = goodRaw.Scope }), WrongType "ext.total-recall.scope"
                  (fun r -> { r with WrittenBy = goodRaw.WrittenBy }), WrongType "ext.total-recall.written_by"
                  (fun r -> { r with Origin = goodRaw.Origin }), WrongType "ext.total-recall.origin" ]
            let mutable raw = allBad
            // each step names what the current record is rejected for, then fixes exactly that field
            Expect.equal (rejected raw) (UnknownFormat "x") "start"
            let mutable i = 0
            for fix, _ in chain do
                raw <- fix raw
                i <- i + 1
                if i < chain.Length then
                    // the reject now reported is the one the NEXT step in the chain fixes
                    let _, next = chain.[i]
                    Expect.equal (rejected raw) next (sprintf "after fix %d" i)
            Expect.isOk (run raw) "all fixed"

        testCase "the two notes together come in a fixed order" <| fun _ ->
            let v = accepted { goodRaw with Source = Value "windsurf"; Sticky = Value true; Tier = Value "cold" }
            Expect.equal v.Notes [ UnknownSourceDropped "windsurf"; StickyNormalisedToHot ] "source note, then sticky note"

        testCase "optional fields: a wrong type is rejected, absent is fine" <| fun _ ->
            Expect.equal (rejected { goodRaw with Project = Mistyped }) (WrongType "ext.total-recall.project") "project"
            Expect.equal (rejected { goodRaw with Scope = Mistyped }) (WrongType "ext.total-recall.scope") "scope"
            Expect.equal (rejected { goodRaw with WrittenBy = Mistyped }) (WrongType "ext.total-recall.written_by") "written_by"
            Expect.equal (rejected { goodRaw with Origin = Mistyped }) (WrongType "ext.total-recall.origin") "origin"
            Expect.equal (rejected { goodRaw with Source = Mistyped }) (WrongType "source") "source"
            let v = accepted { goodRaw with Source = Absent; Project = Absent; WrittenBy = Absent }
            Expect.equal (v.Record.Source, v.Record.Project, v.Record.WrittenBy) (None, None, None) "absent"
            let o = accepted { goodRaw with Origin = Value { Tool = "claude-code"; Hash = "abc" } }
            Expect.equal o.Record.Origin (Some { Tool = "claude-code"; Hash = "abc" }) "origin carried"

        testCase "size boundary" <| fun _ ->
            Expect.isOk (validate path 1_048_576 goodRaw) "exactly 1 MiB is fine"
            Expect.equal (validate path 1_048_577 goodRaw) (Error Oversize) "one over"

        testCase "tag limits on read" <| fun _ ->
            let many n = List.init n (fun i -> sprintf "t%03d" i)
            Expect.isOk (run { goodRaw with Tags = Value (many 256) }) "256 tags"
            Expect.equal (rejected { goodRaw with Tags = Value (many 257) }) BadTags "257 tags"
            Expect.isOk (run { goodRaw with Tags = Value [ String.replicate 256 "a" ] }) "256 code units"
            Expect.equal (rejected { goodRaw with Tags = Value [ String.replicate 257 "a" ] }) BadTags "257 code units"

        testCase "tags are stored sorted and unique" <| fun _ ->
            Expect.equal (accepted { goodRaw with Tags = Value [ "b"; "a"; "b" ] }).Record.Tags [ "a"; "b" ] "normalised"

        testCase "an unknown source is dropped with a note" <| fun _ ->
            let v = accepted { goodRaw with Source = Value "windsurf" }
            Expect.equal v.Record.Source None "dropped"
            Expect.equal v.Notes [ UnknownSourceDropped "windsurf" ] "note"
            let long = accepted { goodRaw with Source = Value (String.replicate 100 "s") }
            Expect.equal long.Notes [ UnknownSourceDropped (String.replicate 64 "s") ] "truncated to 64"
            let dirty = accepted { goodRaw with Source = Value ("a\nb" + string (char 0xD800)) }
            Expect.equal dirty.Notes [ UnknownSourceDropped "a?b?" ] "control character and lone surrogate replaced"

        testCase "sticky with a colder tier is normalised to hot" <| fun _ ->
            let v = accepted { goodRaw with Sticky = Value true; Tier = Value "warm" }
            Expect.equal v.Record.Placement.Tier Hot "model tier"
            Expect.equal v.StoredTier Warm "stored tier"
            Expect.equal v.Notes [ StickyNormalisedToHot ] "note"
            let hot = accepted { goodRaw with Sticky = Value true; Tier = Value "hot" }
            Expect.equal hot.Notes [] "already hot: no note"

        testCase "text from the record is truncated and sanitised in a reject" <| fun _ ->
            let dirty = "a\nb" + string (char 0xD800)   // the bad characters sit inside the first 64 units
            Expect.equal (rejected { goodRaw with Format = Value dirty }) (UnknownFormat "a?b?") "format"
            Expect.equal (rejected { goodRaw with EntryType = Value dirty }) (BadEntryType "a?b?") "entry type"
            Expect.equal (rejected { goodRaw with Tier = Value dirty }) (BadTier "a?b?") "tier"
            // a surrogate pair straddling the 64-unit cut is dropped whole
            let straddle = String.replicate 63 "a" + "\U0001F600" + "tail"
            Expect.equal (rejected { goodRaw with Format = Value straddle }) (UnknownFormat (String.replicate 63 "a")) "pair at the cut"
            Expect.equal (rejected { goodRaw with Tier = Value longText }) (BadTier (String.replicate 64 "x")) "100 units cut to 64"

        testCase "Reject.describe is one line without control characters for every case" <| fun _ ->
            let all =
                [ Oversize; UnknownFormat longText; MissingField "x"; WrongType "x"; BadId; IdPathMismatch
                  BadEntryType "x"; BadTags; BadTier "x"; BadTimestamp "x"; BadStamp "x"; NotJson "x"; DuplicateKey "x" ]
            for r in all do
                let text = Reject.describe (match r with UnknownFormat t -> UnknownFormat (sanitizeText t) | other -> other)
                Expect.isNotEmpty text (sprintf "%A" r)
                Expect.isFalse (text |> Seq.exists System.Char.IsControl) (sprintf "%A" r)

        testProperty "validate never throws for random raw records" <|
            (let pool = [| 'a'; 'b'; '0'; '9'; '-'; '/'; '.'; 'T'; 'Z'; ':'; ' '; '\u0000'; '\u001F'; '\u007F'; '\u2028'; char 0xD800; char 0xDC00 |]
             let strGen = Gen.listOf (Gen.elements pool) |> Gen.map (fun cs -> System.String(List.toArray cs))
             let fieldGen (g: Gen<'a>) = Gen.frequency [ 1, Gen.constant Absent; 1, Gen.constant Mistyped; 6, Gen.map Value g ]
             // nine times in ten a field starts out valid, so the later rules and the Ok branch are reached
             let strField (good: string) = Gen.frequency [ 9, Gen.constant (Value good); 1, fieldGen strGen ]
             let rawGen =
                 gen {
                     let! format = strField "total-recall/memory/v1"
                     let! id = strField "3f9c2a7e-1111"
                     let! content = fieldGen strGen
                     let! entryType = strField "decision"
                     let! created = strField "2026-11-12T09:14:03.000Z"
                     let! updated = strField "2026-11-12T09:14:03.512Z"
                     let! tags = fieldGen (Gen.listOf strGen)
                     let! tier = strField "hot"
                     let! sticky = fieldGen Arb.generate<bool>
                     let! ts = strField stampText
                     let! source = fieldGen strGen
                     let! project = fieldGen strGen
                     return
                         { goodRaw with
                             Format = format; Id = id; Content = content; EntryType = entryType; CreatedAt = created
                             UpdatedAt = updated; Tags = tags; Tier = tier; Sticky = sticky; TierStamp = ts
                             StickyStamp = ts; ContentStamp = ts; Source = source; Project = project }
                 }
             let sizeGen = Gen.frequency [ 8, Gen.constant 1000; 1, Gen.elements [ -5; 0; 1_048_576; 1_048_577; System.Int32.MaxValue ] ]
             let pathGen = Gen.frequency [ 12, Gen.constant path; 1, Gen.elements [ ""; "memories/aa/x.json"; null; "memories/3f/3f9c2a7e-1111.json/" ] ]
             Prop.forAll (Arb.fromGen (Gen.zip3 pathGen sizeGen rawGen)) (fun (p, size, raw) ->
                 match validate p size raw with
                 | Ok v -> isEligibleId v.Record.Id && v.Record.Tags = normaliseTags v.Record.Tags
                 | Error r -> (Reject.describe r).Length > 0))
    ]

let private sampleRecord : SyncRecord.SyncRecord = (accepted goodRaw).Record

[<Tests>]
let writableTests =
    testList "SyncValidation.checkWritable" [
        testCase "size limit" <| fun _ ->
            Expect.equal (checkWritable sampleRecord 262_144) None "exactly at the limit"
            Expect.equal (checkWritable sampleRecord 262_145) (Some TooLarge) "one over"

        testCase "tag count and length" <| fun _ ->
            let tags n = List.init n (fun i -> sprintf "t%03d" i)
            Expect.equal (checkWritable { sampleRecord with Tags = tags 64 } 100) None "64 tags"
            Expect.equal (checkWritable { sampleRecord with Tags = tags 65 } 100) (Some TooManyTags) "65 tags"
            Expect.equal (checkWritable { sampleRecord with Tags = [ String.replicate 128 "a" ] } 100) None "128 units"
            Expect.equal (checkWritable { sampleRecord with Tags = [ String.replicate 129 "a" ] } 100) (Some TagTooLong) "129 units"

        testCase "ineligible id" <| fun _ ->
            Expect.equal (checkWritable { sampleRecord with Id = "../x" } 100) (Some IneligibleId) "traversal"
            Expect.equal (checkWritable { sampleRecord with Id = "3F9C2A7E" } 100) (Some IneligibleId) "upper case"

        testCase "text that cannot be encoded" <| fun _ ->
            let lone = "a" + string (char 0xD800)
            let origin : Origin = { Tool = "claude-code"; Hash = lone }
            for name, r in [ "content", { sampleRecord with Content = lone }
                             "tag", { sampleRecord with Tags = [ lone ] }
                             "project", { sampleRecord with Project = Some lone }
                             "scope", { sampleRecord with Scope = Some lone }
                             "written_by", { sampleRecord with WrittenBy = Some lone }
                             "origin", { sampleRecord with Origin = Some origin } ] do
                Expect.equal (checkWritable r 100) (Some NotEncodable) name
            // the id is checked for eligibility first, so a surrogate in it is IneligibleId
            Expect.equal (checkWritable { sampleRecord with Id = "3f9c2a7e" + string (char 0xD800) } 100) (Some IneligibleId) "id"

        testCase "every string field and every stamp is checked" <| fun _ ->
            let lone = "a" + string (char 0xD800)
            Expect.equal (checkWritable { sampleRecord with Origin = Some { Tool = lone; Hash = "h" } } 100) (Some NotEncodable) "origin tool"
            let stickyBad = { sampleRecord.Placement.StickyStamp with Counter = 1_000_000 }
            Expect.equal (checkWritable { sampleRecord with Placement = { sampleRecord.Placement with StickyStamp = stickyBad } } 100) (Some NotEncodable) "sticky stamp counter"
            let negative = { sampleRecord.ContentStamp with Counter = -1 }
            Expect.equal (checkWritable { sampleRecord with ContentStamp = negative } 100) (Some NotEncodable) "negative counter"
            let late = { sampleRecord.ContentStamp with TimeMs = maxTimeMs + 1L }
            Expect.equal (checkWritable { sampleRecord with ContentStamp = late } 100) (Some NotEncodable) "time above the maximum"
            Expect.equal (checkWritable { sampleRecord with Content = null } 100) (Some NotEncodable) "null content"
            Expect.equal (checkWritable { sampleRecord with Tags = [ null ] } 100) (Some NotEncodable) "null tag"
            Expect.equal (checkWritable { sampleRecord with Project = Some null } 100) (Some NotEncodable) "null project"

        testCase "a hand-built record with a null part is not encodable and does not throw" <| fun _ ->
            Expect.equal (checkWritable { sampleRecord with Tags = Unchecked.defaultof<string list> } 100) (Some NotEncodable) "null tags"
            Expect.equal (checkWritable { sampleRecord with Placement = Unchecked.defaultof<Placement> } 100) (Some NotEncodable) "null placement"
            Expect.equal (checkWritable { sampleRecord with ContentStamp = Unchecked.defaultof<Stamp> } 100) (Some NotEncodable) "null stamp"
            Expect.equal (checkWritable Unchecked.defaultof<SyncRecord.SyncRecord> 100) (Some NotEncodable) "null record"

        testCase "the limits are the spec's numbers" <| fun _ ->
            Expect.equal (maxFileBytes, maxRecordBytes, maxWriteTags, maxWriteTagUnits, maxReadTags, maxReadTagUnits)
                (1_048_576, 262_144, 64, 128, 256, 256) "limits"

        testCase "duplicate tags count once" <| fun _ ->
            let tags = List.init 64 (fun i -> sprintf "t%03d" i)
            Expect.equal (checkWritable { sampleRecord with Tags = tags @ tags } 100) None "64 distinct tags written twice are still 64"

        testCase "numbers outside their domain" <| fun _ ->
            Expect.equal (checkWritable { sampleRecord with CreatedAtMs = Timestamp.minMs - 1L } 100) (Some NotEncodable) "created below"
            Expect.equal (checkWritable { sampleRecord with CreatedAtMs = Timestamp.maxMs + 1L } 100) (Some NotEncodable) "created above"
            Expect.equal (checkWritable { sampleRecord with CreatedAtMs = Timestamp.minMs } 100) None "created at the minimum"
            let bad = { sampleRecord.ContentStamp with Counter = 1_000_000 }
            Expect.equal (checkWritable { sampleRecord with ContentStamp = bad } 100) (Some NotEncodable) "counter"
            let early = { sampleRecord.Placement.TierStamp with TimeMs = -1L }
            Expect.equal (checkWritable { sampleRecord with Placement = { sampleRecord.Placement with TierStamp = early } } 100) (Some NotEncodable) "tier stamp time"

        testCase "the first matching block is returned, in the spec's order" <| fun _ ->
            let many = List.init 65 (fun i -> sprintf "t%03d" i)
            let all = { sampleRecord with Id = "bad"; Tags = (String.replicate 129 "a") :: many; Content = "a" + string (char 0xD800) }
            Expect.equal (checkWritable all 300_000) (Some TooLarge) "size first"
            Expect.equal (checkWritable all 100) (Some TooManyTags) "then tag count"
            Expect.equal (checkWritable { all with Tags = [ String.replicate 129 "a" ] } 100) (Some TagTooLong) "then tag length"
            Expect.equal (checkWritable { all with Tags = [] } 100) (Some IneligibleId) "then the id"
            Expect.equal (checkWritable { all with Tags = []; Id = "3f9c2a7e" } 100) (Some NotEncodable) "then encodability"

        testCase "a good record passes" <| fun _ ->
            Expect.equal (checkWritable sampleRecord 500) None "ok"
    ]
