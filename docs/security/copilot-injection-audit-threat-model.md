# Threat Model: Copilot Injection Defence and Audit Replay

Scope: Addendum A of [docs/prd/role-aware-case-copilot.md](../prd/role-aware-case-copilot.md) (FR-019 to FR-032, NFR-015 to NFR-019). Data is synthetic. This model covers the new Content Safety egress, the screening verdict, the copilot trail details and the Activity view. The existing copilot threat boundaries (model isolation, confirmation, ledger) are unchanged and stay in force.

## Assets

| Asset | Why it matters |
| --- | --- |
| Source record field text (for example `mx-task-card-001` `taskText`) | Attacker-controlled content; must never steer a tool call or reach the model. |
| Screening verdict (`detected`, `clear`, `unavailable`) | Drives the warning chip and a trail row; a forged `clear` hides an attack from the presenter. |
| Copilot trail (ledger rows and `copilot_invocation_details`) | Evidence that every copilot action was attributed, scoped and reviewable. |
| Gateway token credential and APIM system identity | Grants access to the model route and the Content Safety route. |
| `Copilot__AuditKey` | Keys the `callerRef` HMAC; disclosure links trail rows to users outside the app. |

## Trust Boundaries

1. Browser to API: same-origin session, role from the existing demo identity, antiforgery on writes.
2. API to model through APIM: model-safe projection only; record text never crosses (CON-006).
3. API to Content Safety through APIM (new): record field text, managed identity token for audience `cognitiveservices.azure.com`, private endpoint, public network access off, local auth off, deployer network security perimeter in Enforced mode.
4. API to SQL: app identity with `SELECT` and `INSERT` only on the trail tables.
5. Trail to viewer: role-filtered by source visibility (FR-032).

## Threats and Mitigations

| ID | Threat (STRIDE) | Mitigation | Requirement / test |
| --- | --- | --- | --- |
| TM-01 | Tampering: instruction text in a record makes the model call `advance_stage`. | Record text never enters a model message; `advance_stage` only creates a proposal that a presenter confirms in trusted UI; detection is advisory and adds no tool call. | FR-025, AC-049 |
| TM-02 | Spoofing: the model or a record forges a `screening` value. | `screening` is set server-side only inside `untrustedRecords`; the model observation allowlist is unchanged and carries no verdict. | FR-019, AC-043 |
| TM-03 | Information disclosure: record text leaks through logs, exceptions, cache keys or stored rows. | Cache key uses identity and SHA-256 only; no log or exception carries field text; details rows have no text column. | NFR-015, AC-057, FR-029, AC-053 |
| TM-04 | Information disclosure: Content Safety reached from the internet or with keys. | Public network access off, local auth off, private endpoint, APIM managed identity only, Enforced perimeter association. | NFR-018, AC-060 |
| TM-05 | Information disclosure: a denied read still sends text to Content Safety. | Screening runs after authorization; a denied read makes zero screening calls. | FR-020, AC-043 |
| TM-06 | Denial of service: a slow or failing Content Safety stalls the answer. | 3-second deadline with the turn's linked token, first failure short-circuits the request, `unavailable` is not retried or cached. | FR-022, NFR-016, AC-046, AC-058 |
| TM-07 | Repudiation: a copilot action leaves no trace. | Every tool call that reaches a tool is ledgered, including pre-dispatch refusals against the selected case; gate rows commit with the release or the release rolls back. | FR-026, FR-028, FR-032, AC-050, AC-052, AC-056 |
| TM-08 | Tampering: a trail entry is edited or deleted. | No update or delete route or store member; SQL grants are `SELECT`, `INSERT` only. | FR-030, AC-054 |
| TM-09 | Information disclosure: a viewer learns record identities of a source their role may not see. | Record refs and screening are filtered per viewer role with `SourceSummaryService.CanSeeDetails`. | FR-032, AC-056 |
| TM-10 | Information disclosure: a model-supplied foreign case ID is stored in another case's trail. | Refused calls are ledgered against the trusted selected case only; the model's case ID is never stored. | FR-032, AC-056 |
| TM-11 | Tampering: a flagged field value renders as markup in the record pane. | `<mark>` wraps the value as text; no HTML from a record is rendered. | FR-023, AC-047 |

## Residual Risks

- Prompt Shields may not flag a given text (PRD RISK-010); containment does not depend on detection (TM-01).
- A live model may answer without reading the source (PRD RISK-009); the scripted scenario proves the server path only.
- Rotating `Copilot__AuditKey` makes earlier entries show "unknown user" (PRD RISK-014).
- Network security perimeter support for the account is confirmed during the infrastructure slice (PRD RISK-015).
