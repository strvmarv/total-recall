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
      ConfigGetSshCommand
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
            Expect.equal (args ConfigGetSshCommand) [ "config"; "--includes"; "--get"; "core.sshCommand" ] "config"

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
            Expect.equal (args (Stage Memories)) [ "add"; "-A"; "-f"; "--"; "memories" ] "stage memories"
            Expect.equal (args (Stage Manifest)) [ "add"; "-A"; "-f"; "--"; "manifest.json" ] "stage manifest"
            Expect.equal (args (Stage Attributes)) [ "add"; "-A"; "-f"; "--"; ".gitattributes" ] "stage attributes"
            Expect.equal (args HasStaged) [ "diff"; "--cached"; "--quiet"; "--exit-code" ] "has staged"
            Expect.equal (args (Commit "sync: m +1")) [ "commit"; "--no-verify"; "-m"; "sync: m +1" ] "commit"
            Expect.equal (args (Push "main")) [ "push"; "origin"; "HEAD:refs/heads/main" ] "push"
    ]

// ---------------------------------------------------------------------------
// never force

let private allowedDashed =
    set [ "--version"; "--get"; "-b"; "--template="; "--depth=1"; "--no-tags"; "--single-branch"
          "--branch"; "--progress"; "--hard"; "-fd"; "--includes"; "--is-ancestor"; "--verify"; "--quiet"; "-r"; "-z"; "--no-renames"
          "--raw"; "-l"; "-A"; "-f"; "--cached"; "--exit-code"; "--no-verify"; "-m"; "-c"; "--" ]

let private forbidden =
    [ "--force"; "-f"; "--force-with-lease"; "--delete"; "--mirror"; "--prune"; "-d"; "-D"; "--all"; "--tags" ]

let private checkNeverForce (what: string) (argv: string list) =
    // `add -A -f` is the one `-f` besides `clean -fd`: it only affects what is staged and never reaches a remote
    let isStage = List.contains "add" argv
    for a in argv do
        if not (a = "-f" && isStage) then
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

        testCase "-f appears only as the -fd of Clean and the -f of Stage, and --no-verify only in Commit" <| fun _ ->
            for c in allCommands do
                let a = args c
                match c with
                | Clean -> Expect.equal a [ "clean"; "-fd" ] "clean"
                | Stage _ -> Expect.equal (a |> List.filter (fun x -> x.StartsWith "-" && x <> "--")) [ "-A"; "-f" ] "stage"
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
                  "git://h/o/r.git", UrlRemote
                  "git+ssh://h/o/r.git", UrlRemote
                  "file:///tmp/r", UrlRemote
                  "git@github.com:o/r.git", ScpLike
                  "github.com:o/r.git", ScpLike
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
                  "repo"; "../x"; "./x"; "a:b"; "x:y"; "a:"; "user@:path"; "99::x"; "1ext::x"
                  // a scheme outside the allow-list makes git run `git-remote-<scheme>`; a case variant of a refused helper too
                  "Ext://x"; "HTTPS://h/x"; "SSH://h/x"; "File:///x"; "ext://x"; "fd://x"; "ftp://h/x"; "gopher://x"; "rsync://h/x"; "1http://x"; "git-lfs://h/x"; "a.b://x" ] do
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
            for c in [ Version; ConfigGetSshCommand; Init("main", "/d/repo"); Clone("file:///r", "main", "/d/repo"); LsRemote(Url "file:///r", "main") ] do
                match built c with
                | Ok full -> Expect.isFalse (full |> List.exists (fun a -> a.StartsWith "--git-dir")) (sprintf "%A" c)
                | Error e -> failtestf "%s" e
            match built (LsRemote(Origin, "main")) with
            | Ok full -> Expect.isTrue (full |> List.exists (fun a -> a.StartsWith "--git-dir=")) "origin is clone-scoped"
            | Error e -> failtestf "%s" e

        testCase "Version and ConfigGetSshCommand take no overrides at all" <| fun _ ->
            Expect.equal (built Version) (Ok [ "--version" ]) "version"
            Expect.equal (built ConfigGetSshCommand) (Ok (args ConfigGetSshCommand)) "config"
            Expect.isFalse (takesOverrides Version) "version"
            Expect.isFalse (takesOverrides ConfigGetSshCommand) "config"
            Expect.isTrue (takesOverrides Clean) "clean"

        testCase "every command but the two config readers carries the overrides" <| fun _ ->
            for c in allCommands do
                match c, built c with
                | (Version | ConfigGetSshCommand), Ok full -> Expect.isFalse (List.contains "-c" full) (sprintf "%A" c)
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
            for c in [ Version; ConfigGetSshCommand; Init("main", "/d/r"); Clone("file:///r", "main", "/d/r"); LsRemote(Url "file:///r", "main") ] do
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
                  "gc.auto=0"; "maintenance.auto=false"; "clone.defaultRemoteName=origin"; "init.defaultObjectFormat=sha1"
                  "init.defaultRefFormat=files" ]
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

