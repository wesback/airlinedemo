# Aircraft case history API

`GET /api/cases/{caseId}/history` returns read-only history for an authenticated,
in-scope case. It accepts optional query selectors `runId` and `basisId`.

When omitted, `runId` resolves to the run on the latest recorded case event.
`basisId` resolves to the latest basis-bearing event in that run. Supplying only
one selector does not broaden the other: a missing run or basis tuple returns
404.

The success response fields are `caseId: string`, `runId: string`,
`basisId: string`, and `events: CaseJourneyEvent[]`. Events are in persisted
sequence order and contain `eventId: string`, `runId: string`,
`sequence: positive integer`, `caseId: string`, `eventType: string`,
`basisId: string`, `basisVersion: positive integer`, `actorRole: string`,
`summary: string`, `capabilityLabel: string`, and `witnesses:
CaseJourneyWitness[]`. Each witness retains its exact `witnessId: string`,
`documentId: string`, `documentVersion: positive integer`, `page: positive
integer`, `basisId: string`, and `basisVersion: positive integer`; prior-run
events and historical witness values are not replaced with current values.

Requests without a valid session return 401. A case outside the caller's
configured scope, a case absent from the server store, or a nonexistent
case/run/basis tuple returns the same empty 404 response, without event or
witness data.
Store or serialization failures return 500 with a generic error body and no
partial history response. This route does not mutate case history.
