# Altivane Aircraft Return Readiness Demonstrator - Product Requirements

Version: 0.1 draft | Date: 10 September 2026 | Owner: Wesley Backelant

## 1. Purpose

Support a CEO/CFO/CTO presentation with a working demonstration of bounded automation: identify a specific records gap, initiate an authorised administrative request, and retain ambiguous evidence for internal review.

The primary deliverable is a 35-minute executive argument plus 10 minutes of Q&A. The live demonstration targets approximately three minutes within that argument, with a recording of the working application as backup.

Altivane Aviation Capital is an entirely fictional aircraft lessor. This is not a production aircraft-leasing platform. Synthetic results do not substantiate airworthiness, regulatory compliance, production model accuracy, the case's 40%/70% targets or EUR60m benefit.

## 2. Decision status and constraints

| Status | Requirement or position |
| --- | --- |
| Agreed | Augment existing records, maintenance and finance platforms; do not replace them. |
| Agreed | Executive ask: approve a phased programme, releasing funding at evidence-based gates. |
| Agreed | Bounded automatic evidence requests; ambiguous identities go to internal review before external contact. |
| Agreed | Human authority over airworthiness-relevant conclusions and evidence acceptance. |
| Agreed | Synthetic data, live demo and recorded backup. |
| Confirmed constraint | Two participant preparation hours weekly; approximately 26 total across the programme. |
| Confirmed constraint | Azure target Sweden Central, `swedencentral`; ceiling USD 500/month, not a spending target. |
| Proposed | Durable Functions owns the case lifecycle; Agent Framework performs bounded, read-only investigation inside activities. |
| Proposed | Blob Storage, SQL Database, Document Intelligence, Azure OpenAI, Entra ID and Application Insights form the initial shortlist. |
| Unresolved | Repository, implementation language, exact hosting/backend/model/SKUs, network policy, subscription, quotas and regional prices. |
| Not authorised | Azure provisioning, expenditure, real partner communications or access to customer systems. |

Contracts and defaults below are proposed implementation requirements for review, not previously agreed customer facts. Implementers must surface material changes rather than silently substituting technologies, region or behaviour.

## 3. Users and journeys

| Actor | Required journey | Authority |
| --- | --- | --- |
| Presenter | Load a known fixture, explain findings, demonstrate the two routes and reset a demo run. | Demo operator role; not automatically a technical approver. |
| Technical reviewer | Open source evidence, review ambiguity, accept supplied evidence or reject/dismiss an incorrect finding with a reason. | Only for authorised aircraft/lease scope and the current finding basis. |
| Simulated airline contact | Receive a bounded request in a mock inbox and submit a linked response package. | Mock partner identity, restricted to the relevant case. |
| Executive observer | Understand readiness, ownership and action boundaries. | No application access required unless explicitly provisioned. |

Roles are server-authorised. A UI role selector must not grant permissions. Local test identities are allowed only in an explicit local test mode that cannot be enabled in the deployed application.

## 4. Scope

### Must deliver

- One fictional aircraft, one engine, two components and approximately 8-12 evidence documents.
- Versioned mock checklist, package inventory and processing status.
- A complete baseline plus missing-history and ambiguous-identity paths.
- Evidence-backed findings and an explicit policy decision before any request.
- A mock partner inbox; no email, Teams or real airline connector.
- Human review, response ingestion and version-bound reassessment.
- Persistent business state, duplicate suppression and recovery behaviour.
- A small reviewer interface, resettable fixtures and recorded backup.
- Terraform and separate application deployment/seeding instructions.

### Design, but do not implement as additional agents

| Case capability | Programme position |
| --- | --- |
| Records - L4 | Implement routine processing and bounded requests; preserve technical acceptance authority. |
| Utilisation/reserves - L3 | Document deterministic calculations and finance approval before invoice issuance. |
| Transition planning - L2 | Show how unresolved records link to readiness; no autonomous bookings or invented schedule optimisation. |
| Credit/sanctions - L2 | Document separate authoritative feeds, freshness and specialist resolution; no claim the records corpus establishes risk. |
| Contract compliance - L3 | Document legal sign-off before formal notices; administrative evidence requests are not notices. |

The portfolio supervisor is logical coordination and prioritisation, not an extra autonomous runtime in this demo.

### Non-goals

No real certificates, signatures or regulator logos; no genuine personal/customer data; no production partner agents; no aircraft release, invoice issuance, formal notice or sanctions determination; no fleet-scale migration, custom model training, Fabric capacity or Kubernetes platform.

## 5. Shared contracts - authoritative for all four briefs

Contract version: `1.0`. Implementation must publish machine-readable schemas derived from this section before components integrate. Schema changes update this PRD and dependent fixtures together.

Common conventions:

- IDs are opaque strings, stable within a seeded fixture and run. They must not encode the expected scenario outcome.
- `runId` isolates demonstration execution; it does not grant access.
- `airlineId` is business scope, not an assertion of a separate Entra tenant.
- Actual timestamps use UTC ISO 8601; scenario dates/times are separate fields.
- Enumerations are closed; unsupported versions, unknown states and invalid payloads fail explicitly.
- Server validates scope against authenticated identity and resource ownership, never solely caller-supplied IDs.
- `caseRevision` is a server-controlled increasing integer; mutations require the expected revision.

