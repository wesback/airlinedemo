# Backend Data-Flow Mockup

| Field | Value |
|---|---|
| Status | Approved |
| Version | 0.1.0 |
| Date | 2026-11-05 |
| Approval | User approved the specification and plan on 2026-11-05 |

## Problem

Signed-in users can perform source retrieval and upload records from case-specific workflows, but the application has no global explanation of how those two kinds of evidence reach a case. The Hangar Data Room is a case-level source-status view and does not explain PDF intake.

## Outcome

Add an authenticated global `Data flows` view at `/data-flows`. It presents two selectable, illustrative flows: simulated source-system retrieval and uploaded PDF evidence intake. It explains the implemented stages without making live status claims or changing existing case workflows.

## In Scope

* A global navigation entry and authenticated `/data-flows` route.
* Two independently selectable flows using ordered, readable stages.
* An explicit illustrative/non-live label.
* Keyboard-operable, assistive-technology-readable controls and responsive layout.
* Frontend tests for route access, all signed-in personas, flow content, flow switching, no case-specific content, and no content-specific network request.

## Out of Scope

* Backend, API, database, fixture, authentication, authorization, infrastructure, deployment, or case-workflow changes.
* Live vendor connectors, telemetry, runtime health, tenant-specific topology, case data, or secrets.
* Changes to the Hangar Data Room's per-case status and selection behavior.

## Requirements

* `REQ-UI-001`: The `presenter`, `technical-reviewer`, `finance-reviewer`, `legal-reviewer`, and `executive` personas can each reach `Data flows` from global navigation, and `/data-flows` is a valid direct route.
* `REQ-UI-002`: The page presents two separate selectable flows with ordered stages and clearly indicates which flow is selected.
* `REQ-SOURCE-001`: The simulated source-retrieval flow names `technical`, `maintenance`, `finance`, `records`, and `contracts` in the implemented source order; it explains case-scoped authorization, fixture retrieval, evidence pinning, and an audit receipt.
* `REQ-PDF-001`: The PDF-intake flow explains case upload, server validation and parsing, accepted-evidence pinning, registered synthetic text sent to the records agent, and the required human technical-review work item, regardless of the agent's outcome.
* `REQ-ILLUSTRATIVE-001`: The page labels both flows illustrative and never presents them as live processing, a live connector, or current system availability.

## Non-Functional Requirements

* `NFR-PRIV-001`: The walkthrough contains no case-specific data, case identifiers, secrets, tenant-specific topology, or live operational status.
* `NFR-NET-001`: Selecting or switching flows makes no content-specific network request. Existing shell authentication, health, and fleet behavior is unchanged.
* `NFR-A11Y-001`: Flow controls are semantic, keyboard operable, and expose the selected state to assistive technology. The ordered stages remain understandable without color or connector graphics.
* `NFR-VIEW-001`: The layout remains readable at narrow viewport widths, uses existing theme tokens, and maintains at least 4.5:1 text contrast in both light and dark themes.
* `NFR-COMPAT-001`: Existing `/`, `/cases/<caseId>`, and `/hangar` routes and case workflows remain unchanged.

## Security and Privacy

The existing sign-in gate continues to protect the route. The new page is static explanatory content and adds no data source, permission, endpoint, or user-controlled input. Do not copy case records, user identifiers, secrets, or environment-specific infrastructure values into the walkthrough.

## Telemetry

No new telemetry or runtime health reporting is added.

## Failure Behavior

The walkthrough's flow content is local and remains available without a flow-specific API response. Existing application-shell sign-in and service-health behavior is unchanged; this feature adds no loading, retry, or error state for flow selection.

## Backward Compatibility

The new route is additive. Existing route parsing, browser history, sign-in behavior, case actions, and the Hangar Data Room remain intact. The walkthrough does not replace or extend case-level source retrieval or PDF upload.

## Acceptance Criteria

1. `Data flows` appears for all five personas after sign-in. Test: `frontend/tests/TopBar.test.tsx` / `shows Data flows navigation for every persona`. Negative case: signed-out direct request to `/data-flows` -> the existing sign-in screen appears without walkthrough content. Test: `frontend/tests/JourneyApp.test.tsx` / `shows Data flows only after sign-in from its direct route`.
2. The signed-in global navigation opens the page, and unrecognized routes retain the existing board fallback. Test: `frontend/tests/JourneyApp.test.tsx` / `opens Data flows from global navigation and preserves the unknown-route fallback`. Negative case: unrecognized route -> the existing board fallback remains unchanged.
3. The default source flow is marked simulated, names all five source categories in order, and explains authorization, fixture retrieval, evidence pinning, and audit receipt. Test: `frontend/tests/DataFlowWalkthrough.test.tsx` / `shows the five simulated source categories and ordered retrieval stages`. Negative case: the page must not label a source as live or show runtime availability.
4. Selecting the PDF flow displays the separate upload, validation, parsing, evidence-pinning, registered-synthetic-text, and human-review stages; the human review remains required regardless of the agent's outcome. Test: `frontend/tests/DataFlowWalkthrough.test.tsx` / `switches to the PDF intake flow using keyboard controls`. Negative case: PDF flow selection -> source-flow details are not presented as part of the PDF path.
5. Switching between flows makes no content-specific request. Test: `frontend/tests/DataFlowWalkthrough.test.tsx` / `switching flows makes no content request`. Negative case: selecting either flow -> zero flow-content network requests.
6. `/data-flows` renders the Data flows heading without Hangar Data Room content. Test: `frontend/tests/JourneyApp.test.tsx` / `renders the Data flows page without Hangar Data Room content`. Negative case: `/data-flows` -> no Hangar Data Room content is rendered.
7. The rendered walkthrough shows its heading but contains no case identifiers or record references. Test: `frontend/tests/DataFlowWalkthrough.test.tsx` / `renders no case identifiers or record references`. Negative case: `cp-case-c` and `case-c/engine-serial-record.txt` -> neither value is present in the rendered walkthrough. Verified by review: the static page contains no secrets or tenant-specific topology.
8. Existing routes and the Hangar Data Room remain unchanged. Test: `frontend/tests/JourneyApp.test.tsx` / `switches to the 3D hangar mode from the top bar`. Negative case: `/hangar` -> Data flows content is not rendered.
9. Text contrast, keyboard focus visibility, and narrow-width readability meet the non-functional requirements in both themes. Verified by review. Negative case: a flow label relies on color alone -> the label remains understandable from text and selected-state semantics.

## Implementation Evidence

The source-retrieval path is implemented by `frontend/src/journey/arrivalApi.ts`, `frontend/src/journey/ArrivalSources.tsx`, and the backend source endpoints and fixture adapters. PDF intake is implemented by `frontend/src/journey/StepPanels.tsx`, `frontend/src/journey/api.ts`, `backend/AirlineDemo.Api/Evidence/EvidenceEndpoints.cs`, and `backend/AirlineDemo.Api/Evidence/IntakeJourneyHandover.cs`. The authenticated shell and global navigation are in `frontend/src/App.tsx` and `frontend/src/journey/TopBar.tsx`.

The approved Hangar Data Room PRD's decisions `DD-017` and `DD-018` describe its existing source-status role. Its frontmatter says approved, although its executive summary contains stale draft wording; current implementation evidence governs this feature. The draft Source Systems and Return Readiness PRDs do not supply requirements for this feature.
