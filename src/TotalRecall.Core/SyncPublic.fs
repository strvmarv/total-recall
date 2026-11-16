/// The pure side of the public-repository check of git sync (milestone 1a-2): finding the GitHub `owner/repo` a
/// remote names, turning an HTTP status into a verdict, the 24-hour cache value and the two notices. The HTTP call
/// and the `_meta` access are in `TotalRecall.Infrastructure.GitSync`.
module TotalRecall.Core.SyncPublic

open System
open System.Globalization
open System.Security.Cryptography
open System.Text

type GitHubSlug = { Owner: string; Repo: string }

/// What the check concluded. `Private` means GitHub answered 404 (private or missing); `Public` is a 200; `Unknown`
/// is any other answer or none; `Skipped` is a remote the check does not cover (another host, a local path).
type PublicVerdict =
    | Private
    | Public
    | Unknown of string
    | Skipped of string

let private githubSchemes = set [ "https"; "http"; "git"; "ssh"; "git+ssh"; "ssh+git" ]

let private isSlugChar (c: char) =
    (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '.' || c = '_' || c = '-'

/// The host of an authority: what follows the last `@`, without a numeric `:port`. `None` for a bad port.
let private hostOf (authority: string) : string option =
    // A URL authority ends at `/`, `?` or `#`, and a standard parser reads the host after the last `@` of what
    // is left; refuse every spelling on which parsers could disagree about where it ends.
    let suspicious (c: char) = c = '\\' || c = '?' || c = '#' || c = '%' || Char.IsWhiteSpace c || Char.IsControl c
    if authority |> Seq.exists suspicious then None else
    let afterUser =
        match authority.LastIndexOf '@' with
        | -1 -> authority
        | i -> authority.Substring(i + 1)
    match afterUser.IndexOf ':' with
    | -1 -> Some afterUser
    | i ->
        let port = afterUser.Substring(i + 1)
        if port.Length > 0 && port |> Seq.forall (fun c -> c >= '0' && c <= '9') then Some(afterUser.Substring(0, i)) else None

let private slugOfPath (path: string) : GitHubSlug option =
    let path = if path.EndsWith("/", StringComparison.Ordinal) then path.Substring(0, path.Length - 1) else path
    match path.Split('/') with
    | [| owner; repo |] when owner.Length > 0 && repo.Length > 0 && Seq.forall isSlugChar owner && Seq.forall isSlugChar repo ->
        let repo = if repo.EndsWith(".git", StringComparison.Ordinal) then repo.Substring(0, repo.Length - 4) else repo
        let bad (n: string) = n = "" || n = "." || n = ".."
        if bad owner || bad repo then None else Some { Owner = owner; Repo = repo }
    | _ -> None

/// The GitHub `owner/repo` a remote names, or `None` for any other host, an ssh alias, a local path or a path that
/// is not exactly `owner/repo`. User-info is ignored (and never leaves this function).
let parseGitHubSlug (remote: string) : GitHubSlug option =
    if String.IsNullOrWhiteSpace remote then None
    else
        let hostAndPath =
            match remote.IndexOf("://", StringComparison.Ordinal) with
            | e when e > 0 ->
                let scheme = remote.Substring(0, e).ToLowerInvariant()
                let rest = remote.Substring(e + 3)
                if not (githubSchemes.Contains scheme) then None
                else
                    match rest.IndexOf '/' with
                    | -1 -> None
                    | slash -> Some(rest.Substring(0, slash), rest.Substring(slash + 1))
            | _ ->
                match remote.IndexOf ':' with
                | c when c > 0 && not (remote.Substring(0, c).Contains '/') -> Some(remote.Substring(0, c), remote.Substring(c + 1))
                | _ -> None
        match hostAndPath with
        | None -> None
        | Some(authority, path) ->
            match hostOf authority with
            | Some host when String.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) -> slugOfPath path
            | _ -> None

/// 200 is public, 404 is private or missing, everything else (or no answer) is unknown.
let ofStatus (status: int option) : PublicVerdict =
    match status with
    | Some 200 -> Public
    | Some 404 -> Private
    | Some n -> Unknown(sprintf "GitHub answered %d" n)
    | None -> Unknown "no answer from GitHub"

/// The first 16 lowercase hex digits of the SHA-256 of `owner/repo`, lower-cased.
let slugKey (slug: GitHubSlug) : string =
    use sha = SHA256.Create()
    let hash = sha.ComputeHash(UTF8Encoding(false).GetBytes((slug.Owner + "/" + slug.Repo).ToLowerInvariant()))
    Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant()

/// The cache value: `v1|<slugKey>|<unix seconds>`.
let formatCache (slug: GitHubSlug) (unixSeconds: int64) : string =
    sprintf "v1|%s|%d" (slugKey slug) unixSeconds

/// The key and the time of a cache value, or `None` when it is not a `v1` value.
let parseCache (value: string) : (string * int64) option =
    if isNull value then None
    else
        match value.Split('|') with
        | [| "v1"; key; secs |] when key.Length > 0 ->
            match Int64.TryParse(secs, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, n -> Some(key, n)
            | _ -> None
        | _ -> None

/// A cached private answer is used when it is at most 24 hours old; one stamped more than 5 minutes in the future
/// is ignored (a clock set wrong must not pin "private").
let isFresh (nowUnix: int64) (cachedAt: int64) : bool =
    cachedAt - nowUnix <= 300L && nowUnix - cachedAt <= 86400L

/// The warning for an unknown answer. It carries no address.
let unknownNotice (reason: string) : string =
    sprintf "Could not check whether the sync repository is public (%s). Continuing; keep the repository private, because it holds your memories in plain text." reason

/// The notice for a remote the check does not cover. It carries no address.
let skippedNotice (reason: string) : string =
    sprintf "The public-repository check only covers github.com (%s). Keep the sync repository private: it holds your memories in plain text." reason
