# Aircraft case-pack manifest v1

The server accepts only an exact v1 JSON contract; malformed JSON, unknown properties, missing data, wrong types, or failed validation reject the whole manifest. Required fields: integer `schemaVersion: 1`, non-empty `packVersion`, real `scenarioDate` in `YYYY-MM-DD` form (no time/offset), `sha256` (64 lowercase hex), non-empty `productOwnerReviewReference`, `cases`, and `files`.
There are exactly three cases. Each has non-empty `caseId` and `generatorReference`, `synthetic: true`, `aircraftCount: 1`, `engineCount: 1`, two `components`, and 8–12 `documents` (24–36 total). Each component has non-empty `componentId` and maps one or more `evidenceDocumentPaths` to documents in its case. Every document has a unique-in-case `path`, `synthetic: true`, and non-empty `generatorReference`. `files` is the unique list of relative fixture paths; it exactly matches supplied file contents and includes every document path.
## Content hash

`sha256` hashes the UTF-8 bytes of compact JSON (no BOM or indentation), serialized with `System.Text.Json`'s default encoder. Exclude the `sha256` field. Canonical fields are ordered as follows: `schemaVersion`, `packVersion`, `scenarioDate`, `productOwnerReviewReference`, `cases`, `files`.

Sort `cases` by `caseId`; case fields are ordered `caseId`, `synthetic`, `generatorReference`, `aircraftCount`, `engineCount`, `components`, `documents`. Sort components by `componentId` (fields `componentId`, sorted `evidenceDocumentPaths`); sort documents by `path` (fields `path`, `synthetic`, `generatorReference`). Sort files by `path`; each has fields `path`, `sha256`, where the latter is lowercase SHA-256 of the file's exact bytes. String values use the serializer's JSON escaping; each indicated sort is ordinal.
No file contents are normalized before hashing. Any file, mapping, provenance, review reference, date, or other canonical-field change requires a new `packVersion` and matching recalculated hash. Comparison takes previous/current manifests and file bytes directly, without Git or a child process. The digest detects drift but does not provide authenticity.

## Publication migration note

Pack publication is atomic at the visibility boundary: consumers receive one immutable snapshot containing the pack version, hash, case records, and document references. A failed publication never exposes a partial pack and does not alter a prior published pack or unrelated case data. Immutable evidence blobs written before a failed commit may remain in the evidence store, but no published snapshot references them. Consumers must use the published snapshot, rather than treating individual case or blob writes as proof that a pack is ready.
