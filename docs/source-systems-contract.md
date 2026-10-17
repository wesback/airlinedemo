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

## Finance payloads

`FinancePayload` is shared by exactly four finance kinds. Every value is a string; a payload must contain exactly the fields listed for its kind.

| Kind | Required payload fields |
|---|---|
| `rent` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`) |
| `maintenance_reserve` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`), `rateBasis` (`per_flight_hour` or `per_cycle`), `rate` (non-negative decimal string) |
| `security_deposit` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`) |
| `reserve_reimbursement` | `recordedAmount` (non-negative decimal string), `collectedAmount` (non-negative decimal string), `currency` (three uppercase letters), `period` (string, `YYYY-MM`), `dueDate` (string, `YYYY-MM-DD`), `claimId` (string), `status` (`claimed`, `approved` or `paid`) |

`FinanceRecordValidator` rejects unknown kinds, missing fields and fields not listed for that kind. Reserve drafts consume only `maintenance_reserve` and `reserve_reimbursement`: billed is `recordedAmount`, collected is `collectedAmount`, underbilled is the non-negative difference, and overdue is underbilled when `dueDate` is before the draft period's last calendar day. Reimbursable is `recordedAmount` only for an approved or paid reimbursement. The draft total is the sum of underbilled amounts; it makes no profit, revenue or cash-recognition claim.

## Signed fixture cases

Each of the five sources (`technical`, `maintenance`, `finance`, `records`, `contracts`) ships one fixture file under `backend/AirlineDemo.Api/Fixtures/sources/`, with a manifest (`<source>.manifest.json`) and a detached signature (`<source>.manifest.sig`). The manifest lists `owningCaseIds` (`case-001`, `case-a`, `case-b`, `case-c`), the SHA-256 digest of the fixture file and the record count of every source. Every record's `caseId` must be one of the owning IDs, and each source holds at least one record for each case.

| Case | Scenario the case exists to show | Where |
|---|---|---|
| `case-001` | The core return-readiness case. | all five sources |
| `case-a` | A maintenance record that explains the missing-history finding after the mock response. | `maintenance`: kind `maintenance_history` (`SYN-MH-A-0003`) |
| `case-b` | A technical record that disagrees with a document on engine identity: the technical record names engine serial `SYN-SN-77201`; the case-b release document in the evidence set states `SYN-SN-77210`. | `technical`: `tech-b-component-001`; `records`: component-release document `SYN-DOCB002` |
| `case-c` | A baseline in all five sources and a records entry for the blocked document. | `records`: `SYN-DOCC003`, `parseStatus` `blocked` |

The new finance records use the typed kind `maintenance_reserve` with the common fields (`recordedAmount`, `collectedAmount`, `currency`, `period`, `dueDate`, `rateBasis`, `rate`). The `case-001` finance records keep their earlier kinds.

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