| Object | Required fields and rules |
| --- | --- |
| CaseContext | `runId`, `caseId`, `airlineId`, `aircraftId`, `leaseId`. Scope accompanies stored objects and is validated server-side. |
| Asset | `aircraftId`, `engineId`, component entries with `componentId`, `serialNumber`; reference-source identity/version. Relationships must be consistent. |
| Requirement | `requirementId`, `version`, `componentId`, `evidenceKind`, `description`, `applicability`, `approvedBy`, `approvedAt`, `sourceRef`. Optional required date interval. Seeded mock approval is labelled setup, not AI approval. |
| Document | `documentId`, `version`, `sourceSystem`, `sourceRecordId`, `fileName`, `mediaType`, `sha256`, `issuedOn`. Server-managed locator identifies the exact stored content/version. |
| EvidenceRef | `documentId`, `version`, one-based `page`, optional `bounds` and quote. Server verifies the cited version/page exists and belongs to the case. |
| SubmissionPackage | `schemaVersion`, `packageId`, scope IDs, `submittedAt`, `scenarioEffectiveAt`, `manifest[]` of Documents. Manifest describes supplied files, not expected answers. |
| EvidenceBasis | `basisId`, scope, document/version/hash inventory, requirement IDs/versions, `createdAt`, extraction/model/prompt/policy versions, `caseRevision`. Immutable; large raw content stays outside workflow history. |
| Finding | `findingId`, `componentId`, `requirementId`, `basisId`, `assessment`, `reasonCode`, `explanation`, `evidenceRefs[]`. Assessment is `satisfied`, `missing`, `ambiguous`, `conflicting` or `blocked`. It is not technical acceptance. |
| PolicyDecision | `decisionId`, `findingId`, `basisId`, `policyVersion`, `outcome`, `reasonCodes[]`, `evaluatedAt`. Outcome is `auto_request`, `internal_review` or `no_action`; calculated by application policy, never accepted directly from model output. |
| EvidenceRequest | `requestId`, stable `requestKey`, `findingId`, `basisId`, `requirementId`, `recipientRef`, `templateVersion`, rendered `message`, `status`, `createdAt`. Status is `pending`, `delivered`, `delivery_unknown`, `responded`, `closed` or `cancelled`; terminal states require `closureReason`. |
| ReviewDecision | `reviewId`, `findingId`, `basisId`, `decision`, `reviewerSubject`, `reason`, `decidedAt`. Decision is `accept_evidence`, `dismiss_finding` or `needs_evidence`. Actor/time come from the server. Preserve history; new basis may make the prior decision non-current. |
| ReviewTask | `taskId`, `findingId`, `basisId`, `reasonCode`, `status`, optional assigned reviewer. Status is `open` or `completed`; completion does not automatically mean accepted evidence. |
| AuditEntry | `auditId`, scope, `actorType`, `actorId`, `action`, affected IDs, `basisId` when relevant, `recordedAt`, `correlationId`. Business audit is separate from diagnostic traces. |

`requestKey` represents a requirement/component/coverage gap within the case, not merely one model run or evidence basis. Reprocessing must reuse an equivalent active request; a new basis alone must not generate another request. Active includes `responded` while evidence is still being assessed. A response does not close the request. Closing requires a recorded authorised disposition; cancellation does not satisfy a requirement. A cancelled or closed request may require an explicitly recorded new action cycle if a genuinely new gap arises.

### Event envelope

Every externally submitted package/response uses:

```json
{
  "schemaVersion": "1.0",
  "eventId": "EVT-0101",
  "type": "package.submitted",
  "runId": "RUN-0001",
  "caseId": "CASE-0001",
  "airlineId": "AIRLINE-0001",
  "aircraftId": "MOCK-AC-001",
  "leaseId": "LEASE-0001",
  "occurredAt": "2026-09-10T13:00:00Z",
  "scenarioEffectiveAt": "2026-09-10T09:00:00Z",
  "correlationId": "CORR-0001",
  "payload": {
    "packageId": "PKG-0001"
  }
}
```

`package.submitted` payload requires `packageId`; `partner.response.received` requires `packageId` and `requestId`. Server records actual receipt time and authenticates the source; supplied `occurredAt` is not an authority claim. Review commands use the authenticated review API, not importable approval events.

Repeated `(runId, eventId)` with identical payload is idempotent. Reuse with a different canonical payload is a conflict. Receivers handle duplicate/out-of-order messages through persisted business state, not a claim of exactly-once transport.

### State separation

Package processing: `queued -> processing -> complete | failed`. An explicit retry can re-enter processing; preserve the failed attempt. Reaching complete requires accounting for every declared file and rejecting silent omissions.

Finding assessment and review disposition are separate. A `satisfied` model/code assessment is not an approval. A completed review task may dismiss a false finding rather than accept a part.

