# Source Systems Contract

Status: synthetic demonstrator contract. Governed by [PRD: Synthetic Source-System Stubs and Read-Only Source Port](prd/aircraft-source-systems.md) (FR-001, DD-004, DD-006, DD-007).

## Port

Code: `AirlineDemo.Api.SourceSystems.ISourceSystem<TPayload>` (`backend/AirlineDemo.Api/SourceSystems/ISourceSystem.cs`).

| Method | Parameters | Returns |
|---|---|---|
| `ListAsync` | `string caseId`, optional `CancellationToken` | `SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>` |
| `GetAsync` | `string caseId`, `string recordId`, optional `CancellationToken` | `SourceReadResult<SourceRecord<TPayload>>` |

The port is read-only. It has no write, update or delete members, and it has no properties or events. Every read is scoped by `caseId`. Identity, role and case-scope checks happen on the server before a port is called.

## Record envelope: `SourceRecord<TPayload>`

`TPayload` is a class and is typed per `kind`.

| Property | Type | Rule |
|---|---|---|
| `sourceId` | string | Non-empty. One of `technical`, `maintenance`, `finance`, `records`, `contracts`. |
| `recordId` | string | Non-empty. |
| `recordVersion` | integer (JSON number) | Positive integer, 1 or greater. |
| `asOf` | date | UTC calendar date, serialized as `YYYY-MM-DD`. In C# this is a `DateOnly`. The default value is invalid. |
| `caseId` | string | Non-empty. Must equal the requested case. |
| `kind` | string | Non-empty. |
| `stale` | boolean | `true` when `asOf` is older than the scenario date (DD-007). |
| `payload` | object | Non-null, typed per `kind`. |
| `synthetic` | boolean | Must be `true`. `false` is rejected. |

`SourceRecord<TPayload>.Validate(requestedCaseId)` returns every violation. An empty list means the record is valid. A record whose `caseId` differs from `requestedCaseId` is rejected and must never be returned.

## Technical payloads

| Kind | Field | Type and accepted values |
|---|---|---|
| `utilization` | `periodStart` | String date, `YYYY-MM-DD`; first day of the reserve month. |
| `utilization` | `periodEnd` | String date, `YYYY-MM-DD`; last day of the reserve month. |
| `utilization` | `hours` | Non-negative decimal string; flight-hour utilization. |
| `utilization` | `cycles` | Non-negative decimal string; cycle utilization. |

An `applicability` payload contains exactly `requirementId` (required, non-empty string), optional `regime` (`EASA`, `FAA-91`, `FAA-121` or `Other`), and optional `responsibleParty` (`lessee-operator`, `lessor`, `camo` or `unassigned`). An unlisted value or any additional field is rejected. The fixture applicability record is synthetic and identifies requirement `SYN-AD-2026-014`.

## Finance payloads

`FinancePayload` is shared by exactly four finance kinds. Every value is a string; a payload must contain exactly the fields listed for its kind.

| Kind | Required payload fields |
|---|---|
| `rent` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`) |
| `maintenance_reserve` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`), `rateBasis` (`per_flight_hour` or `per_cycle`), `rate` (non-negative decimal string) |
| `security_deposit` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`) |
| `reserve_reimbursement` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`), `claimId` (string), `status` (`claimed`, `approved` or `paid`) |

`FinanceRecordValidator` rejects unknown kinds, missing fields and fields not listed for that kind. Reserve drafts consume only `maintenance_reserve` and `reserve_reimbursement`: billed is `recordedAmount`, collected is `collectedAmount`, underbilled is the non-negative difference, and overdue is underbilled when `dueDate` is before the draft period's last calendar day. Reimbursable is `recordedAmount` only for an approved or paid reimbursement. The draft total is the sum of underbilled amounts; it makes no profit, revenue or cash-recognition claim.

The source-fed reserve calculation reads `rateBasis` (`per_flight_hour` or `per_cycle`) and `rate` (a non-negative decimal string) from pinned `maintenance_reserve` records. It multiplies the matching pinned technical `hours` or `cycles` by that rate, rounds each calculation line to cents with midpoints away from zero, and then sums the rounded lines. `reserve_reimbursement` records remain separate finance lines with their own `claimId` (non-empty string) and `status` (`claimed`, `approved` or `paid`); billed, collected, underbilled, overdue and reimbursable remain separate values. Each calculated reserve line cites both the technical utilization record and the finance rate record by `(sourceId, recordId, recordVersion)`. A missing or non-available required source blocks the draft rather than treating its value as zero.