// ===========================================================================
// part B: failures, redaction, ssh, keys, ls-remote

let private input (stderr: string) : FailureInput =
    { ExitCode = Some 128; TimedOut = false; Cancelled = false; SpawnFailed = false; OutputTooLarge = false; Stderr = stderr }

let private kindOf (stderr: string) : FailureKind = classifyFailure (input stderr)

[<Tests>]
let classifyTests =
    testList "SyncGit.classifyFailure: the ordered fragment table of decision 12" [
        testCase "Auth" <| fun _ ->
            for s in
                [ "fatal: could not read Username for 'https://github.com': terminal prompts disabled"
                  "fatal: unable to get password from user"
                  "git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository."
                  "fatal: Authentication failed for 'https://github.com/o/r.git/'"
                  "Host key verification failed.\nfatal: Could not read from remote repository."
                  "error: The requested URL returned error: 401"
                  "fatal: unable to access 'https://h/': The requested URL returned error: 403"
                  "remote: Invalid username or password."
                  "fatal: could not read Password for 'https://u@h': terminal prompts disabled"
                  "fatal: unable to access 'https://h/': The requested URL returned error: HTTP 401"
                  "remote: HTTP 403 forbidden" ] do
                Expect.equal (kindOf s) Auth s

        testCase "RemoteNotFound" <| fun _ ->
            for s in
                [ "fatal: '/x/missing' does not appear to be a git repository\nfatal: Could not read from remote repository."
                  "ERROR: Repository not found.\nfatal: Could not read from remote repository."
                  "fatal: repository '/abs/missing' does not exist"
                  "fatal: repository 'https://h/o/r.git/' not found"
                  "error: The requested URL returned error: 404"
                  "remote: HTTP 404 not found" ] do
                Expect.equal (kindOf s) RemoteNotFound s

        testCase "RemoteBranchMissing" <| fun _ ->
            for s in
                [ "warning: Could not find remote branch main to clone.\nfatal: Remote branch main not found in upstream origin"
                  "fatal: couldn't find remote ref refs/heads/main" ] do
                Expect.equal (kindOf s) RemoteBranchMissing s

        testCase "NonFastForward" <| fun _ ->
            for s in
                [ " ! [rejected]        HEAD -> main (non-fast-forward)"
                  " ! [rejected]        HEAD -> main (fetch first)"
                  "error: failed to push some refs to 'x'\nhint: Updates were rejected because the remote contains work" ] do
                Expect.equal (kindOf s) NonFastForward s

        testCase "Network" <| fun _ ->
            for s in
                [ "ssh: Could not resolve hostname x: Name or service not known\nfatal: Could not read from remote repository."
                  "ssh: connect to host h port 22: Connection refused"
                  "fatal: unable to access 'https://h/': Failed to connect to h port 443 after 5 ms: Couldn't connect to server"
                  "fatal: unable to access 'https://h/': Could not resolve host: h"
                  "fatal: unable to access 'https://h/': Connection timed out after 20 ms"
                  "fatal: the remote end hung up unexpectedly\nfatal: early EOF"
                  "error: RPC failed; curl 28 Operation too slow"
                  "kex_exchange_identification: Connection closed by remote host"
                  "fatal: unable to access 'https://h/': Couldn't connect to server"
                  "fatal: unable to access 'https://h/': Operation too slow. Less than 1000 bytes/sec transferred the last 20 seconds"
                  "fatal: unable to access 'https://h/': Operation timed out after 5001 milliseconds"
                  "ssh: connect to host h port 22: Network is unreachable"
                  "ssh: connect to host h port 22: No route to host"
                  "Connection reset by peer" ] do
                Expect.equal (kindOf s) Network s

        testCase "ordered fragments need their order, and 'curl' must be a curl code" <| fun _ ->
            Expect.equal (kindOf "' not found while repository '") Other "wrong order, not found"
            Expect.equal (kindOf "' does not exist, the repository '") Other "wrong order, does not exist"
            Expect.equal (kindOf " not found in upstream, remote branch ") Other "wrong order, branch"
            Expect.equal (kindOf "fatal: unable to access 'https://github.com/curl/curl.git/': SSL certificate problem") Other "curl in a name"

        testCase "NotARepository" <| fun _ ->
            Expect.equal (kindOf "fatal: not a git repository: '/x/.git'") NotARepository "not a repo"

        testCase "Other" <| fun _ ->
            for s in
                [ "fatal: Could not read from remote repository."
                  ""
                  "remote: error: GH013: Repository rule violations found\n! [remote rejected] main -> main (push declined due to repository rule violations)"
                  "fatal: unrecognized argument" ] do
                Expect.equal (kindOf s) Other (sprintf "%A" s)

        testCase "the order decides: Auth before Network, a specific line beats the generic trailer" <| fun _ ->
            Expect.equal (kindOf "Permission denied (publickey).\nssh: Could not resolve hostname x") Auth "auth first"
            Expect.equal (kindOf "fatal: repository 'x' not found\nfatal: Could not read from remote repository.") RemoteNotFound "specific"
            Expect.equal (kindOf "Connection refused\nfatal: not a git repository") Network "network before notarepo"

        testCase "matching is case-insensitive" <| fun _ ->
            Expect.equal (kindOf "PERMISSION DENIED (PUBLICKEY).") Auth "upper"
            Expect.equal (kindOf "connection REFUSED") Network "mixed"

        testCase "flags are decided before any fragment, in the stated order" <| fun _ ->
            let text = "Permission denied (publickey)."
            Expect.equal (classifyFailure { input text with SpawnFailed = true }) NoGit "spawn"
            Expect.equal (classifyFailure { input text with TimedOut = true }) TimedOut "timeout"
            Expect.equal (classifyFailure { input text with Cancelled = true }) Cancelled "cancelled"
            Expect.equal (classifyFailure { input text with OutputTooLarge = true }) OutputTooLarge "large"
            Expect.equal (classifyFailure { input text with SpawnFailed = true; TimedOut = true; Cancelled = true; OutputTooLarge = true }) NoGit "all four"
            Expect.equal (classifyFailure { input text with TimedOut = true; Cancelled = true; OutputTooLarge = true }) TimedOut "three"
            Expect.equal (classifyFailure { input text with Cancelled = true; OutputTooLarge = true }) Cancelled "two"

        testProperty "total, and a flag always wins over the text" <| fun (text: string) (a: bool) (b: bool) (c: bool) (d: bool) ->
            let i = { input (if isNull text then "" else text) with SpawnFailed = a; TimedOut = b; Cancelled = c; OutputTooLarge = d }
            let k = classifyFailure i
            if a then k = NoGit
            elif b then k = TimedOut
            elif c then k = Cancelled
            elif d then k = OutputTooLarge
            else k <> NoGit && k <> TimedOut && k <> Cancelled && k <> OutputTooLarge && k <> GitTooOld
    ]

