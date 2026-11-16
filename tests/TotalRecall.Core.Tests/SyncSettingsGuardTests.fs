module TotalRecall.Core.Tests.SyncSettingsGuardTests

open Expecto
open TotalRecall.Core.SyncSettingsGuard

[<Tests>]
let tests =
    testList "SyncSettingsGuard" [
        testCase "the storage mode, the connection string, git_sync and everything beneath them are protected" <| fun _ ->
            for key in [ "storage.mode"; "storage.connection_string"; "storage.mode.x"; "storage.connection_string.x"; "storage"
                         "git_sync"; "git_sync.remote"; "git_sync.a.b"; " Git_Sync . remote "; "STORAGE.MODE"
                         "storage . connection_string"; "storage..mode"; ""; "."; "git_sync." ] do
                Expect.isTrue (isProtectedKey key) (sprintf "%A" key)

        testCase "look-alikes and ordinary keys stay writable" <| fun _ ->
            for key in [ "storagex.mode"; "storage.other"; "git_syncx.remote"; "tiers.hot.max_entries"; "embedding.provider"; "x.storage.mode" ] do
                Expect.isFalse (isProtectedKey key) key

        testCase "the refusal names the key and says the user changes it, without naming sync commands" <| fun _ ->
            let text = refusal "git_sync.remote"
            Expect.stringContains text "git_sync.remote" "key"
            Expect.stringContains text "changed by you, in `config.toml`; an agent cannot change it" "wording"
            Expect.isFalse (text.Contains "sync init") "no sync commands yet"
    ]
