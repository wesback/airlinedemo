# Implementation Brief - Workflow and Evidence API

Version: 0.1 draft | Date: 10 September 2026
Dependency: [Demo PRD](demo-prd.md). The PRD owns object contracts, authority boundaries and acceptance identifiers.

## 1. Outcome and implementation boundary

Process authorised evidence into traceable findings and controlled actions. Persist business state so review, response and recovery work beyond one model session.

Proposed implementation: Azure Functions/Durable Functions for lifecycle; bounded Agent Framework investigation inside an activity; Blob Storage for evidence and SQL Database for business records. Language, hosting plan, Durable backend and model are decisions to resolve before scaffold generation.

Use a normal model call if multi-step investigation adds no benefit. Do not create separate long-lived Agent Framework and Durable orchestration of the same case.

## 2. Processing pipeline

1. Authenticate and authorise the caller for the entire case context.
2. Validate the event, manifest, document types/size limits and exact file hashes. Reject arbitrary remote URLs and paths outside authorised input storage.
3. Record receipt idempotently and return a trackable operation.
4. Extract each submitted file; record parser/OCR versions, pages and explicit failure state. Package completion requires every declared file to be accounted for.
5. Build an immutable evidence basis referencing the scoped inventory and approved checklist version.
6. Investigate evidence using case-restricted retrieval; validate structured output and citations. Unresolvable or unsupported claims route internally.
7. Apply deterministic policy to each finding; commit business changes, audit entries and any dispatch intent transactionally.
8. Dispatch permitted requests to the mock inbox; persist acknowledgement or uncertainty.
9. Ingest responses through the same pipeline. Reassess findings against the new basis; acceptance requires the authorised review path.

Retrieval returning no hits is not a completeness assessment. Do not use an extraction-confidence threshold as the sole trigger for missing-evidence requests.

## 3. Proposed API surface

Paths describe HTTP routes, not filesystem paths. The shared contract must be exposed through OpenAPI or an equivalent machine-readable definition during implementation.

| Operation | Input | Response and constraints |
| --- | --- | --- |
| `POST /api/packages` | PRD package plus event envelope; files preloaded to the authorised run store | `202` with operation/case IDs; identical replay returns the prior receipt; changed payload under the same event ID is `409`. |
| `GET /api/operations/{id}` | Authenticated scoped caller | Processing status and safe error codes; no hidden success fallback. |
| `GET /api/cases/{id}` | Authorised caller | Case revision, scoped findings, tasks, requests and aggregate status. |
| `GET /api/cases/{id}/evidence/{documentId}` | Exact document version and permitted page | Authorised evidence preview; no unrestricted blob locator or cross-case access. |
| `POST /api/cases/{id}/reviews` | Finding/basis IDs, decision, reason; `If-Match` case revision | Reviewer subject/time derived server-side; stale revision/basis rejected with `412`. |
| `POST /api/partner-responses` | `partner.response.received` envelope with request/package IDs | Validate recipient scope, request lifecycle and idempotency; process as new evidence. |
| `GET /api/mock-inbox` | Authorised mock-partner scope | Only that scope's delivered requests; explicit simulation label. |
| `POST /api/demo/runs` | Approved fixture reference | Operator-only; creates fresh isolated run, never ingests evaluator data. |
| `DELETE /api/demo/runs/{runId}` | Explicit operator confirmation | Demo-only cleanup within the run; cancel work first; preserve evidence required for evaluation. |

Return `401` for missing/invalid authentication, `404` for inaccessible case/evidence to avoid existence leakage, `403` for a disallowed action within an otherwise visible scope, `400` for invalid payloads, `413` for configured size limits, and `429` for admission throttling. Errors carry a safe code and correlation ID, not stack traces or document contents.

Cancellation/cleanup endpoints are unavailable outside explicit demo mode. No outbound email, invoice, formal notice or regulatory approval endpoint is in scope.

## 4. Business state, concurrency and delivery

SQL owns business objects and authoritative status. Durable history owns execution progress and references those objects by ID. Do not carry complete document bodies or identity credentials in orchestration history.

