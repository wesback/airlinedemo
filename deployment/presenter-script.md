# Presenter script: aircraft return-readiness walkthrough

Status: checked-in walkthrough script. This script describes a synthetic-data
rehearsal and a mock integration; it is not evidence that a live deployment or
recording has been executed.

Use the same approved fixture run, application version, and authenticated
presenter account throughout. Keep the visible `SYNTHETIC DATA` and
`MOCK INTEGRATION` labels on screen whenever the relevant panel is shown.
Speak the execution mode before each stage: **LIVE**, **RECORDING**, or
**STATIC EVIDENCE/CONTROL VIEW**.

## Stage 1 - Case setup

**Presenter action:** Open the case overview, identify the fictional lessor,
aircraft, planned return date, selected run, package state, and current
evidence basis. Point out the case revision and the source document/version
identifiers supplied by the application.

**Executive point:** This is preparation before handover. The application
shows authoritative case and evidence state; the presenter does not calculate
a readiness score or acceptance decision.

**Labels:** `SYNTHETIC DATA` - the case and documents are generated fixtures.
`MOCK INTEGRATION` - all external actions in this walkthrough are simulated.

## Stage 2 - Missing-history mock request

**Presenter action:** Open the missing-history finding, show its cited
document/version and policy route, then show the resulting request in the mock
partner inbox. State that the request key is stable and that the inbox is not
a real partner channel.

**Executive point:** Routine follow-up can be dispatched through the bounded
mock integration without turning an unresolved finding into acceptance. A
request is evidence collection, not a received response or a completed
requirement.

**Labels:** `SYNTHETIC DATA` - the missing history and finding are generated
fixtures. `MOCK INTEGRATION` - the request and inbox are simulated and no
external message is sent.

## Stage 3 - Ambiguous-identity internal review

**Presenter action:** Open the ambiguous serial-number finding and its source
preview. Show the uncertainty, evidence citation, open internal review task,
permitted decisions, current basis, and required reason. Submit only the
permitted review decision through the authenticated review control, or show
the control as pending when this is a recording.

**Executive point:** Uncertainty stays inside an internal review route. A
model finding, a completed task, or a received response does not itself accept
the case.

**Labels:** `SYNTHETIC DATA` - the ambiguity and source preview are generated
fixtures. `MOCK INTEGRATION` - any surrounding request/response state is
simulated and the internal decision is not regulatory approval.

## Stage 4 - Version-bound history

**Presenter action:** Open the history panel and select the cited document
version. Show that the evidence basis, finding citation, review, and any
later document version remain distinguishable. Explain that later evidence
causes a new assessment rather than rewriting the prior immutable record.

**Executive point:** The history is bound to exact evidence versions. A later
version can require reassessment and can invalidate a current outcome; the
screen does not turn an old decision into a current acceptance.

**Labels:** `SYNTHETIC DATA` - every displayed document/version is a
controlled fixture. `MOCK INTEGRATION` - request and response history is
simulated, not a real partner response.

## Closing statement

Say: “This walkthrough uses synthetic data and mock integrations. The
application state shown is limited to this case and evidence basis. A
recording, if used, shows previously observed behaviour and is not current
live execution.”

Do not describe a satisfied finding, completed task, received response, or
recorded walkthrough as automatic case acceptance.
