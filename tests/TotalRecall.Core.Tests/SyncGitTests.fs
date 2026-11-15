module TotalRecall.Core.Tests.SyncGitTests

open Expecto
open FsCheck
open TotalRecall.Core.SyncGit

let private sha40 = String.replicate 40 "a"
let private sha64 = String.replicate 64 "b"
let private ctx : BuildContext =
    { CloneDir = "/d/repo"; NoHooksDir = "/d/no-hooks"; EmptyFile = "/d/empty"; Windows = false }

let private args (c: GitCommand) : string list = commandArgs c
let private built (c: GitCommand) : Result<string list, string> = build ctx c
let private isError (r: Result<'a, string>) = match r with Error _ -> true | Ok _ -> false

// ---------------------------------------------------------------------------
// every case with representative legal parameters

let private longBranch = String.replicate 100 "a"

let private allCommands : GitCommand list =
    [ Version
      ConfigGetSshCommand GlobalScope
      ConfigGetSshCommand SystemScope
      Init("main", "/d/repo")
      Init("a/b-c", "/d/repo")
      Clone("file:///r", "main", "/d/repo")
      Clone("https://h/o/r.git", longBranch, "/d/repo")
      LsRemote(Url "https://h/o/r.git", "main")
      LsRemote(Origin, "main")
      AddOrigin "https://h/o/r.git"
      SetOriginUrl "git@github.com:o/r.git"
      Fetch "main"
      Fetch "a/b-c"
      ResetHard Head
      ResetHard(Sha sha40)
      ResetHard(Sha sha64)
      ResetHard(RemoteBranch "main")
      ResetHard(LocalBranch "main")
      ResetHard LastSynced
      Clean
      IsAncestor(Sha sha40, Head)
      IsAncestor(LastSynced, RemoteBranch "main")
      RevParse Head
      RevParse LastSynced
      UpdateLastSynced sha40
      DiffTree(Sha sha40, Sha sha40)
      DiffTree(LastSynced, RemoteBranch "main")
      LsTree(Sha sha40)
      LsTree Head
      LsTreeRoot(Sha sha40)
      CatBlob sha40
      Stage Memories
      Stage Manifest
      Stage Attributes
      HasStaged
      Commit "sync: m +1 ~0 -0"
      Commit "quotes \" and $(x) and\nnewlines é"
      Push "main"
      Push "a/b-c" ]

[<Tests>]
let commandTableTests =
    testList "SyncGit.commandArgs: the closed command table" [
        testCase "Version and ConfigGetSshCommand" <| fun _ ->
            Expect.equal (args Version) [ "--version" ] "version"
            Expect.equal (args (ConfigGetSshCommand GlobalScope)) [ "config"; "--global"; "--get"; "core.sshCommand" ] "global"
            Expect.equal (args (ConfigGetSshCommand SystemScope)) [ "config"; "--system"; "--get"; "core.sshCommand" ] "system"

        testCase "Init and Clone carry the target directory after --" <| fun _ ->
            Expect.equal (args (Init("main", "/d/repo"))) [ "init"; "-b"; "main"; "--template="; "--"; "/d/repo" ] "init"
            Expect.equal
                (args (Clone("file:///r", "main", "/d/repo")))
                [ "clone"; "--depth=1"; "--no-tags"; "--single-branch"; "--branch"; "main"; "--template="; "--"; "file:///r"; "/d/repo" ]
                "clone"

        testCase "LsRemote lists one exact ref" <| fun _ ->
            Expect.equal (args (LsRemote(Url "https://h/o/r.git", "main"))) [ "ls-remote"; "--"; "https://h/o/r.git"; "refs/heads/main" ] "url"
            Expect.equal (args (LsRemote(Origin, "main"))) [ "ls-remote"; "--"; "origin"; "refs/heads/main" ] "origin"

        testCase "origin management" <| fun _ ->
            Expect.equal (args (AddOrigin "u")) [ "remote"; "add"; "origin"; "--"; "u" ] "add"
            Expect.equal (args (SetOriginUrl "u")) [ "remote"; "set-url"; "origin"; "--"; "u" ] "set-url"

        testCase "Fetch forces only the tracking ref of the disposable clone" <| fun _ ->
            Expect.equal
                (args (Fetch "main"))
                [ "fetch"; "--no-tags"; "--progress"; "origin"; "+refs/heads/main:refs/remotes/origin/main" ]
                "fetch"

        testCase "reset, clean, ancestry, rev-parse, update-ref" <| fun _ ->
            Expect.equal (args (ResetHard(Sha sha40))) [ "reset"; "--hard"; sha40 ] "reset sha"
            Expect.equal (args (ResetHard(RemoteBranch "main"))) [ "reset"; "--hard"; "refs/remotes/origin/main" ] "reset remote"
            Expect.equal (args Clean) [ "clean"; "-fd" ] "clean"
            Expect.equal (args (IsAncestor(Sha sha40, Head))) [ "merge-base"; "--is-ancestor"; sha40; "HEAD" ] "ancestor"
            Expect.equal (args (RevParse LastSynced)) [ "rev-parse"; "--verify"; "--quiet"; "refs/total-recall/last-synced^{commit}" ] "rev-parse"
            Expect.equal (args (UpdateLastSynced sha40)) [ "update-ref"; "refs/total-recall/last-synced"; sha40 ] "update-ref"

        testCase "tree reading" <| fun _ ->
            Expect.equal
                (args (DiffTree(Sha sha40, Sha sha64)))
                [ "diff-tree"; "-r"; "-z"; "--no-renames"; "--raw"; sha40; sha64; "--"; "memories" ]
                "diff-tree"
            Expect.equal (args (LsTree(Sha sha40))) [ "ls-tree"; "-r"; "-z"; "-l"; sha40; "--"; "memories" ] "ls-tree"
            Expect.equal (args (LsTreeRoot(Sha sha40))) [ "ls-tree"; "-z"; "-l"; sha40 ] "ls-tree root"
            Expect.equal (args (CatBlob sha40)) [ "cat-file"; "blob"; sha40 ] "cat-file"

        testCase "staging, committing and pushing" <| fun _ ->
            Expect.equal (args (Stage Memories)) [ "add"; "-A"; "--"; "memories" ] "stage memories"
            Expect.equal (args (Stage Manifest)) [ "add"; "-A"; "--"; "manifest.json" ] "stage manifest"
            Expect.equal (args (Stage Attributes)) [ "add"; "-A"; "--"; ".gitattributes" ] "stage attributes"
            Expect.equal (args HasStaged) [ "diff"; "--cached"; "--quiet"; "--exit-code" ] "has staged"
            Expect.equal (args (Commit "sync: m +1")) [ "commit"; "--no-verify"; "-m"; "sync: m +1" ] "commit"
            Expect.equal (args (Push "main")) [ "push"; "origin"; "HEAD:refs/heads/main" ] "push"
    ]

// ---------------------------------------------------------------------------
// never force

let private allowedDashed =
    set [ "--version"; "--global"; "--system"; "--get"; "-b"; "--template="; "--depth=1"; "--no-tags"; "--single-branch"
          "--branch"; "--progress"; "--hard"; "-fd"; "--is-ancestor"; "--verify"; "--quiet"; "-r"; "-z"; "--no-renames"
          "--raw"; "-l"; "-A"; "--cached"; "--exit-code"; "--no-verify"; "-m"; "-c"; "--" ]

let private forbidden =
    [ "--force"; "-f"; "--force-with-lease"; "--delete"; "--mirror"; "--prune"; "-d"; "-D"; "--all"; "--tags" ]

let private checkNeverForce (what: string) (argv: string list) =
    for a in argv do
        Expect.isFalse (List.contains a forbidden) (sprintf "%s: %s must not appear in %A" what a argv)
    // `-no-verify` only for Commit; `+` only in the fetch refspec; a leading ':' refspec (delete) never
    let plus = argv |> List.filter (fun a -> a.StartsWith "+")
    for p in plus do
        Expect.isTrue (p.StartsWith "+refs/heads/" && p.Contains ":refs/remotes/origin/") (sprintf "%s: stray + argument %s" what p)
    Expect.isFalse (argv |> List.exists (fun a -> a.StartsWith ":")) (sprintf "%s: a delete refspec in %A" what argv)

[<Tests>]
let neverForceTests =
    testList "SyncGit: no command can force, delete or mirror" [
        testCase "no listed command, built or bare, contains a forbidden argument" <| fun _ ->
            for c in allCommands do
                checkNeverForce "commandArgs" (args c)
                match built c with
                | Ok full -> checkNeverForce "build" full
                | Error e -> failtestf "build failed for %A: %s" c e

        testCase "-f appears only as the -fd of Clean, and --no-verify only in Commit" <| fun _ ->
            for c in allCommands do
                let a = args c
                match c with
                | Clean -> Expect.equal a [ "clean"; "-fd" ] "clean"
                | _ -> Expect.isFalse (a |> List.exists (fun x -> x = "-fd" || x = "-f")) (sprintf "%A" c)
                match c with
                | Commit _ -> ()
                | _ -> Expect.isFalse (List.contains "--no-verify" a) (sprintf "%A" c)

        testCase "every dashed argument of a built command is on the fixed allow-set" <| fun _ ->
            for c in allCommands do
                match built c with
                | Ok full ->
                    for a in full do
                        if a.StartsWith "-" && not (a.StartsWith "--git-dir=") && not (a.StartsWith "--work-tree=") then
                            // the -c list values (k=v) do not start with '-', so only flags are checked here
                            Expect.isTrue (allowedDashed.Contains a) (sprintf "unlisted flag %s in %A" a full)
                | Error e -> failtestf "build failed: %s" e

        testCase "Push is exactly push origin HEAD:refs/heads/<b> after the overrides" <| fun _ ->
            for b in [ "main"; "a/b-c"; longBranch ] do
                match built (Push b) with
                | Ok full ->
                    let n = List.length full
                    Expect.equal (full |> List.skip (n - 3)) [ "push"; "origin"; "HEAD:refs/heads/" + b ] "tail"
                | Error e -> failtestf "%s" e

        testProperty "random legal branches never put a force or delete in Fetch, Push or Clone" <| fun (seed: int) ->
            let alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-_."
            let rng = System.Random(seed)
            let name =
                "a" + System.String(Array.init (rng.Next(0, 30)) (fun _ -> alphabet.[rng.Next alphabet.Length]))
            // only legal names are meaningful: the others are refused by build
            let cmds = [ Fetch name; Push name; Clone("file:///r", name, "/d/repo"); ResetHard(RemoteBranch name) ]
            cmds
            |> List.forall (fun c ->
                match built c with
                | Ok full -> not (full |> List.exists (fun a -> List.contains a forbidden))
                | Error _ -> true)
    ]

// ---------------------------------------------------------------------------
// addresses

[<Tests>]
let remoteTests =
    testList "SyncGit.classifyRemote and validation" [
        testCase "accepted shapes" <| fun _ ->
            for s, k in
                [ "https://h/o/r.git", UrlRemote
                  "https://[::1]/x", UrlRemote
                  "ssh://git@h/o/r.git", UrlRemote
                  "file:///tmp/r", UrlRemote
                  "git@github.com:o/r.git", ScpLike
                  "github.com:o/r.git", ScpLike
                  "1http://x", UrlRemote
                  "/abs/path", AbsolutePath
                  "\\\\server\\share\\r", AbsolutePath ] do
                Expect.equal (classifyRemote false s) (Some k) s
                Expect.isFalse (isError (validateRemote false s)) s

        testCase "drive-letter paths are absolute only on Windows" <| fun _ ->
            for s in [ "C:\\r"; "C:/r"; "d:\\repos\\x.git" ] do
                Expect.equal (classifyRemote true s) (Some AbsolutePath) (s + " on windows")
                Expect.isNone (classifyRemote false s) (s + " elsewhere")
                Expect.isTrue (isError (validateRemote false s)) s

        testCase "refused shapes" <| fun _ ->
            for s in
                [ ""; "-oProxyCommand=x"; "-"; "a b"; "a\tb"; "a\u0000b"; "https://h/x y"
                  "ext::sh -c x"; "ext::x"; "fd::1"; "rsync::x"; "ext::https://u:tok@h/x"
                  "repo"; "../x"; "./x"; "a:b"; "x:y"; "a:"; "user@:path"; "99::x"; "1ext::x" ] do
                Expect.isNone (classifyRemote false s) (sprintf "%A" s)
                Expect.isTrue (isError (validateRemote false s)) (sprintf "%A" s)

        testCase "a refused address never echoes user-info in the message" <| fun _ ->
            for s in [ "https://u:tok@h/x y"; "ext::https://u:tok@h/x" ] do
                match validateRemote false s with
                | Error m ->
                    Expect.isFalse (m.Contains "tok") (sprintf "token in %s" m)
                    Expect.isFalse (m.Contains "u:") (sprintf "user in %s" m)
                | Ok () -> failtestf "%s should be refused" s
                match built (LsRemote(Url s, "main")) with
                | Error m ->
                    Expect.isFalse (m.Contains "tok") m
                    Expect.isFalse (m.Contains "u:") m
                | Ok _ -> failtestf "build should refuse %s" s
    ]

// ---------------------------------------------------------------------------
// build: validation and shape

[<Tests>]
let buildTests =
    testList "SyncGit.build" [
        testCase "invalid branches, revisions, directories and messages are refused" <| fun _ ->
            let bad =
                [ Fetch "-x"; Fetch ""; Fetch "a..b"; Fetch "a//b"; Fetch "a/"; Fetch "a."; Fetch "x.lock"; Fetch "a/.b"
                  Fetch(String.replicate 101 "a"); Fetch "a b"; Push "-x"; Push "bad branch"
                  Clone("file:///r", "-x", "/d/repo"); Init("a b", "/d/repo")
                  LsRemote(Url "file:///r", "a..b")
                  ResetHard(Sha "abc"); ResetHard(Sha(String.replicate 40 "A")); ResetHard(Sha(String.replicate 39 "a"))
                  ResetHard(Sha(String.replicate 41 "a")); CatBlob "xyz"; UpdateLastSynced "nothex"
                  ResetHard(RemoteBranch "-x"); ResetHard(LocalBranch "a..b")
                  Commit ""; Commit "a\u0000b"
                  Init("main", ""); Init("main", "a\u0000b"); Clone("file:///r", "main", "")
                  Init("main", "relative/dir"); Clone("file:///r", "main", "relative/dir") ]
            for c in bad do
                Expect.isTrue (isError (built c)) (sprintf "%A must be refused" c)

        testCase "a relative CloneDir is refused for a clone-scoped command" <| fun _ ->
            let rel = { ctx with CloneDir = "relative/repo" }
            Expect.isTrue (isError (build rel (Fetch "main"))) "clone-scoped, relative"
            Expect.isFalse (isError (build rel Version)) "not clone-scoped"

        testCase "valid values are accepted" <| fun _ ->
            for c in allCommands do
                Expect.isFalse (isError (built c)) (sprintf "%A" c)

        testCase "a clone-scoped command names its repository, then the overrides, then the subcommand" <| fun _ ->
            match built (Fetch "main") with
            | Ok full ->
                Expect.equal (full |> List.take 2) [ "--git-dir=/d/repo/.git"; "--work-tree=/d/repo" ] "repository"
                Expect.equal (full.[2]) "-c" "overrides start"
                Expect.equal (full |> List.skip (List.length full - 5)) (args (Fetch "main")) "subcommand last"
            | Error e -> failtestf "%s" e

        testCase "commands that are not clone-scoped have no --git-dir" <| fun _ ->
            for c in [ Version; ConfigGetSshCommand GlobalScope; Init("main", "/d/repo"); Clone("file:///r", "main", "/d/repo"); LsRemote(Url "file:///r", "main") ] do
                match built c with
                | Ok full -> Expect.isFalse (full |> List.exists (fun a -> a.StartsWith "--git-dir")) (sprintf "%A" c)
                | Error e -> failtestf "%s" e
            match built (LsRemote(Origin, "main")) with
            | Ok full -> Expect.isTrue (full |> List.exists (fun a -> a.StartsWith "--git-dir=")) "origin is clone-scoped"
            | Error e -> failtestf "%s" e

        testCase "Version and ConfigGetSshCommand take no overrides at all" <| fun _ ->
            Expect.equal (built Version) (Ok [ "--version" ]) "version"
            Expect.equal (built (ConfigGetSshCommand GlobalScope)) (Ok (args (ConfigGetSshCommand GlobalScope))) "config"
            Expect.isFalse (takesOverrides Version) "version"
            Expect.isFalse (takesOverrides (ConfigGetSshCommand SystemScope)) "config"
            Expect.isTrue (takesOverrides Clean) "clean"

        testCase "every command but the two config readers carries the overrides" <| fun _ ->
            for c in allCommands do
                match c, built c with
                | (Version | ConfigGetSshCommand _), Ok full -> Expect.isFalse (List.contains "-c" full) (sprintf "%A" c)
                | _, Ok full ->
                    Expect.isTrue (List.contains "protocol.ext.allow=never" full) (sprintf "ext in %A" c)
                    Expect.isTrue (List.contains "core.hooksPath=/d/no-hooks" full) (sprintf "hooks in %A" c)
                    Expect.isTrue (List.contains "core.askPass=" full) (sprintf "askpass in %A" c)
                | _, Error e -> failtestf "%s" e

        testCase "Windows drive-letter remotes pass build only with Windows = true" <| fun _ ->
            let win = { ctx with Windows = true }
            for r in [ "C:\\r"; "C:/r" ] do
                Expect.isFalse (isError (build win (Clone(r, "main", "/d/repo")))) (r + " on windows")
                Expect.isFalse (isError (build win (LsRemote(Url r, "main")))) (r + " on windows")
                Expect.isTrue (isError (build ctx (Clone(r, "main", "/d/repo")))) (r + " elsewhere")
                Expect.isTrue (isError (build ctx (LsRemote(Url r, "main")))) (r + " elsewhere")

        testCase "empty hooks or attributes paths are refused, and so is a root clone directory" <| fun _ ->
            Expect.isTrue (isError (build { ctx with NoHooksDir = "" } Clean)) "empty hooks dir"
            Expect.isTrue (isError (build { ctx with EmptyFile = "" } Clean)) "empty file"
            Expect.isFalse (isError (build { ctx with NoHooksDir = "" } Version)) "version takes no overrides"
            for root in [ "/"; "//"; "C:\\"; "C:/" ] do
                Expect.isTrue (isError (build { ctx with CloneDir = root } Clean)) (sprintf "clone dir %s" root)
            Expect.isFalse (isError (build { ctx with CloneDir = "/d/repo/" } Clean)) "trailing slash is fine"

        testCase "isCloneScoped" <| fun _ ->
            for c in [ Fetch "main"; ResetHard Head; Clean; Stage Memories; Push "main"; LsRemote(Origin, "main"); AddOrigin "u"; HasStaged; CatBlob sha40 ] do
                Expect.isTrue (isCloneScoped c) (sprintf "%A" c)
            for c in [ Version; ConfigGetSshCommand GlobalScope; Init("main", "/d/r"); Clone("file:///r", "main", "/d/r"); LsRemote(Url "file:///r", "main") ] do
                Expect.isFalse (isCloneScoped c) (sprintf "%A" c)
    ]

// ---------------------------------------------------------------------------
// overrides and environment

[<Tests>]
let overridesTests =
    testList "SyncGit.overrides and the environment" [
        testCase "the -c list is exactly the spec's, in order" <| fun _ ->
            let expected =
                [ "core.autocrlf=false"; "core.symlinks=false"; "core.longpaths=true"; "commit.gpgsign=false"
                  "core.hooksPath=/h"; "user.name=total-recall"; "user.email=noreply@total-recall.local"
                  "credential.interactive=never"; "core.quotepath=false"; "protocol.ext.allow=never"
                  "protocol.fd.allow=never"; "core.askPass="; "core.attributesFile=/e"; "core.excludesFile=/e"
                  "core.fsmonitor="; "push.gpgSign=false"; "http.lowSpeedLimit=1000"; "http.lowSpeedTime=20"
                  "gc.auto=0"; "maintenance.auto=false" ]
            let got = overrides "/h" "/e"
            Expect.equal (got |> List.mapi (fun i x -> i % 2 = 0, x) |> List.filter (fun (isFlag, _) -> isFlag) |> List.map snd |> List.distinct) [ "-c" ] "every key is preceded by -c"
            Expect.equal (got |> List.mapi (fun i x -> i % 2 = 1, x) |> List.filter fst |> List.map snd) expected "values"

        testCase "the overrides sit between the repository selector and the subcommand" <| fun _ ->
            match built Clean with
            | Ok full -> Expect.isTrue (full |> List.contains "core.hooksPath=/d/no-hooks") "hooks path"
            | Error e -> failtestf "%s" e

        testCase "isAllowedEnvName: kept names" <| fun _ ->
            for n in
                [ "PATH"; "HOME"; "SSH_AUTH_SOCK"; "HTTPS_PROXY"; "GIT_SSH"; "GIT_SSH_COMMAND"; "git_ssh_command"
                  "GIT_SSH_VARIANT"; "GIT_SSL_CAINFO"; "GIT_SSL_NO_VERIFY"; "GIT_PROXY_COMMAND"; "GIT_CONFIG_GLOBAL"
                  "GIT_CONFIG_SYSTEM"; "GIT_CONFIG_NOSYSTEM"; "GIT_EXEC_PATH" ] do
                Expect.isTrue (isAllowedEnvName n) n

        testCase "isAllowedEnvName: dropped names" <| fun _ ->
            for n in
                [ "GIT_DIR"; "GIT_WORK_TREE"; "GIT_INDEX_FILE"; "GIT_OBJECT_DIRECTORY"; "GIT_ALTERNATE_OBJECT_DIRECTORIES"
                  "GIT_COMMON_DIR"; "GIT_NAMESPACE"; "GIT_PREFIX"; "GIT_CONFIG_PARAMETERS"; "GIT_CONFIG_COUNT"
                  "GIT_CONFIG_KEY_0"; "GIT_CONFIG_VALUE_0"; "GIT_AUTHOR_NAME"; "GIT_COMMITTER_EMAIL"; "GIT_TRACE"
                  "GIT_TRACE2_EVENT"; "GIT_CURL_VERBOSE"; "GIT_ASKPASS"; "git_dir"; "Git_Work_Tree" ] do
                Expect.isFalse (isAllowedEnvName n) n

        testCase "scrubEnvironment drops exactly the disallowed names, keeps order, and drops SSH_ASKPASS" <| fun _ ->
            let input =
                [ "PATH", "/bin"; "GIT_DIR", "/x"; "HOME", "/h"; "SSH_ASKPASS", "a"; "GIT_SSH_COMMAND", "ssh"
                  "GIT_AUTHOR_NAME", "evil"; "GIT_SSL_CAINFO", "c"; "LANG", "en" ]
            Expect.equal
                (scrubEnvironment input)
                [ "PATH", "/bin"; "HOME", "/h"; "GIT_SSH_COMMAND", "ssh"; "GIT_SSL_CAINFO", "c"; "LANG", "en" ]
                "scrubbed"

        testCase "fixedEnvironment is the three pairs" <| fun _ ->
            Expect.equal fixedEnvironment [ "GIT_TERMINAL_PROMPT", "0"; "LC_ALL", "C"; "LANGUAGE", "C" ] "fixed"
            Expect.equal removedEnvironmentNames [ "SSH_ASKPASS" ] "removed"
    ]