## Contracts payloads

The obligation evaluator consumes `obligation_clause` records from the latest committed source-basis pins. The `payload` contains these string fields:

| Field | Type and meaning |
|---|---|
| `clauseId` | String identifier of the contract clause. |
| `obligation` | String obligation name. |
| `noticeWindowStart` | String date in `YYYY-MM-DD` format; inclusive first day of the notice window. |
| `noticeWindowEnd` | String date in `YYYY-MM-DD` format; inclusive last day of the notice window. |
| `noticeWindowDays` | String number of days in the notice window. |

The evaluator uses the pinned record envelope's `asOf` date for the scenario date and cites the clause by its exact `(sourceId, recordId, recordVersion)` tuple. It does not read live contracts records or send notices.

### Specialist review flags

The `specialist_flag` kind contains exactly the following string fields:

| Field | Accepted values |
|---|---|
| `relatedRequirementId` | Non-empty requirement identifier, matched ordinally and case-sensitively. |
| `topic` | `sanctions`, `cape_town`, `ifrs16`, `gdpr` or `other`. |
| `result` | `specialist_review_required` or `not_assessed`. |
| `regime` | Required only for `sanctions`; exactly `EU`, `US` or `UK`. It is absent for all other topics. |

Unknown or missing fields, empty base values, unsupported topic/result values and regime fields on non-sanctions flags invalidate the contracts source during fixture initialization. An assessment is related to a flag only through an explicit, exact `relatedRequirementId`. A matching `specialist_review_required` or `not_assessed` result routes to internal review without creating a mock request or acceptance event. Sanctions results apply only to the named regime; a different regime remains `not_assessed`. These outcomes are not clearance, compliance, recoverability, legal, financial or accounting conclusions. In particular, an `ifrs16` flag does not classify a lease, assess impairment or recognize an accounting amount.

The `cape_town_interest` kind is a separate typed evidence payload with exactly these fields:

| Field | Type and accepted values |
|---|---|
| `internationalInterestRef` | Non-empty string reference. |
| `registrationRef` | Non-empty string reference. |
| `idera` | `present`, `absent` or `not_recorded`. |
| `stateDeclarationNoted` | JSON boolean. |

The payload has no `relatedRequirementId` field. A finding that relies on this evidence must have a `relatedRequirementId` matching exactly and case-sensitively a `specialist_flag` whose topic is `cape_town`; otherwise assessment refuses the finding. A matching finding is routed to internal review and includes a source-record witness and the typed evidence. The evidence does not determine priority, remedy, outcome, recoverability or any legal result.

## Signed fixture cases

Each of the five sources (`technical`, `maintenance`, `finance`, `records`, `contracts`) ships one fixture file under `backend/AirlineDemo.Api/Fixtures/sources/`, with a manifest (`<source>.manifest.json`) and a detached signature (`<source>.manifest.sig`). The manifest lists `owningCaseIds` (`case-001`, `case-a`, `case-b`, `case-c`), the SHA-256 digest of the fixture file and the record count of every source. Every record's `caseId` must be one of the owning IDs, and each source holds at least one record for each case.

| Case | Scenario the case exists to show | Where |
|---|---|---|
| `case-001` | The core return-readiness case. | all five sources |
| `case-a` | A maintenance record that explains the missing-history finding after the mock response. | `maintenance`: kind `maintenance_history` (`SYN-MH-A-0003`) |
| `case-b` | A technical record that disagrees with a document on engine identity: the technical record names engine serial `SYN-SN-77201`; the case-b release document in the evidence set states `SYN-SN-77210`. | `technical`: `tech-b-component-001`; `records`: component-release document `SYN-DOCB002` |
| `case-c` | A baseline in all five sources and a records entry for the blocked document. | `records`: `SYN-DOCC003`, `parseStatus` `blocked` |

The new finance records use the typed kind `maintenance_reserve` with the common fields (`recordedAmount`, `collectedAmount`, `currency`, `period`, `dueDate`, `rateBasis`, `rate`). The `case-001` finance records keep their earlier kinds.

The contracts fixture contains seven synthetic `specialist_flag` records: one each for `cape_town`, `ifrs16`, `gdpr` and `other`, and three for `sanctions` (regimes `EU`, `US` and `UK`). Each record has a distinct related requirement identifier and one of the accepted result values. It also contains one synthetic `cape_town_interest` record with reference-only values and no legal conclusion.

### Signing

