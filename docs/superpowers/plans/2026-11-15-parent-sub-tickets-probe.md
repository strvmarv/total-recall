# Sub-issue call probe: findings

Probed on 2026-11-15 against the throwaway private repository `strvmarv/develop-probe` (default branch `main`), with the account `strvmarv`, through `skills/develop/scripts/ghpin.js run` (`gh api -i`, so the status line is in the output). Every text field went to GitHub from a file (`-F title=@file`, `-F body=@file`). The repository is not deleted (the token has no delete scope); Task 10 reuses it and archives it.

Objects created: repository (201), issues #1 (parent) and #2 to #4 (children), five more issues #5 to #9, branch `probe`, file `probe.txt`, pull request #10 (body `Closes #4`), merged.

## Differs from the spec

None. Every assumption the spec marks as unverified held: the link call takes the numeric `id` as an integer, a closed-by-`Closes` issue has `state_reason` `completed`, and five creates and links in a row hit no rate limit. Two behaviours the spec does not describe are recorded below as findings (the string form is refused with 422; a duplicate link is refused with 422), and neither contradicts it.

## (a) The link call and the string form

Working form (HTTP 201; the response is the **parent** issue's JSON, with `number` 1 and `sub_issues_summary` `{"total":1,"completed":0,"percent_completed":0}`):

```
gh api -X POST repos/<o>/<r>/issues/<parent>/sub_issues -F sub_issue_id=<numeric id of the child>
```

`<numeric id>` is the child's `id` from the issue JSON (for example `6408707710` for issue #2), not its `number`. `-F` types a numeric value as an integer.

The string form, `-f sub_issue_id=<id>`, is refused: HTTP 422, message `Invalid request. Invalid property /sub_issue_id: "6408707829" is not of type integer.` Nothing is linked. So the spec's `-F` is required, not a style choice, and a 422 on the link call is not always "transient": a wrong type is permanent.

Reading back: `GET repos/<o>/<r>/issues/<parent>/sub_issues?per_page=100` lists the linked children with `id`, `number`, `state` and `state_reason` (all `open` / `null` here). `GET repos/<o>/<r>/issues/<n>/parent` on an unlinked issue (#4) returns HTTP 404, message `No parent issue found`.

## (b) Linking the same issue twice

Linking child 1 (#2) again, same call and same `-F sub_issue_id`: HTTP 422, message `An error occurred while adding the sub-issue to the parent issue. Issue may not contain duplicate sub-issues and Sub issue may only have one parent`. The list is unchanged (still one entry for #2). So a second link is refused and harmless; ensure-stubs must treat a 422 with this message as "already linked" only after re-reading the list, since the same message also covers "already has another parent".

## (c) Five creates and links in a row

Five issues (#5 to #9) were created (each HTTP 201) and linked (each HTTP 201) immediately one after another with no pause. No `Retry-After` header and no rate-limit message appeared; `X-Ratelimit-Remaining` fell by two per create-and-link pair (4970, 4968, 4966, 4964, 4962 after the links), all against the ordinary core limit of 5000. The sub-issue list afterwards held #2, #5, #6, #7, #8, #9 (six entries, in link order). No secondary rate limit at this volume; a breakdown of up to 50 parts was not probed.

## (d) `state_reason` after a merged `Closes` PR

Sequence: `POST git/refs` (`ref=refs/heads/probe`, `sha` of `main`) 201; `PUT contents/probe.txt` on branch `probe` 201; `POST pulls` (`head=probe`, `base=main`, body `Closes #4`) 201, pull request #10; `PUT pulls/10/merge` (`merge_method=merge`) 200 `merged: true`. Then `GET issues/4` (about five seconds later): `state` `closed`, `state_reason` `completed`. So the definition of "done" (closed with `state_reason` `completed`) holds for a part closed by a merged `Closes #<n>` pull request into the default branch.

Side observation: #4 was **not** linked to a parent (only #2 and #5 to #9 were linked: #3 failed the string-form link, #4 was never linked), so this confirms `state_reason` independent of any link. The issue timeline's `closed` event shows `state_reason` null; read the issue itself, not the timeline.

## (e) The per-parent sub-issue limit

Read from GitHub's documentation (`Adding sub-issues`, docs.github.com, read 2026-11-15, not probed): "You can add up to 100 sub-issues per parent issue and create up to eight levels of nested sub-issues." The design's cap of 50 parts is within the limit, and a single `per_page=100` page of the list holds a whole parent.

## Walk-through

Run on 2026-11-15 in `strvmarv/develop-probe` as `strvmarv`, most calls through `ghpin.js run` with an `out` file, the argument forms of the Trust golden in `handoff.md` (no `-i`). The repository was archived afterwards (`PATCH repos/strvmarv/develop-probe -F archived=true`, HTTP 200, a read back shows `archived` true). Nothing was deleted: the parent #11, the stubs #12 to #14 and the impostor #15 stay in the archived repository. Not retained as files: the output of the local scripts (`route.js parts`, `route.js stub`, `counted.js write-access`), the two `parent-read` calls on #11 and #15 (so the 404 for #15 exists only as stdout); the `parent-read` call on #12 has an `out` file.

Step 1, the scripts on a three-part spec (parts `Depends on: none`, `Part 1`, `Part 1, Part 2`). `route.js parts` returned `ok` with three parts and `dependsOn` `[]`, `[1]`, `[1,2]`. `route.js stub-body` with a growing dependency map wrote bodies whose `Depends on:` lines read `nothing`, `#<part 1>` and `#<part 1>, #<part 2>`, and `route.js stub` parsed each (`{ok:true,part:n,count:3,parent:<n>}`).

Step 2, Ensure-stubs by hand on a fresh parent, #11 (`created_at` `2026-11-15T20:43:43Z`, which matches the `definedAt` pattern):

- Step 1 (`sub-issues`, page 1): `[]`, fewer than 100 entries, so one page.
- Step 2 (`issue-scan`, `since=2026-11-15T20:43:43Z`, page 1): one entry, #11 itself (its first line is no stub line), so no claimant. Issues #1 to #9 from the probe are older than `since` and were not listed.
- Step 3: `permission` for the author `strvmarv` answered `{"permission":"admin",...}` (200) and `counted.js write-access <file>` printed `{"writeAccess":true}`.
- Step 4, for parts 1, 2 and 3 in order: `stub-body` (stdout `{ok,title}`), `route.js stub` on the written body (ok), `stub-create` (HTTP 201, no assignee: `assignees` empty) giving #12 (id 6409271647), #13 (id 6409271956), #14 (id 6409272281), then `sub-issue-link` with `-F sub_issue_id=<id>` (201 each; the parent's `sub_issues_summary.total` went 1, 2, 3). Part 2's body read `Depends on: #12` and part 3's `Depends on: #12, #13`, the issue numbers created so far. The sub-issue list then held #12, #13, #14 in that order, each with the first line `Part <n> of 3 in #11`.

Step 3, the second run and the impostor:

- Second run: step 1 listed #12 to #14 (three claimants, one per part), the scan added no issue that was not already there, all three authors were eligible (`writeAccess` true), so no part lacked a stub and nothing was created.
- Impostor: #15, filed by `strvmarv` with the first line `Part 2 of 3 in #11` (`route.js stub` parsed it as part 2, parent 11). Third run: part 2 had two eligible claimants, #13 (linked) and #15 (unlinked, author `strvmarv`, `User`, write access); the lowest number, #13, stays the stub and #15 is reported as a duplicate; it was not linked, edited or closed (read back: open, no assignee, still no parent), and the sub-issue list stayed #12, #13, #14. Nothing was created.

### The `parent_issue_url` question

Observed case: the issues-LIST response (`GET repos/<o>/<r>/issues?state=open&since=...`) carries `parent_issue_url`, and the link shows in it. For linked #12 to #14 it is the string `https://api.github.com/repos/strvmarv/develop-probe/issues/11`; for an unlinked issue (#11, #15) the key is absent (not `null`). The entries also carry `sub_issues_summary` and `issue_dependencies_summary`. The `sub_issues` list entries carry it too. `deliver.md` Ensure-stubs step 3 now states the rule once from this: an entry with `parent_issue_url` is linked, to the parent it names; an entry whose `parent_issue_url` is absent is treated as unlinked, and `parent-read` is run on it before adoption as a cheap confirmation, because the list can lag (see below). The walk-through ran `parent-read` on #15 (HTTP 404 `No parent issue found`, consistent with the list) and on #12 (200, names #11). The earlier wording "(the field is unverified)" was removed from step 3.

### Deviation from `deliver.md` as written

One edit to `deliver.md` came out of the walk-through: the `parent_issue_url` rule in step 3 (above). `skill.test.js` is unchanged; its assertion on step 3 (`parent-read`, `parent_issue_url`, `absent`) still holds. Every other step worked as written.

One behaviour to know about, not a failure: the issues list can lag a write. The create call for #15 returned at 14:44:22 (`created_at` `20:44:22Z`) and the scan saved as `sc3.json` at 14:44:23.7, about 1 s after the create, did not list #15. The next saved scan, `sc4.json` at 14:44:45.0, about 22 s after the create, listed it. No scan between the two was saved, so the captures do not show when it first appeared, nor that it stayed listed; the repository is archived and read-only, so this was not probed further. A run that starts right after another one can therefore miss a stub the earlier one just created and create a second one. That is the "two runs, each creating a stub" case the section already reports as a duplicate and leaves to the person to close, so the prose is not changed for it.
