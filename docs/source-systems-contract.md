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

## Read outcomes: `SourceReadResult<TSuccess>`

Each outcome is a distinct type.

| Type | Meaning | Error code |
|---|---|---|
| `SourceReadSucceeded<TSuccess>` | Successful list or get. A list may be empty. | none |
| `SourceUnavailable<TSuccess>` | The source is in `unavailable` mode (DD-007). | `source_unavailable` |
| `UnknownSource<TSuccess>` | The source identifier is not registered. | unknown source |
| `SourceRecordNotFound<TSuccess>` | The source is available, but no record in this case has the requested `recordId`. | not found |

`source_unavailable` is never represented as an empty successful list. Callers must handle each outcome separately.

## Tests

`backend/AirlineDemo.Api.Tests/SourcePortContractTests.cs` covers AC-001 (`PortExposesCaseScopedReadOperationsOnly`), AC-002 (`RecordRequiresCompleteProvenanceAndSyntheticFlag`) and AC-003 (`UnavailableAndUnknownSourcesHaveDistinctTypedResults`).

## Limitations

All data is synthetic. This contract is not an integration with any real maintenance, ERP, records or lease-management product.
