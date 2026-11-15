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

Side observation: #4 was **not** linked to a parent (the brief links only #2 and #5 to #9), so this confirms `state_reason` independent of any link. The issue timeline's `closed` event shows `state_reason` null; read the issue itself, not the timeline.

## (e) The per-parent sub-issue limit

Read from GitHub's documentation (`Adding sub-issues`, docs.github.com, read 2026-11-15, not probed): "You can add up to 100 sub-issues per parent issue and create up to eight levels of nested sub-issues." The design's cap of 50 parts is within the limit, and a single `per_page=100` page of the list holds a whole parent.