[<Tests>]
let versionTests =
    testList "SyncGit.parseVersion" [
        testCase "parses the first two numbers" <| fun _ ->
            Expect.equal (parseVersion "git version 2.43.0") (Some(2, 43)) "plain"
            Expect.equal (parseVersion "git version 2.45.1.windows.1\n") (Some(2, 45)) "windows"
            Expect.equal (parseVersion "git version 2.39.5 (Apple Git-154)") (Some(2, 39)) "apple"
            Expect.isNone (parseVersion "garbage") "garbage"
            Expect.isNone (parseVersion "") "empty"
            Expect.isNone (parseVersion null) "null"
        testCase "the minimum is 2.28" <| fun _ ->
            Expect.isTrue (isSupportedVersion (2, 28)) "2.28"
            Expect.isTrue (isSupportedVersion (2, 56)) "2.56"
            Expect.isTrue (isSupportedVersion (3, 0)) "3.0"
            Expect.isFalse (isSupportedVersion (2, 27)) "2.27"
            Expect.isFalse (isSupportedVersion (1, 9)) "1.9"
    ]

[<Tests>]
let redactTests =
    testList "SyncGit.redact, userInfoOf, cleanStderr" [
        testCase "the user-info of every URL becomes ***" <| fun _ ->
            Expect.equal (redact [] "https://user:ghp_abc@github.com/o/r.git") "https://***@github.com/o/r.git" "password"
            Expect.equal (redact [] "ssh://git@h/x") "ssh://***@h/x" "no password"
            Expect.equal (redact [] "https://h/o/r") "https://h/o/r" "none"
            Expect.equal (redact [] "a https://u:p@h/x and ssh://k@h2/y") "a https://***@h/x and ssh://***@h2/y" "two"
            Expect.equal (redact [] "'https://u:p@h' x") "'https://***@h' x" "quote ends"
            Expect.equal (redact [] "https://u:p@h x") "https://***@h x" "space ends"
            Expect.equal (redact [] "https://u:p@ss@h/x") "https://***@h/x" "last @ before the path"
            Expect.equal (redact [] "git@github.com:o/r.git") "git@github.com:o/r.git" "scp-like carries no credential"
            Expect.equal (redact [] "mail me@x.com") "mail me@x.com" "@ without ://"
            Expect.equal (redact [] "https://h/a@b") "https://h/a@b" "an @ after the path is not user-info"
            Expect.equal (redact [] "https://h x@y.com") "https://h x@y.com" "whitespace ends the authority"
            Expect.equal (redact [] "'https://h'@x.com") "'https://h'@x.com" "a quote ends the authority"

        testCase "caller secrets are replaced wherever they appear, before the URL rule" <| fun _ ->
            Expect.equal (redact [ "ghp_abc" ] "token ghp_abc seen") "token *** seen" "literal"
            Expect.equal (redact [ "p@ss" ] "https://u:p@ss@h/x") "https://***@h/x" "literal first"
            Expect.equal (redact [ "" ] "x") "x" "an empty secret is ignored"
            Expect.equal (redact [ "pass1234"; "user:pass1234" ] "x user:pass1234 y") "x *** y" "the longer secret first"
            Expect.equal (redact [ "abcdef"; "defghi" ] "abcdefghi") "***ghi" "overlapping secrets: no extra leak beyond the longest-first rule"

        testCase "idempotent" <| fun _ ->
            let once = redact [ "zzz" ] "https://u:p@h/x zzz https://k@h2"
            Expect.equal (redact [ "zzz" ] once) once "twice"

        testProperty "random credentials never survive" <| fun (a: uint32) (b: uint32) ->
            let user = "USR" + string a
            let pass = "PW" + string b
            let out = redact [] ("see https://" + user + ":" + pass + "@h/x")
            not (out.Contains user) && not (out.Contains pass)

        testCase "userInfoOf" <| fun _ ->
            Expect.equal (userInfoOf "https://user:pw@h/x") (Some "user:pw") "url"
            Expect.equal (userInfoOf "git@github.com:o/r.git") None "scp-like"
            Expect.equal (userInfoOf "ssh://git@h/x") (Some "git") "ssh"
            Expect.equal (userInfoOf "https://h/x") None "none"
            Expect.equal (userInfoOf "x https://u:p@ss@h/x") (Some "u:p@ss") "last @"
            Expect.equal (userInfoOf null) None "null"

        testCase "cleanStderr keeps lines, drops progress rewrites, handles CRLF" <| fun _ ->
            Expect.equal (cleanStderr [] "a\nb\n") "a\nb\n" "plain"
            let out = cleanStderr [] "Receiving objects: 10%\rReceiving objects: 100%\rdone\nerror: boom\n"
            Expect.isTrue (out.Contains "done") "done kept"
            Expect.isTrue (out.Contains "error: boom") "error kept"
            Expect.isFalse (out.Contains "10%") "progress dropped"
            Expect.equal (cleanStderr [] "Permission denied\r\n") "Permission denied\n" "crlf"
            Expect.equal (cleanStderr [] "x\r") "x" "a trailing CR is dropped first"
            Expect.equal (cleanStderr [] null) "" "null"

        testCase "cleanStderr keeps the last 2048 code units" <| fun _ ->
            let big = String.replicate 5 (String.replicate 999 "a" + "\n") + "END"
            let out = cleanStderr [] big
            Expect.equal out.Length maxStderrKeptChars "length"
            Expect.isTrue (out.EndsWith "END") "the tail"

        testCase "the cut never starts on half a surrogate pair" <| fun _ ->
            let out = cleanStderr [] ("\U0001F600" + String.replicate 2047 "a")
            Expect.isFalse (System.Char.IsLowSurrogate out.[0]) "no lone low surrogate"
            Expect.isTrue (out.Length <= maxStderrKeptChars) "within the limit"

        testCase "a token cannot be half-visible at the cut" <| fun _ ->
            let url = "fatal: https://user:SECRETTOKEN@h/x\n"
            for pad in [ 2000 .. 2050 ] do
                let out = cleanStderr [] (url + String.replicate pad "z")
                Expect.isFalse (out.Contains "SECRETTOKEN") (sprintf "pad %d" pad)
                Expect.isFalse (out.Contains "user:") (sprintf "pad %d" pad)
            Expect.equal maxStderrKeptChars 2048 "constant"
    ]

