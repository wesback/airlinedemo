# Implementation Brief - Reviewer Interface and Demonstration

Version: 0.3 draft | Date: 30 September 2026
Dependencies: [Demo PRD](demo-prd.md), [Workflow/API brief](workflow-evidence-api-brief.md).

Visual source: [Frontend theme specification](frontend-theme.md), based on the supplied Altivane Aviation Capital design and logo.

## 0. Technology decision

Implement the interface as a .NET 10-compatible web client served by the ASP.NET Core application on Azure Container Apps. It consumes the deterministic API and hosts no agent or model runtime. Findings from the server-side bounded investigation agent (PRD section 6.1) are displayed as bounded, cited output; all policy decisions and authorised actions remain server-side.

## 1. Outcome

A small, credible interface lets executives see the outcome of useful automation, and its limits, within two minutes: 90 seconds live plus a 30-second recorded agent trace. It is a case-readiness view, not a generic chatbot or enterprise portal replacement.

Apply the Altivane visual language: deep navy, aviation green, pale mint highlights, generous white space, thin linework and restrained cards. Keep the interface calm and evidence-led rather than decorative.

Demonstrate one automatic request and one internally reviewed ambiguity. The interface must never invent findings, calculate authority locally or hide a failed/pending backend operation.

## 2. Minimal views

| View | Required content | Exclusions |
| --- | --- | --- |
| Case overview | Altivane Aviation Capital aircraft/lease, planned return, package completeness, unresolved items, owner/action and current basis | No invented readiness percentage or predicted EUR saving. |
| Finding/evidence panel | Requirement, explanation, source document/version/page, uncertainty and policy route | No uncited fluent answer presented as evidence. |
| Internal review | Open tasks, source preview, permitted decisions and required reason | No approval without backend authorisation or current-basis confirmation. |
| Mock partner inbox | Delivered request, approved wording, request ID and optional response submission | No real communications; simulation status always visible. |
| Demo controls | Run identifier, approved fixture load/reset and clear execution-mode indicators | No arbitrary file paths, answer-key browser or universal role switch. |
| Investigation trace | Ordered agent tool calls, citation checks (including any downgrade) and the hand-off to policy, rendered from the `InvestigationTrace` API for one basis | No raw document text, full prompts or staged steps; read-only. |
| Finance and legal review | Draft reserve calculation with discrepancies, and draft notice for an obligation at risk; approve/return or sign off/reject with a reason | No invoice issuance or notice dispatch control; drafts are clearly labelled. |

One page with panels is preferable to several independently hosted applications. Choose the smallest accessible UI supported by the selected backend/hosting environment; framework choice remains open.

## 3. Data and action contract

Consume the parent PRD objects and API responses. The backend supplies authoritative case state, findings, policy routes and allowed actions. The client may format them but cannot promote a finding from `satisfied` to accepted.

Use `caseRevision`/ETag on review commands. On `412`, reload the current basis, explain what changed and require a new deliberate review. Do not silently resubmit the old approval.

Fetch evidence through the scoped API with an explicit version. No permanent broadly accessible document links or unrestricted storage URLs.

Show package processing failure distinctly from missing evidence. Keep simultaneous external and internal tasks visible even when the case has a single aggregate status.

The presenter may use preconfigured demo accounts with genuine server-side roles. If only one account is feasible, explicitly record that it holds both roles for demonstration; do not pretend the demo proves organisational separation of duties.

## 4. Two-minute walkthrough: outcome first

The executive "so what" must land within the first 20 seconds. Proposed script, rehearsed to fit:

| Time | Action | Executive point |
| --- | --- | --- |
| 0:00-0:20 | Open on the MOCK-AC-001 readiness view: nine months before return, one missing engine LLP record found, a routine request already sent, one ambiguous serial number held for internal review. | Surprises found early, while there is time to act. |
| 0:20-0:50 | One click into the missing-record finding: source citations and policy basis. | The request is evidence-backed and policy-bound. |
| 0:50-1:10 | The request as the airline received it in the mock partner inbox. | Routine follow-up happened without another manual handoff. |
| 1:10-1:30 | The ambiguous serial-number finding: internal review task, no external request. | Uncertainty is not exported as a compliance allegation. |
| 1:30-2:00 | Recorded agent trace from a real run of the frozen version: tools chosen, a citation checked and corrected, hand-off to policy. | Autonomy inside guardrails; people and policy decide. |

If processing is pre-completed, label it as preprocessed setup and demonstrate subsequent actions honestly. If running live ingestion, show queued/processing states and use the agreed fallback rather than imply instant completion.

Do not claim that a few minutes of demonstration proved earlier rental income, regulatory compliance or a real airline response.

## 5. Failure and fallback behaviour

Proposed live-wait threshold: 15 seconds without useful progress, then explain the switch to a recording. Confirm the threshold in rehearsal; it is a presenter decision, not a backend timeout requirement.

Capture backup video from the working application using the same fixture/app version. Include synthetic-data and mock-integration labels. Store a receipt with seed, fixture version, app version and capture date.

A recording proves previously observed behaviour; it must not be presented as current live execution. If no working application exists, use a labelled concept walkthrough only after explicitly revising the live-demo commitment.

Do not spend Q&A time troubleshooting. Have a static evidence/control view available if video playback fails; distinguish it from a working demonstration.

## 6. Usability and safety

- Clear labels: "Needs internal review", "Evidence requested", "Processing failed", and "Accepted for mock checklist"; never a generic "Airworthy" badge.
- Keyboard-operable controls, legible projection text and status conveyed without colour alone.
- Disable unavailable actions for usability, but always enforce authority on the server.
- Require deliberate confirmation for acceptance and run reset; preserve reason and current basis.
- Display errors with safe correlation identifiers, not raw traces or document secrets.
- Render document/model text as untrusted content; no executable HTML or instructions.

The main case view shows readiness evidence, not a fabricated portfolio dashboard. Reserves and contract-notice drafts appear only because they are implemented thinly (PRD section 4.1). Mention transition planning and credit/sanctions in the narrative or architecture, not as inactive buttons implying implementation.

## 7. Acceptance evidence

Own AC-06, AC-09, AC-11 and AC-12 jointly with the backend; visibly demonstrate AC-02 and AC-03.

Cover stale-review response, unauthorised access, processing failure, duplicate button clicks, later contradictory evidence and reset cancellation. Verify that every apparent success reflects backend state and that controls remain usable with keyboard navigation.

Own AC-13's visible trace view and the review steps of AC-14 and AC-15 jointly with the backend.

Complete five consecutive rehearsals under the proposed parent readiness bar. Record observed duration, failure/fallback use and app/fixture versions; do not report synthetic repetitions as production accuracy.

Deliver the minimal interface, focused tests, presenter script, preflight/reset instructions and a reproducible recording procedure. Produce the actual recording only once the application works.
