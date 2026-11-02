# Arriving-plane source journey

This local rehearsal demonstrates governed access to the five synthetic source fixtures before records intake. The source systems run in the AirlineDemo application; no external source or model service is required. Fixture hashes and metadata are checked at startup.

## Start with a fresh case store

From the repository root, create a new temporary directory for this rehearsal. Keep it separate from any existing case store; a new directory is required for each clean run.

```sh
set -euo pipefail
DEMO_STATE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/airlinedemo-arrival.XXXXXX")"
export Demo__ArrivalJourney=true
export Demo__Passcode='choose-a-local-demo-passcode'
export Persistence__CaseStateProvider=File
export CaseState__Path="$DEMO_STATE_DIR/cases.json"
ASPNETCORE_URLS=http://localhost:5080 dotnet run --project backend/AirlineDemo.Api
```

In another terminal, start the frontend from the same repository:

```sh
npm --prefix frontend run dev
```

Open the frontend's local address and sign in with the passcode configured above. The opt-in enrolls `case-001`; other cases keep their existing behavior. Do not point `CaseState__Path` at an existing store or erase a store to restart the demo.

## Walk the scenario

1. Sign in as **Demo presenter**, open the `Boeing 737-8 N-DEMO1` aircraft marked **Arrived**, then select **Start arrival**.
2. The arrival step lists exactly `technical`, `maintenance`, `finance`, `records` and `contracts`. Each panel shows the server summary's mode, `Simulated` capability and coverage. A source the current persona cannot access is labelled **Not permitted** and does not show record details.
3. Switch to **Technical reviewer** and retrieve and pin `technical`, `maintenance` and `records`.
4. Switch to **Finance reviewer** and retrieve and pin `finance`.
5. Switch to **Legal reviewer** and retrieve and pin `contracts`.
6. Switch back to **Demo presenter** and select **Complete arrival**. The server reports the case ready for records intake; the five source panels and the activity trail remain available.

Successful reads append their permitted record identity and version to the case activity trail and pin the source snapshot into the case basis. The server's ordered missing-source list prevents completion until all five are pinned. The presenter sees source activity but not record identities; reviewer views show identities only for sources their role may access.

## Rehearse one unavailable-source recovery

The existing mode command is presenter-only. While signed in as **Demo presenter**, read the current `caseRevision` from `GET /api/v1/cases/case-001/arrival`, then set one source to `unavailable` with that revision:

```sh
curl --fail-with-body \
  --cookie "$PRESENTER_COOKIE" \
  --header "X-CSRF-Token: $PRESENTER_CSRF" \
  --header "Content-Type: application/json" \
  --data '{"commandId":"maintenance-off-1","expectedRevision":<CASE_REVISION>,"mode":"unavailable"}' \
  http://localhost:5080/api/v1/cases/case-001/sources/maintenance/mode
```

Replace `<CASE_REVISION>` with the integer from the status response. `$PRESENTER_COOKIE` must contain the presenter's session cookie as `__Host-demo-session=<value>`; `$PRESENTER_CSRF` is that session's CSRF token. Use the same authenticated session for the recovery command.

The maintenance panel now reports `unavailable`; its coverage is false. Switching to **Technical reviewer** and requesting it returns the unavailable outcome, records that attempt without record references, and leaves `maintenance` in the missing-source list. Switch back to **Demo presenter**, fetch the new current revision, then send another mode command with a new command ID and `mode` set to `available`. Switch to **Technical reviewer** and retrieve and pin `maintenance`. Do not reuse the previous expected revision after either mode change or read attempt.

## Stop at the handoff

Stop once arrival is complete and the case is at **Records package intake**. Do not click **Send demo records package**: that begins the separate intake and AI investigation workflow, which is outside this fixture-only rehearsal.