The detached signature is RSA-PSS over the bytes of `<source>.manifest.json`, hash SHA-256, salt length 32 bytes, verified against the public key pinned in `SourceFixtureLoader`. The private key is held by the release owner and is never in the repository. It is kept on the maintainer's machine at `~/release-keys/airlinedemo-fixtures.pem` (mode 600, backed up by the owner). If it is lost, generate a new key pair, pin its public key (SubjectPublicKeyInfo DER, base64) in `SourceFixtureLoader` and re-sign all five manifests. After any change to a fixture or manifest, the release owner re-signs all five manifests:

```
openssl dgst -sha256 -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:32 \
  -sign <release-key.pem> -out backend/AirlineDemo.Api/Fixtures/sources/<source>.manifest.sig \
  backend/AirlineDemo.Api/Fixtures/sources/<source>.manifest.json
```

The digests and record counts that each `*Source.cs` carries in `CoreManifest` must match the manifest and the fixture file; the tests refuse a mismatch.

## Read outcomes: `SourceReadResult<TSuccess>`

Each outcome is a distinct type.

| Type | Meaning | Error code |
|---|---|---|
| `SourceReadSucceeded<TSuccess>` | Successful list or get. A list may be empty. | none |
| `SourceUnavailable<TSuccess>` | The source is in `unavailable` mode (DD-007). | `source_unavailable` |
| `UnknownSource<TSuccess>` | The source identifier is not registered. | unknown source |
| `SourceRecordNotFound<TSuccess>` | The source is available, but no record in this case has the requested `recordId`. | not found |

`source_unavailable` is never represented as an empty successful list. Callers must handle each outcome separately.

## Source-mode reads and dependent findings

Reads apply each case mode without changing fixture data: `available` returns the fixture result, `unavailable` returns
`SourceUnavailable<T>` without calling the port, `stale` sets `asOf: 2026-09-30` and `stale: true`, and `conflicting`
returns each record plus a simulated alternate version whose `recordVersion` is one greater; the list preserves both, and get returns the alternate.

Assessments compare technical serials cited by document witnesses with source serials for the claimed component
(for example, `Engine` matches `engine_1`, not `apu_1`). Unavailable or stale dependent
findings route to `Review` with reason `source_unavailable` or `source_stale`; mismatch or conflicting mode routes to
`Review` with reason `source_conflict`. Claims retain document witnesses and source witnesses named
`source:<sourceId>:<recordId>:v<recordVersion>`. Non-available modes create no mock requests or accepted events. Only available
mode refreshes pinned basis content; stale/conflicting reads are assessment-only. Basis history uses the append-only
`ICaseStateStore.Commit(scope, expectedRevision, ...)` operation.

## Records envelopes

`GET /api/v1/cases/{caseId}/sources/{sourceId}/records` returns exactly
`contractVersion`, `caseId`, `sourceId` and `records`. Each record contains exactly
`sourceId`, `recordId`, `recordVersion`, `asOf`, `caseId`, `kind`, `stale`,
`payload` and `synthetic`.

`GET /api/v1/cases/{caseId}/sources/{sourceId}/records/{recordId}` returns exactly
`contractVersion`, `caseId`, `sourceId` and `record`, whose value has the same
record shape. Both successful envelopes use the integer `contractVersion: 1`.
For the `records` source, each fixture payload must contain
`containsPersonalData` as a JSON boolean. A returned records payload contains
exactly `documentId`, `documentVersion` and `containsPersonalData`; fixture-only
fields, including synthetic personal-data fields, are not serialized in either
the list or get response.
The endpoint uses the case-scoped read-only `ISourceSystem<TPayload>` port and
bounded source-read admission service. Identity, role and requested-case scope
are checked before admission or source access; a returned record for another
case is rejected before serialization.

## Investigator source-record tools

| Tool | Arguments | Behavior |
|---|---|---|
| `list_source_records` | `sourceId` | Lists records from one authorized source for the investigator's bound case. |
| `get_source_record` | `sourceId`, `recordId` | Gets one authorized source record for the investigator's bound case. |

Neither accepts `caseId`; server binds case and role. Both call the read-only port, reject source/case mismatches and
delimit returned JSON as untrusted. Calls and returned results count against existing investigation/token limits.

Direct source access follows this matrix; all unlisted pairs, including unknown roles, are denied before port access.

| Role | Sources readable by direct investigator tools |
|---|---|
| `technical-reviewer` | `technical`, `maintenance`, `records` |
| `finance-reviewer` | `technical`, `finance` |
| `legal-reviewer` | `contracts`, `records` |
| `presenter` | none |
| `executive` | none |

