/// Which configuration keys only the user may change (milestone 1a-2): the storage mode and the sync settings. One
/// pure predicate, called by the MCP `config_set` handler (which the web UI reaches too) and the CLI `config set`, so
/// a memory the agent read cannot point the store at someone else's repository or database.
module TotalRecall.Core.SyncSettingsGuard

open System

/// True for `git_sync` and `transcripts` at any depth (and the bare table names), for `storage.mode`, `storage.connection_string`, every
/// key beneath them, and the bare `storage` (which would replace the table); also for a key with an empty segment.
/// Segments are trimmed and compared without regard to case, so a spelling the config writer would quote still counts.
let isProtectedKey (key: string) : bool =
    if isNull key then true
    else
        let segments = key.Split('.') |> Array.map (fun s -> s.Trim().ToLowerInvariant())
        if segments |> Array.exists (fun s -> s.Length = 0) then true
        else
            match segments.[0] with
            | "git_sync" -> true
            | "transcripts" -> true
            | "storage" -> segments.Length < 2 || segments.[1] = "mode" || segments.[1] = "connection_string"
            | _ -> false

/// The refusal message, naming the key.
let refusal (key: string) : string =
    sprintf "config: refusing to set '%s'. This setting is changed by you, in `config.toml`; an agent cannot change it." key