Case coordination: `active`, `awaiting_external`, `awaiting_review`, `ready_for_acceptance`, `accepted`, `blocked`. The case summary is a server-derived view; concurrent external and internal tasks must remain visible. Precedence: blocked processing, then review, then external wait, then readiness. No UI should hide other outstanding tasks behind one aggregate label.

Here `accepted` means accepted for the mock checklist scope by an authorised reviewer, never regulatory airworthiness certification or aircraft release. New relevant evidence invalidates the current acceptance basis and returns the case to reassessment.

## 6. Autonomy and policy requirements

An automatic evidence request is permitted only when all are true:

1. A current approved requirement explicitly identifies the expected evidence.
2. Aircraft/component identity and the relevant record association are unambiguous.
3. The applicable package is completely processed and coverage checked; no relevant unreadable or uncertain evidence is being treated as absent.
4. The requested evidence was not located in that defined evidence set. An empty semantic-search result is insufficient.
5. Recipient and message template are approved for the case.
6. There is no equivalent active request; scope and evidence basis remain current.

Use wording such as: "We could not locate [required evidence] in the submitted package. Please provide it or identify its location." Never claim that the part is unsafe or the airline is noncompliant.

Ambiguity, unsupported interpretation or conflicting records goes to internal review. Ingestion/inference failure produces an explicit blocked outcome, not a success-shaped assessment.

The model has case-scoped read tools only. It cannot approve, select arbitrary recipients, send requests, change policy or write authoritative business state.

## 7. Acceptance criteria

| ID | Required observable outcome |
| --- | --- |
| AC-01 | Complete baseline produces source-linked assessments, no missing-evidence requests and no automatic technical acceptance. |
| AC-02 | Clearly missing required history produces one delivered request in the mock inbox, with approved wording and recorded policy basis. |
| AC-03 | Ambiguous identity creates an internal review task and no external request for that finding. |
| AC-04 | A corrupt/unprocessed document prevents a completeness-based request or acceptance; the processing failure is visible and retryable. |
| AC-05 | Duplicate package/events and dispatcher retry produce no duplicate business request or mock-inbox delivery. Different payload under the same event ID is rejected. |
| AC-06 | An authorised human decision binds to the current evidence basis. Stale or unauthorised decisions cannot close a finding/case. |
| AC-07 | Later contradictory evidence preserves prior history and reopens affected assessment/acceptance. Pending obsolete actions are suppressed. |
| AC-08 | Documents containing workflow instructions do not alter policy, permissions or available tools. |
| AC-09 | Wrong-airline read, evidence retrieval, response and approval attempts reveal no protected data and perform no mutation. |
| AC-10 | After restart the application preserves findings, requests and approvals and resumes safely without hidden duplicate actions. |
| AC-11 | Demo reset is limited to its run and cannot delete unrelated data or ingest evaluator answers. The backup recording uses a reproducible fixture/application version. |
| AC-12 | The walkthrough fits approximately three minutes in rehearsal or transparently switches to its recorded fallback; pending/error states are not disguised as live success. |

Proposed rehearsal bar: five consecutive runs of the two headline paths with no forbidden action. This is a demonstration-readiness criterion, not a statistical production assurance claim. Set model limits, timeout behaviour and fallback timing before rehearsing; record actual outcomes and costs.

AC-07 does not imply an already delivered message can be recalled. If new evidence arrives while delivery is in flight, preserve the decision/delivery ordering and route the now-obsolete request for reconciliation rather than hiding the action.

## 8. Dependencies, change control and delivery

The PRD owns common contracts and AC identifiers. Four implementation briefs inherit them:

- [Synthetic data generator](synthetic-data-generator-brief.md)
- [Workflow and evidence API](workflow-evidence-api-brief.md)
- [Reviewer interface and demonstration](reviewer-interface-demo-brief.md)
- [Terraform and deployment](terraform-deployment-brief.md)

Order: contract review/schema creation first; generator and infrastructure feasibility can then progress independently; workflow consumes generator inputs; interface integrates with the API. Use contract-derived test doubles before integration but never present them as live model results.

Before coding, choose a repository and supported implementation language. Before Azure deployment, resolve subscription, permissions, regional availability, model-processing geography, network requirements and a costed bill of materials; obtain explicit deployment approval.

Completion means a traceable demonstration and reproducible deployment, with known limitations. If the technical spike overruns the allocated participant time, narrow the UI/integration scope before borrowing rehearsal time. Keep architecture and programme coverage even where no working agent is built.

## 9. References and provenance

Business scope derives from Case Study 43 and the participant's decisions. Altivane Aviation Capital is the fictional customer identity used in all derivative material. The ten-aircraft/twelve-week customer phase in the solution outline is a proposal, not this one-aircraft demo scope.

Technical candidates and their limits:

- [Durable Functions](https://learn.microsoft.com/en-us/azure/durable-task/durable-functions/durable-functions-overview)
- [Orchestrator constraints](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-code-constraints)
- [Agent Framework workflows](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/)
- [Document Intelligence Read](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/prebuilt/read?view=doc-intel-4.0.0)
- [Blob versioning](https://learn.microsoft.com/en-us/azure/storage/blobs/versioning-overview)
- [Foundry deployment types](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/deployment-types)
