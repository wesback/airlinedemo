# Aircraft case-pack manifest

The fixture loader accepts schema version `1` with exactly three cases: `cp-case-a`, `cp-case-b`, and `cp-case-c`. The fixed scenario date is `2026-10-01`. Each case has one aircraft, one engine, two components, and a synthetic aircraft registration and four synthetic serial labels.

## Fictional identity fields

Each case record has:

- `aircraftRegistration`: an object with `value` and `synthetic`.
- `serialNumbers`: exactly four objects with `kind`, `value`, and `synthetic`. The kinds are one `aircraft`, one `engine`, and two `component`. Each component serial also has a `componentId` matching exactly one component ID in that case. Non-component serials omit `componentId`.

Every identity value must start with `SYN-CP-` and use the exact case-specific value below. Every marker must be `synthetic: true`. Duplicate values, incorrect kinds or counts, absent component IDs, and values that do not match this table are rejected.

| Case | Registration | Aircraft serial | Engine serial | Component serials |
|---|---|---|---|---|
| `cp-case-a` | `SYN-CP-A-REG` | `SYN-CP-A-AIR-001` | `SYN-CP-A-ENG-001` | `SYN-CP-A-CMP-001`, `SYN-CP-A-CMP-002` |
| `cp-case-b` | `SYN-CP-B-REG` | `SYN-CP-B-AIR-001` | `SYN-CP-B-ENG-001` | `SYN-CP-B-CMP-001`, `SYN-CP-B-CMP-002` |
| `cp-case-c` | `SYN-CP-C-REG` | `SYN-CP-C-AIR-001` | `SYN-CP-C-ENG-001` | `SYN-CP-C-CMP-001`, `SYN-CP-C-CMP-002` |

## Version and content hash

Pack version `1.0.2` introduces the required identity labels; manifests that do not parse as a numeric version at least `1.0.2` are rejected. Any change to canonical manifest fields or to a listed file requires a new pack version and a recalculated SHA-256.

The hash is SHA-256 over compact UTF-8 JSON serialized with `System.Text.Json`'s default encoder. The `sha256` field itself is excluded. Canonical top-level fields are ordered `schemaVersion`, `packVersion`, `scenarioDate`, `productOwnerReviewReference`, `cases`, and `files`. Cases are sorted by `caseId`; their canonical fields are `caseId`, `synthetic`, `generatorReference`, `aircraftCount`, `engineCount`, `components`, `documents`, `aircraftRegistration`, and `serialNumbers`. Components are sorted by `componentId`, with sorted `evidenceDocumentPaths`; documents are sorted by `path`; serials are sorted by `kind`, then `componentId`. Files are sorted by path and hash each file's exact bytes, including all three case JSON files and all evidence documents. No file content is normalized.

`productOwnerReviewReference` points to `issue-473`. Before fixture merge, the product owner must approve the three labels and expected case outcomes against pack version `1.0.2` and the exact SHA-256 in `manifest.json`. The reference is included in the canonical hash.
