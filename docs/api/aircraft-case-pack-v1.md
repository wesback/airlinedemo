# Aircraft case-pack API v1

The case-pack read contract uses `/api/v1/` paths. Successful and error envelopes use integer `contractVersion: 1`.
A response is not versioned by a case-state revision; fixture changes require a new `packVersion`, and a breaking response
schema change requires a new path and a new `contractVersion` (for example `/api/v2/` and `contractVersion: 2`).

## Case list

`GET /api/v1/cases` requires an authenticated, in-scope session. It returns:

- `contractVersion: integer` — always `1`.
- `packVersion: string` — current pack version.
- `cases: CaseSummary[]` — role-visible, authorized summaries.

Every `CaseSummary` contains `caseId`, `name`, `aircraftCategory`, `caseRevision`, `currentRunId`, `status`, `owner`,
`nextAction`, `activityId`, `capabilityLabel`, and `synthetic`. `status` is `CaseStates` with exactly `assessment`, `review`,
`request`, `finance`, `legal`, and `acceptance`. `nextAction` is null or `ActionSummary`, with `actionId` and `text`.
`aircraftCategory` is `narrow-body`, `regional`, or `wide-body`; `capabilityLabel` is exactly one of `AI agent`,
`Rules-based`, `Simulated`, `Design-only`, or `Unknown`; `synthetic` is `true`.

## Scene

`GET /api/v1/cases/{caseId}/scene` returns a scene with the following exact fields:

- `contractVersion: integer` — always `1`.
- `packVersion: string`.
- `scenarioDate: string` — fixed UTC date `2026-10-01`.
- `case: CaseSummary` — the same projection fields as the case list.
- `allowedActions: string[]` — currently either no entries or exactly `resetBlockedVariant`.
- `canResetBlockedVariant: boolean`.
- `objects: SceneObject[]` — exactly one aircraft, one engine, and two components.
- `history: JourneyEvent[]` — at most 20 latest authorized events in the current run, ascending by sequence.
- `olderHistoryAvailable: boolean` — true when older runs or earlier current-run events are outside the scene window.
- `documentRefs: EvidenceReference[]` — current object mappings and all references required by returned witnesses.
- `evidence: EvidenceWitness[]` — exact witnesses for returned events.

`CaseSummary.status` is the ARR role-visible `CaseStates` object. `SceneObject` contains `objectId`, `kind`, `caseId`,
`displayName`, `status`, `owner`, `nextAction`, `activityId`, `capabilityLabel`, and `evidenceIds`. Kind is exactly `aircraft`,
`engine`, or `component`; each `evidenceIds` entry is an `EvidenceReference.evidenceRefId`, never a document or witness ID.

`EvidenceReference` contains `evidenceRefId`, `documentId`, `documentVersion`, `basisId`, `basisVersion`, `displayLabel`,
`processingState`, `retryable`, and `usableAsEvidence`. A reference identifies one exact case/document-version/basis tuple.
A failed, partial, or unprocessed document has `usableAsEvidence: false` and no witness. Current object mappings use the
current-basis document-version tuple; historical witnesses retain their original document and basis versions.

`JourneyEvent` contains `eventId`, `runId`, `sequence`, `eventType`, `basisId`, `basisVersion`, `actorRole`, `summary`,
`capabilityLabel`, and `witnessIds`. Sequence is positive and ordered ascending in the response.

`EvidenceWitness` contains `witnessId`, `evidenceRefId`, `documentId`, `documentVersion`, `page`, `basisId`,
`basisVersion`, and `displayLabel`. It binds only to a usable reference with the same case, document ID, document version,
basis ID, and basis version and an existing positive page.

## ARR history selectors

The scene does not create a history store or history endpoint. Complete retained history remains available from the existing
ARR route `GET /api/cases/{caseId}/history?runId={runId}&basisId={basisId}`. It returns exact `caseId`, `runId`, `basisId`,
and ordered events with historical witness tuples. `runId` and `basisId` are exact selectors; omitting them selects ARR's
latest authorized run and basis-bearing event. The scene uses only the scoped current run's latest 20 events and never deletes
older ARR runs.

## Reset command

`POST /api/v1/cases/{caseId}/reset-blocked-variant` is accepted only for the in-scope Case C presenter. The request body is:
`contractVersion`, `commandId`, `expectedRevision`, and `variant`. `contractVersion` is integer `1`; `commandId` is 1-64 ASCII
letters, digits, hyphens, or underscores; `expectedRevision` is a nonnegative integer; `variant` is exactly `blocked`.
The response receipt contains `contractVersion`, `commandId`, `caseId`, `priorRevision`, `newRevision`, `newRunId`, and
`auditEventId`, all exact server values. Replay, stale revision, conflict, validation, role, scope, and anti-forgery outcomes
use their specified HTTP status and error code.

## Error envelope and migration

Every top-level error is `{ "contractVersion": 1, "error": { "code": string, "message": string } }`. There is no case
or receipt data in an authorization or command-conflict error. Unsupported request versions use
`unsupported_contract_version`; scene byte-limit overflow uses `scene_payload_limit`.

A breaking contract change requires a new request path and a new integer `contractVersion`; clients must not interpret an
unsupported version. A case-state revision is not a contract version, and fixture content changes require a new `packVersion`.

## Payload limit

Scene serialization is measured as UTF-8 bytes before compression. At most 65,536 bytes are returned. A larger serialized
scene returns HTTP 422 with this exact error envelope and no partial scene:

`{"contractVersion":1,"error":{"code":"scene_payload_limit","message":"scene_payload_limit"}}`

Unauthenticated reads return 401, unsupported versions return 400 `unsupported_contract_version`, and a missing or out-of-scope
case returns the same empty 404 response. All data remains case-scoped and role-filtered.