[<Tests>]
let sshTests =
    testList "SyncGit.composeSshCommand" [
        let none : SshSource = { EnvCommand = None; ConfigCommand = None; GitSsh = None; Variant = None }
        let opts = "-o BatchMode=yes -o ConnectTimeout=8"
        testCase "defaults to ssh with the two options" <| fun _ ->
            Expect.equal (composeSshCommand none) (Some("ssh " + opts)) "default"
        testCase "the process's command, then the config's, and blanks are absent" <| fun _ ->
            Expect.equal (composeSshCommand { none with EnvCommand = Some "ssh -i k" }) (Some("ssh -i k " + opts)) "env"
            Expect.equal
                (composeSshCommand { none with EnvCommand = Some "ssh -i env"; ConfigCommand = Some "ssh -i cfg" })
                (Some("ssh -i env " + opts))
                "env beats config"
            Expect.equal (composeSshCommand { none with EnvCommand = Some "  "; ConfigCommand = Some "ssh -i cfg" }) (Some("ssh -i cfg " + opts)) "blank env"
            Expect.equal
                (composeSshCommand { none with ConfigCommand = Some "C:/Windows/System32/OpenSSH/ssh.exe" })
                (Some("C:/Windows/System32/OpenSSH/ssh.exe " + opts))
                "config path"
        testCase "GIT_SSH alone leaves the environment alone" <| fun _ ->
            Expect.isNone (composeSshCommand { none with GitSsh = Some "/x/plink" }) "git_ssh"
            Expect.equal
                (composeSshCommand { none with GitSsh = Some "/x/plink"; EnvCommand = Some "ssh" })
                (Some("ssh " + opts))
                "a command beats GIT_SSH"
        testCase "plink and putty get -batch and no -o options" <| fun _ ->
            Expect.equal (composeSshCommand { none with EnvCommand = Some "plink -ssh" }) (Some "plink -ssh -batch") "plink"
            Expect.equal (composeSshCommand { none with Variant = Some "plink" }) (Some "ssh -batch") "variant"
            Expect.equal (composeSshCommand { none with Variant = Some "TortoisePlink" }) (Some "ssh -batch") "tortoise variant"
            Expect.equal (composeSshCommand { none with EnvCommand = Some "putty.exe" }) (Some "putty.exe -batch") "putty"
            Expect.equal
                (composeSshCommand { none with EnvCommand = Some "\"C:/Program Files/PuTTY/plink.exe\" -ssh" })
                (Some "\"C:/Program Files/PuTTY/plink.exe\" -ssh -batch")
                "a quoted path"
            Expect.equal (composeSshCommand { none with EnvCommand = Some "ssh -i ~/plinkkey" }) (Some("ssh -i ~/plinkkey " + opts)) "only the program counts"
        testCase "quotes, backslashes and case in the program path" <| fun _ ->
            Expect.equal
                (composeSshCommand { none with EnvCommand = Some "'C:/Program Files/PuTTY/plink.exe' -ssh" })
                (Some "'C:/Program Files/PuTTY/plink.exe' -ssh -batch")
                "single quotes"
            Expect.equal
                (composeSshCommand { none with EnvCommand = Some "\"C:\\Program Files\\PuTTY\\PLINK.EXE\" -ssh" })
                (Some "\"C:\\Program Files\\PuTTY\\PLINK.EXE\" -ssh -batch")
                "backslashes and upper case"
        testCase "a command that already names BatchMode still gets the options appended" <| fun _ ->
            Expect.equal
                (composeSshCommand { none with EnvCommand = Some "ssh -o BatchMode=no" })
                (Some("ssh -o BatchMode=no " + opts))
                "user's first value wins in ssh"
    ]

