<!-- ai-sdlc-pipeline:start (managed by wesback/engineeringpipelinev3 scripts/onboard-repo.sh) -->
## Issues for the AI-SDLC pipeline

This repository is worked by an autonomous pipeline (wesback/engineeringpipelinev3): Copilot implements an issue,
three Codex reviewers check it, and AI-SDLC merges it when every gate passes. An issue is picked up when it has the
label `ai-eligible`, passes AI-SDLC's Definition-of-Ready gate, and every issue it depends on is closed. When you
(a person or an assistant) write an issue for it, follow these rules.

### What a story must contain

- **One change**, with a clear title and a one-paragraph goal.
- **The files it touches**, by path, inside the implementer's write scope (source and test folders).
- **`## Acceptance criteria`**: checkboxes, each one testable, each naming the behaviour and at least one negative case.
- **Size**: the pipeline refuses a change over 300 changed lines (added plus removed) or one with no test file. Aim for a story that changes
  well under 300 lines; split anything bigger, and give a deletion-only cleanup its own test.
- **A behaviour test**: name the test file. The test must fail on a broken version; the pipeline requires the new tests
  to fail on `main` and pass on the branch, and the whole test suite to pass. One exception: a story that changes
  **only test files** (tests, specs, e2e, fixtures; no production file) cannot fail on `main`, so the pipeline waives
  that check for it. Say "tests only" in the goal. A new test that pins behaviour `main` already has belongs in such a
  story, never in one that also changes production code.
- **`Depends on #N`**, one line per prerequisite issue. The story waits until those are closed.
- **`PRD: docs/prd/<file>.md`** when the story comes from a PRD; cite the PRD's requirement IDs (FR-, NFR-, AC-).
- **No wishlist words**: "nice to have", "maybe", "TBD", "etc.".
- **Never** ask for changes to `.github/`, `.ai-sdlc/`, `AGENTS.md`, CI or dependency files: the implementer cannot
  write them and the pipeline never merges them.

### Story template

```markdown
<Goal: what changes and why, in one paragraph.> Files: `<path>`, `<test path>`.

## Acceptance criteria

- [ ] <behaviour>, and <the negative case that must fail>.
- [ ] `<test file>` passes, and fails when <the change> is missing.
- [ ] Existing tests pass. A test that asserts the old behaviour this story removes is updated to the new behaviour, never deleted. No other files are changed.

Depends on #<n>
PRD: docs/prd/<file>.md
```

### Creating it

```sh
gh issue create -R <owner>/<repo> --title "<title>" --body-file story.md            # a draft: not picked up yet
gh issue edit <n> -R <owner>/<repo> --add-label ai-eligible                         # ready: the pipeline takes it
```

Add `area:frontend` for UI stories (the implementer then loads the UI and accessibility skills).

### Big features: PRD first

A feature too large for one story starts as a PRD. The merge of the PRD is the human gate; nothing is built before it.

1. **Write the PRD** in `docs/prd/<name>.md` on the template `docs/templates/prd-template.md` (HVE Core's
   requirements-author template). In an assistant session, HVE's `prd-builder` agent fills it in. Give every
   requirement and acceptance criterion a stable ID: `FR-1`, `NFR-1`, `AC-1`. Say what is out of scope and what the
   security notes are (data handled, trust boundaries).
2. **Merge it through a PR.** A named person approves it in the PR.
3. **Planning** (on for this repository: the pipeline runs it by itself after the merge): HVE's PRD quality review, the Security Planner (a threat model in
   `docs/security/`), and the Functional Planner (the stories). The stories become issues with `ai-eligible`, a
   `PRD:` line, `Depends on #N` lines, and the threat model's mitigations as acceptance criteria. Review the created
   issues; each still has to pass the readiness gate before it is built.
4. **Stories are built one at a time** in dependency order and merged by the pipeline.

Changing an approved PRD later: edit it in a PR (an addendum is fine). A changed PRD file is planned again when
planning is on, so say in the PR whether the change needs new stories.

### After you label it

The pipeline opens a PR from `ai-sdlc/issue-<n>`. Do not push to that branch. If the pipeline needs a person it labels
the PR or issue `ai-sdlc:needs-human` and comments why; fix the issue text and remove the label to let it retry.
<!-- ai-sdlc-pipeline:end -->