Presenters and executives may see source summaries, not records; no source write, mode-change, network-fetch, approval or request tool is exposed.

Every coded error response contains exactly `contractVersion` and `error`, and
the error contains exactly `code`. An unavailable source returns HTTP 503 with
`error.code = source_unavailable` and no `records` or `record` property.
Unsupported integer path versions, including `/api/v2/...`, return HTTP 400
with `error.code = unsupported_contract_version`. These errors never include
source error details, record payloads or case data. An unauthenticated request
returns a coded 401; an out-of-scope or missing case returns the same bodyless
404. POST to either records path returns a bodyless 405 without accessing a
source.

## Source pins in a case basis

Gathered source basis content is JSON with `format: "source-record-pins-v1"` and a non-empty `pins` array. Each pin has the exact `sourceId`, `recordId`, `recordVersion` tuple, a complete source-record envelope in `content`, and `contentDigest` (lowercase SHA-256 of the UTF-8 content). Envelope JSON is compact; object properties are recursively sorted ordinally and array order is preserved. Pins are sorted by source ID, record ID, then numeric version. The store rejects empty, malformed or duplicate pins and verifies the envelope tuple and digest.

Identical gathered inputs do not append a version. Changed record content or version appends a later `BasisVersion` through `ICaseStateStore.Commit(scope, expectedRevision, ...)`, which validates pins and the append-only digest chain under the store lock. Earlier content and digests are never rewritten. A stale revision rejects the whole commit without adding a basis or audit event.

## Source-mode command

`POST /api/v1/cases/{caseId}/sources/{sourceId}/mode` changes the simulated mode of one of the five sources. The command requires an authenticated `presenter` session, a case scope, a valid anti-forgery token, and a JSON body with `commandId`, `expectedRevision` and `mode`. The server derives the actor, timestamp and audit reason; client-supplied attribution is ignored.

Validation order is authentication, presenter role, case scope, anti-forgery token, required command fields and command ID length, source and mode values, then replay lookup. The command ID is bound to its original actor, case, source, mode and expected revision. An exact authorized replay returns the original receipt; a different actor or payload conflicts. Accepted changes use `ICaseStateStore.Commit(scope, expectedRevision, ...)` so a concurrent revision change is rejected atomically. Only an accepted change appends a source-mode audit event.

Every response body uses `contractVersion: 1`. A successful change or exact replay returns exactly this receipt shape:

```json
{
  "contractVersion": 1,
  "commandId": "cmd-1",
  "caseId": "case-001",
  "sourceId": "technical",
  "priorMode": "available",
  "newMode": "unavailable",
  "priorRevision": 1,
  "newRevision": 2,
  "auditEventId": "audit-4"
}
```

Every error response contains exactly `contractVersion` and `error`; the error contains exactly `code` and `message`. Error responses do not include case data or a receipt.

| Status | Code | Message |
|---|---|---|
| 401 | `unauthenticated` | `Not signed in.` |
| 403 | `forbidden` | `Only a presenter can change source modes.` |
| 404 | `not_found` | `Case not found.` |
| 403 | `anti_forgery_failed` | `Anti-forgery validation failed.` |
| 400 | `invalid_request` | `commandId, expectedRevision and mode are required.` |
| 400 | `invalid_request` | `commandId must be at most 64 characters.` |
| 400 | `invalid_request` | `Request body must be a valid source-mode command.` |
| 422 | `unknown_source` | `Source is not one of the five case sources.` |
| 422 | `invalid_mode` | `Mode must be available, unavailable, stale or conflicting.` |
| 409 | `command_id_conflict` | `This command ID was already used for a different command.` |
| 409 | `stale_revision` | `The case revision has changed; reload and retry.` |
| 422 | `mode_unchanged` | `The source already has this mode.` |

`backend/AirlineDemo.Api.Tests/SourceModeCommandTests.cs` verifies complete response envelopes and the authorization, validation-order, replay, audit and expected-revision protections.

## Tests

`backend/AirlineDemo.Api.Tests/SourcePortContractTests.cs` covers AC-001 (`PortExposesCaseScopedReadOperationsOnly`), AC-002 (`RecordRequiresCompleteProvenanceAndSyntheticFlag`) and AC-003 (`UnavailableAndUnknownSourcesHaveDistinctTypedResults`).

## Limitations

All data is synthetic. This contract is not an integration with any real maintenance, ERP, records or lease-management product.