Each basis and human decision is immutable. Use optimistic concurrency and the PRD state rules. When new evidence arrives, invalidate current acceptance as appropriate without deleting the old review, and suppress obsolete pending requests before dispatch.

For automatic requests, the transaction commits the permitted PolicyDecision, EvidenceRequest, audit entry and outbox intent together. Enforce uniqueness for equivalent active `requestKey` values, not merely model execution IDs.

A dispatcher claims intents with a bounded lease, rechecks current scope/basis/eligibility, sends to the mock adapter and stores acknowledgement. The mock adapter deduplicates by stable `requestId`. A crash after delivery but before acknowledgement must reconcile that ID before retrying.

Define the atomic authorisation/claim point and record delivery ordering. New evidence suppresses unclaimed stale actions; it cannot retroactively unsend an in-flight or delivered message. Such races produce a visible reconciliation item, not an erased audit entry.

Requests in `responded` remain active until assessed. Apply `closed` or `cancelled` only with the PRD's recorded disposition/closure reason. A response package, dismissal or transport acknowledgement must not silently grant technical acceptance.

If a real future connector cannot deduplicate or query delivery status, leave the request `delivery_unknown` for reconciliation; do not promise exactly-once delivery.

Identical activity retries reuse committed findings/actions for the same processing basis. Guard against a stale model result arriving after a newer submission. Out-of-order partner responses are retained or explicitly rejected according to the current request lifecycle, never silently attached to an unrelated request.

## 5. Agent and approval controls

Permitted investigation tools retrieve only approved requirements, manifest/processing facts and authorised evidence. The model cannot choose an arbitrary airline scope, invoke a send action or mutate policy.

Configure maximum calls, context/pages, tokens, elapsed time and retries. When limits or providers fail, record an explicit blocked/review outcome. No unbounded self-correction loops or invented evidence.

Require citations to valid case document versions and pages. A missing finding cites the applicable requirement and the supporting identity/inventory basis, not a fabricated absent page.

`accept_evidence` is allowed only when the reviewer can assess the referenced supplied evidence. `dismiss_finding` records a false finding with a reason; it does not automatically satisfy a still-unresolved requirement. `needs_evidence` records the review outcome and retains unresolved work; it does not itself authorise an external request.

Current acceptance requires all mock checklist requirements to be resolved through authorised decisions. It is not airworthiness certification. Formal finance/legal authority remains documented future scope.

## 6. Security and observability

Enforce organisation/aircraft/lease permission on reads, retrieval tools, responses and mutations. Do not derive access solely from `runId`, a caller's airline field, or a shared aircraft identifier.

Prefer managed identities and constrained repository/service access. The investigation component receives no general database, storage-account or communication credentials.

Correlate case, event, basis, operation and request IDs across traces. Record processing completeness, retries, latency, model usage, policy outcomes and review queue age. Do not log raw evidence, access tokens, full prompts or unsupported compliance claims.

The deployed identity configuration must not allow a local fixture-authentication bypass. Production-grade network and retention decisions are recorded gaps until designed and implemented.

## 7. Acceptance and integration

Own AC-01 through AC-10 with generator and UI support. Cover:

- Correct two-path routing, actual source citations and absence of false auto-acceptance.
- Provider/processing failure and exhausted retry budgets.
- Restart between transaction, delivery and acknowledgement.
- Duplicate, conflicting and out-of-order inputs.
- Concurrent/stale reviews and later evidence changes.
- Cross-airline request/read/approval attempts and document-borne instructions.

Use the repository's existing runner, adding focused tests in that ecosystem. An emulator/test double can validate control logic; record separate evidence for the configured Azure OCR/model integration.

Deliver source, migrations, API/schema definitions, tests and an operational failure walkthrough. Do not mark the API complete merely because a chat response looks correct.

## 8. Work packages

1. Agree language, contracts and persistence mappings.
2. Implement scoped ingestion and the deterministic baseline without AI.
3. Add real extraction and bounded investigation behind the same contracts.
4. Implement policy, requests/outbox and reviewer/response lifecycle.
5. Add recovery, isolation and stale-state tests, then integrate the minimal UI.

Do not add a general multi-agent chat framework, broad connector catalogue or production portfolio optimisation engine.