[<Tests>]
let keyTests =
    testList "SyncGit.cloneKey and dbKey" [
        testCase "cloneKey against a fixed vector" <| fun _ ->
            // printf '%s' "git@github.com:alice/total-recall-sync.git" | sha256sum | cut -c1-12
            Expect.equal (cloneKey "git@github.com:alice/total-recall-sync.git") "d938d07c0fca" "vector"
            Expect.notEqual (cloneKey "git@github.com:alice/total-recall-sync") (cloneKey "git@github.com:alice/total-recall-sync.git") "differs"
            Expect.isTrue (System.Text.RegularExpressions.Regex.IsMatch(cloneKey "x", "^[0-9a-f]{12}$")) "shape"
        testCase "dbKey against a fixed vector and case rules" <| fun _ ->
            // printf '%s' "total-recall.db" | sha256sum | cut -c1-12
            Expect.equal (dbKey false "total-recall.db") "da6cac52b1c4" "vector"
            Expect.notEqual (dbKey false "A.db") (dbKey false "a.db") "case-sensitive"
            Expect.equal (dbKey true "A.db") (dbKey true "a.DB") "case-insensitive"
            Expect.notEqual (dbKey false "a.db") (dbKey false "b.db") "different names"
    ]

[<Tests>]
let lsRemoteTests =
    testList "SyncGit.parseLsRemote" [
        let a = String.replicate 40 "a"
        let b = String.replicate 40 "b"
        testCase "an exact ref only" <| fun _ ->
            Expect.equal (parseLsRemote "main" (a + "\trefs/heads/main\n")) (Some a) "one"
            Expect.isNone (parseLsRemote "main" "") "empty"
            let tricky = a + "\trefs/heads/foo/refs/heads/main\n" + b + "\trefs/heads/main\n"
            Expect.equal (parseLsRemote "main" tricky) (Some b) "suffix match ignored"
            let swapped = b + "\trefs/heads/main\n" + a + "\trefs/heads/foo/refs/heads/main\n"
            Expect.equal (parseLsRemote "main" swapped) (Some b) "swapped"
            Expect.isNone (parseLsRemote "main" ("abc\trefs/heads/main\n")) "short sha ignored"
            Expect.isNone (parseLsRemote "main" (a + "\trefs/heads/other\n")) "other branch"
            Expect.equal (parseLsRemote "main" (String.replicate 64 "c" + "\trefs/heads/main\r\n")) (Some(String.replicate 64 "c")) "sha-256 and CRLF"
    ]
