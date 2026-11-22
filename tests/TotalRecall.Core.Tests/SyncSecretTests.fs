module TotalRecall.Core.Tests.SyncSecretTests

open Expecto
open TotalRecall.Core
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncSecret

let private fields : ContentFields =
    { Text = "Use bun, not npm."
      Project = Some "owner/repo"
      Scope = Some "team"
      EntryType = Correction
      Source = None
      CreatedAtMs = 1_700_000_000_000L }

let private tags = [ "tooling"; "js" ]

let private baseKey = secretKey fields tags

[<Tests>]
let secretKeyTests =
    testList
        "SyncSecret.secretKey"
        [ test "is 64 lowercase hex characters and deterministic" {
              Expect.equal baseKey.Length 64 "length"
              Expect.isTrue (baseKey |> Seq.forall (fun c -> System.Uri.IsHexDigit c && not (System.Char.IsUpper c))) "lowercase hex"
              Expect.equal (secretKey fields tags) baseKey "deterministic"
          }
          test "the reason is exactly secret-detected" { Expect.equal blockReason "secret-detected" "reason" }
          test "changes with the text" {
              Expect.notEqual (secretKey { fields with Text = "Use bun." } tags) baseKey "text"
          }
          test "changes with the project" {
              Expect.notEqual (secretKey { fields with Project = Some "owner/other" } tags) baseKey "project"
              Expect.notEqual (secretKey { fields with Project = None } tags) baseKey "project none"
          }
          test "changes with the scope" {
              Expect.notEqual (secretKey { fields with Scope = Some "me" } tags) baseKey "scope"
              Expect.notEqual (secretKey { fields with Scope = None } tags) baseKey "scope none"
          }
          test "a project and a scope with the same value are not interchangeable" {
              let a = secretKey { fields with Project = Some "x"; Scope = None } tags
              let b = secretKey { fields with Project = None; Scope = Some "x" } tags
              Expect.notEqual a b "fields are positional"
          }
          test "changes with any tag" {
              Expect.notEqual (secretKey fields [ "tooling"; "other" ]) baseKey "tag changed"
              Expect.notEqual (secretKey fields [ "tooling" ]) baseKey "tag removed"
              Expect.notEqual (secretKey fields [ "tooling"; "js"; "more" ]) baseKey "tag added"
          }
          test "is unchanged by entry type, source tool and created_at" {
              let other = { fields with EntryType = Preference; Source = Some ClaudeCode; CreatedAtMs = 42L }
              Expect.equal (secretKey other tags) baseKey "not in the key"
          }
          test "is unchanged by tag order and duplicates" {
              Expect.equal (secretKey fields [ "js"; "tooling"; "js" ]) baseKey "normalised"
          }
          test "two different tag splits differ" {
              Expect.notEqual (secretKey fields [ "ab" ]) (secretKey fields [ "a"; "b" ]) "length-prefixed"
          } ]

let private key = "k1"

[<Tests>]
let precheckTests =
    testList
        "SyncSecret.precheck"
        [ test "an allowed hash equal to the key is Allowed" {
              Expect.equal (precheck None None (Some key) key) Allowed "allowed"
          }
          test "allowed wins over a matching blocked hash" {
              Expect.equal (precheck (Some blockReason) (Some key) (Some key) key) Allowed "allowed first"
          }
          test "a different allowed hash does not allow" {
              Expect.equal (precheck None None (Some "other") key) NeedsScan "other allowed"
          }
          test "the secret reason with the equal key is StillBlocked" {
              Expect.equal (precheck (Some blockReason) (Some key) None key) StillBlocked "still blocked"
          }
          test "the secret reason with a different key needs a scan" {
              Expect.equal (precheck (Some blockReason) (Some "other") None key) NeedsScan "changed"
              Expect.equal (precheck (Some blockReason) None None key) NeedsScan "no hash"
          }
          test "a writer reason with an equal blocked hash needs a scan" {
              Expect.equal (precheck (Some "too-large") (Some key) None key) NeedsScan "writer reason"
          }
          test "no mark needs a scan" { Expect.equal (precheck None None None key) NeedsScan "no mark" } ]

[<Tests>]
let afterScanTests =
    testList
        "SyncSecret.afterScan"
        [ test "a hit sets the block whatever the reason" {
              Expect.equal (afterScan None true key) (SetBlock key) "no reason"
              Expect.equal (afterScan (Some "too-large") true key) (SetBlock key) "replaces too-large"
              Expect.equal (afterScan (Some blockReason) true key) (SetBlock key) "already blocked"
          }
          test "no hit clears the secret block" {
              Expect.equal (afterScan (Some blockReason) false key) ClearBlock "clear"
          }
          test "no hit keeps a writer reason and no reason" {
              Expect.equal (afterScan (Some "too-large") false key) KeepMark "too-large"
              Expect.equal (afterScan None false key) KeepMark "none"
          }
          test "afterAllowed clears only the secret block" {
              Expect.equal (afterAllowed (Some blockReason)) ClearBlock "clear"
              Expect.equal (afterAllowed (Some "too-large")) KeepMark "too-large"
              Expect.equal (afterAllowed None) KeepMark "none"
          } ]
