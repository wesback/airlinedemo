---
prd_id: "role-aware-case-copilot"
title: "Role-Aware Case Copilot over Five MCP Tools"
status: "draft"
version: "0.1.0"
owners: ["wesback"]
reviewers: ["wesback"]
created_date: "2026-10-30"
last_updated: "2026-10-30"
product_goal_ids: ["GOAL-001", "GOAL-002", "GOAL-003", "GOAL-004"]
product_goal_smart_status: "deferred"
fr_to_ac_coverage_threshold_pct: 80.0
fr_to_goal_coverage_threshold_pct: 100.0
diagram_format: "none"
lineage:
  supersedes: []
  superseded_by: []
source_brd_id: null
requirement_id_prefixes:
  fr: "FR"
  ac: "AC"
  nfr: "NFR"
  con: "CON"
  goal: "GOAL"
license: "CC-BY 4.0 (Microsoft HVE-Core)"
---

# Role-Aware Case Copilot over Five MCP Tools

> **role-aware-case-copilot** | Status: draft | Version: 0.1.0 | Last Updated: 2026-10-30

## Executive Summary

This feature adds a bounded conversational entrypoint to the existing synthetic Aircraft Return Readiness Demonstrator. A signed-in user can ask one question, see a role-scoped answer, inspect authorized source records in a separate pane, and, as presenter, request the final simulated aircraft release. Exactly five MCP tools expose case work: `list_my_cases`, `get_case_summary`, `get_open_points`, `read_source`, and `advance_stage`. The example questions are “List the planes I need to verify,” “What are the open points on this aircraft,” “Show me the maintenance records behind that finding,” and “Move this aircraft to the next stage.”

MCP is a thin adapter, not a workflow owner. The API continues to own authentication, case scope, role/source entitlements, source admission, stage/status projections, release gates, and audit persistence. This feature includes API prerequisites for safe summary and open-point projections, an independent invocation ledger, immutable confirmation proposals, and atomic release receipts. These capabilities do not already exist as copilot contracts. The delivered source-summary work, issue #363, is dependency evidence, not an open prerequisite.

A minimal model client and one-question Q&A UI are included in the first release. They reuse the existing lazy, manifest-governed model gateway client and per-execution limits. The new copilot input contract permits an explicitly sent free-text user question and runtime-validated, API-owned structural context. Raw source text is UI-only and must never enter any outbound model request, including later tool-result messages and retries. The investigator's exact-hash synthetic-evidence gate remains unchanged.

The primary release metric is 42 of 42 defined acceptance criteria verified, with zero unintended effects in the hostile-model contract suite. These are future acceptance targets, not measured results. The intended release horizon is the first same-origin demo feature release after wesback's human PRD-merge gate and manual HVE RPI planning/implementation. There is no calendar delivery commitment, live accuracy claim, aviation clearance, or approval in this draft. OAuth OBO, MCP APIM routing, persisted multi-turn chat, other business writes, live data, and cloud changes are excluded.

---

## Research Readiness

Research status: `Required`

Rationale: The unfamiliar MCP transport, request-identity, confirmation, gateway-client, and model-input security contracts materially affect the requirements. The completed research executed two Wider/Deeper/Contrarian cycles; it did not install packages, implement the feature, or validate a live model.

Research artifact: [Completed primary research](../research/2026-10-30/role-aware-copilot-mcp-research.md).

Planning blockers: None for product scope and behavior. D1-D9 are resolved below as precise requirements. Pending human merge approval and future executable verification are release gates, not unanswered product choices. The user's subsequent delivery direction assigns planning/implementation to manual HVE RPI, superseding research's intended automatic pipeline handoff without changing its evidence. The parent must resolve a feature-specific way to satisfy the repository's human PRD-merge gate without automatic pickup before delivery proceeds; no skip mechanism or exemption is asserted. Research evidence remains authoritative for facts; this PRD owns the product dispositions.

### Research Finding Dispositions

Each primary-artifact link refers to the complete evidence log, not a new research record.

| Gap ID | Evidence IDs | Affected targets | Disposition | Rationale | Primary research artifact |
| --- | --- | --- | --- | --- | --- |
| F1 | W1, W2, W3, C1 | DD-001, FR-001, NFR-013, AC-001 | Incorporated | Host SDK 2.3.0 in net10.0; demo sessions are not OAuth. | [Research](../research/2026-10-30/role-aware-copilot-mcp-research.md#f1-same-application-hosting-is-supported-but-demo-sessions-are-a-bounded-integration) |
| F2 | C2, C3 | FR-006, FR-010, NFR-001, AC-008, AC-009, AC-017 | Incorporated | API owns existing source admission and final-release policy; no adapter gate algorithm. | [Research](../research/2026-10-30/role-aware-copilot-mcp-research.md#f2-final-release-and-source-read-policy-already-belong-to-the-api) |
| F3 | W4 | DD-004, CON-002, AC-037 | Deferred | MCP APIM exposure is a separate feature; existing model APIM route stays unchanged. | [Research](../research/2026-10-30/role-aware-copilot-mcp-research.md#f3-apims-managed-remote-mcp-feature-does-not-document-demo-cookie-authorization) |
| F4 | W5, W6, C2, C6 | FR-008, FR-009, FR-010, AC-012, AC-014, AC-016 | Incorporated | UI navigation or model acceptance is not human approval; approval is server-held. | [Research](../research/2026-10-30/role-aware-copilot-mcp-research.md#f4-opening-a-confirmation-url-is-not-release-approval) |
| F5 | W6 | DD-006, FR-013, FR-014, AC-026, AC-027 | Revised | Include the minimal client/UI and deterministic source exclusion, not annotation-only immunity. | [Research](../research/2026-10-30/role-aware-copilot-mcp-research.md#f5-an-untrusted-data-envelope-does-not-prove-that-a-future-model-ignores-instructions) |
| F6 | C5, C7 | FR-007, FR-011, FR-017, AC-010, AC-011, AC-034 | Incorporated | Add API-owned append-only admission/outcome persistence without read-driven business revision changes. | [Research](../research/2026-10-30/role-aware-copilot-mcp-research.md#f6-read-auditing-needs-a-separate-api-owned-persistence-operation) |
| F7 | C4, C8 | FR-003, FR-004, FR-005, AC-004, AC-005, AC-006 | Incorporated | Raw case and finance reads are not safe role-projected point contracts. | [Research](../research/2026-10-30/role-aware-copilot-mcp-research.md#f7-existing-raw-casefinance-reads-are-not-the-proposed-role-aware-open-points-api) |
| F8 | W3, W5, W8, W9, W10 | DD-002, FR-001, FR-002, AC-001, AC-002, AC-016 | Incorporated | Explicit stateless transport, per-request identity, ordinary proposal results and exact retry. | [Research](../research/2026-10-30/role-aware-copilot-mcp-research.md#f8-stateless-mcp-and-api-owned-confirmation-avoid-transport-session-authority) |
| F9 | C10, C11, C14 | DD-007, FR-012, FR-013, CON-005, AC-024, AC-025 | Incorporated | Reuse the approved provider boundary; human merge approves a separate copilot content contract. | [Research](../research/2026-10-30/role-aware-copilot-mcp-research.md#f9-reusing-the-gateway-client-does-not-authorize-new-copilot-content) |
| F10 | W11, W12, W13, C11, C14 | FR-014, FR-015, FR-016, AC-027, AC-028, AC-029, AC-042 | Incorporated | Host validates effects, arguments, citations and rendering irrespective of model obedience; live accuracy remains unmeasured. | [Research](../research/2026-10-30/role-aware-copilot-mcp-research.md#f10-the-client-must-constrain-effects-even-when-model-instructions-fail) |

---

## Product Context

The demonstrator already exposes scoped case packs, scenes, worklists, source summaries, source records, and a presenter-only final release. Users currently navigate those surfaces themselves. The copilot improves discoverability without moving reviewer judgment, release authority, or source-read policy into a model.

Canonical companion references are [Aircraft Return Readiness](aircraft-return-readiness.md) (ARR), [Aircraft Case Pack](aircraft-case-pack.md) (CP), [Source Systems](aircraft-source-systems.md) (SRC), and [Role Workspaces](role-workspaces.md) (RW). SRC version 0.9.1 supplies **SRC:DD-023** source entitlements and **SRC:DD-024** read boundaries; this is not ARR:DD-023. ARR:DD-025 and ARR:DD-034 supply the synthetic-only and deterministic model-authority boundaries. All unqualified identifiers in this PRD are document-local.

Existing inputs, not new copilot contracts:

| Existing route or seam | Permitted reuse | Boundary |
| --- | --- | --- |
| `GET /api/v1/cases` | Scoped list, canonical status and role-owned `nextAction` | Reproject only allowed fields; never let the model supply role or user. |
| `GET /api/v1/cases/{caseId}/scene` | Canonical state projection and trusted UI aircraft identity | No whole-scene/history/document prose forwarded to MCP/model. |
| `GET /api/v1/workspace/worklist` | Existing role-owned work items | Not an open-point detail contract. |
| `GET /api/v1/cases/{caseId}/sources` | Delivered metadata summary (#363 closed) | Preserve summary visibility and errors; no record bodies. |
| Source `/records` and `/records/{recordId}` routes | Authorized reads through existing API admission | No independent MCP counter, cache, source-port bypass or privileged caller. |
| `POST /api/cases/{caseId}/release` | Only business write target and existing release policy | New proposal/approval/receipt support must surround the API-owned release commit. |
| `GET /api/cases/{caseId}` and raw finance reserve/obligation reads | Internal authorized API inputs to a new projection only | Never expose the raw responses to MCP/model; do not assume point-role filtering exists. |
| `Program.cs` lazy manifest-configured `IChatClient` | Existing governed provider registration, lazy credentials and correlation | Not authorization for new copilot content; no direct-endpoint fallback. |

Trust boundaries are the browser/server session boundary, model/host dispatch boundary, MCP/API boundary, API/source-edge boundary, and persistence/atomic-commit boundary. The server owns all authority across them. Source data, model output, user questions, transport session identifiers, and caller-provided “origin” labels are not authority.

---

## Users and Personas

| Persona | Job and pain point | Success outcome |
| --- | --- | --- |
| Technical reviewer | Find unresolved technical work and inspect maintenance/records evidence without navigating unrelated domains. | Sees only technical points and entitled technical, maintenance and records data. |
| Finance reviewer | Locate reserve work and its authorized technical/finance evidence. | Sees only finance points and technical/finance sources; no legal or technical-point detail. |
| Legal reviewer | Locate legal obligations and trace entitled evidence. | Sees only legal points and contracts/records sources; no finance/technical points. |
| Presenter | Explain coarse readiness and perform the final simulated release after human review. | Coarse summaries and explicit trusted confirmation; no reviewer decisions or record prose. |
| Executive | Understand coarse progress without underlying findings or source prose. | Read-only coarse summaries; cannot propose, approve or execute release. |
| Unknown-role or anonymous caller | No authorized feature persona. | Deterministic denial without data-port access or unauthorized case disclosure. |

“My cases” means the current server-owned demo user's `CaseScopes`, filtered by the API. “Need to verify” is the existing role-owned next action, not a new MCP readiness calculation. Finance's entitlement to technical sources does not grant access to technical-reviewer point details; legal's records entitlement does not grant access to every point referring to records.

---

## Design Decisions

| ID | Decision, authority and effect |
| --- | --- |
| DD-001 | D1: Use current demo sessions in the existing net10.0 app. Real OAuth OBO and general external OAuth connectors are deferred. |
| DD-002 | Use SDK `ModelContextProtocol.AspNetCore` 2.3.0, explicit `Stateless` SessionMode, Streamable HTTP at same-origin `/mcp`, and wire compatibility scope **2025-11-25 only** for this release. SDK support for 2026-07-28 is evidenced, but its additional bootstrap is not an acceptance commitment here. |
| DD-003 | D2: `advance_stage` means only presenter final simulated release. It never advances UI gates or performs technical, finance or legal decisions. |
| DD-004 | D5: Same-origin release; MCP APIM routing is deferred. Reuse the existing governed model gateway route without infrastructure, provider-manifest, credential or cloud changes. |
| DD-005 | D3/D7: Immutable API proposal, trusted UI approval, then exact tool retry. Expiry is creation plus five minutes; approval does not extend it. Success consumes approval atomically with release, original receipt and business audit. |
| DD-006 | D6/D8/D9: Include a minimal model client and one-question UI. Free questions are untrusted USER messages; context is closed structure; raw sources are UI-only across the entire model execution. No persisted conversation product. |
| DD-007 | Human merge of this PRD by wesback explicitly approves FR-013's new copilot input contract, not an investigator gate relaxation or provider change. Until that merge, no implementation/content-policy approval is claimed. |
| DD-008 | D4: API-owned invocation ledger separate from business revision. Authorized-case attempts, including denials, project into case activity; list/no-case/anonymous/out-of-scope/missing attempts remain caller/security-only. |
| DD-009 | Safe summary/open-point projections are in-scope API prerequisites. They expose closed fields and enforce role/case visibility before source calls; MCP does not repair raw APIs by applying its own business filter. |
| DD-010 | Source matrix and limits remain SRC:DD-023/DD-024, with actual current endpoint failures authoritative over older prose. Both direct and MCP record reads use the same session key and admission store. |
| DD-011 | Release notes originate in trusted UI input, never model context. The host binds that note to the authenticated request context, then to the immutable proposal; models cannot set or alter a note or approve a proposal. |
| DD-012 | Subsequent user direction: authoring ends at draft creation; manual HVE RPI owns subsequent planning, security/quality review and implementation here, not automatic post-merge story handoff. Preserve the human PRD-merge gate. The parent must resolve the feature-specific non-pickup approval route; no pipeline skip mechanism is assumed. Later work slices retain the repository's at-most-500-changed-line discipline including tests; this PRD creates no plans, issues or labels. |

---

## Product Goals

All goals are MUST for the first same-origin feature release; wesback owns measurement acceptance.

| ID | Goal | Target and measurement window |
| --- | --- | --- |
| GOAL-001 | Make the four example questions reachable through five role-aware tools without widening resource visibility. | By release acceptance, AC-001 through AC-009 and AC-040 pass: 10/10 criteria. |
| GOAL-002 | Keep final release human-authorized, exactly bound, atomic and replay-safe while preserving existing gates. | By release acceptance, AC-012 through AC-023 pass: 12/12 criteria; zero duplicate releases/business audits in retry tests. |
| GOAL-003 | Bound model effects and exclude raw source prose from every model request without weakening the investigator. | By release acceptance, AC-024 through AC-033 and AC-042 pass: 11/11 criteria; zero source-body leaks or unintended hostile-model effects. |
| GOAL-004 | Make feature activity accountable without business revision drift, prompt persistence or new cloud operations. | By release acceptance, AC-010, AC-011 and AC-034 through AC-039 and AC-041 pass: 9/9 criteria. |

**SMART Evaluation**: Authoring self-check; formal assessment remains pending.

- [x] **S**pecific: One bounded same-origin case-copilot release.
- [x] **M**easurable: Defined criterion sets and zero-effect assertions.
- [ ] **A**chievable: Implementation and production-host tests have not run.
- [x] **R**elevant: Discoverability without transferring human/API authority.
- [x] **T**ime-bound: First feature-release acceptance, not an invented date.

**Status**: deferred. This author self-check is not the Validate→Finalize assessment.

---

## Functional Requirements

All FRs are MUST. The following tables and contract definitions are normative parts of their referenced FRs.

| ID | Requirement / expected outcome | Actor and trigger | Product goals | Acceptance criteria |
| --- | --- | --- | --- | --- |
| FR-001 | Expose exactly the five named MCP tools with strict schemas through the existing app's same-origin, explicitly stateless transport. | Authenticated client initializes or calls a tool. | GOAL-001 | AC-001, AC-040 |
| FR-002 | Resolve current server-owned demo identity on every MCP, Q&A and confirmation request; enforce Origin, Host and CSRF controls before sensitive dispatch. | Any request, including after switch/sign-out. | GOAL-001 | AC-002, AC-003 |
| FR-003 | Return only current scoped cases and their allowed summary fields through `list_my_cases`. | Signed-in user requests their work. | GOAL-001 | AC-004 |
| FR-004 | Provide an API-owned safe `get_case_summary` projection, reusing canonical API statuses and role next actions without scene/raw-state prose. | User requests selected-case readiness. | GOAL-001 | AC-005 |
| FR-005 | Provide an API-owned open-point projection with the precise role/field outcomes below, not the existing worklist or raw finance response. | User requests selected-case open work. | GOAL-001 | AC-006, AC-007 |
| FR-006 | Route `read_source` metadata and record operations through their existing API contracts with unchanged entitlements, errors and shared record-read admission. | User requests source evidence. | GOAL-001 | AC-008, AC-009, AC-040 |
| FR-007 | Persist API-owned durable invocation admission before data/model-sensitive work, and correlate an append-only outcome; fail closed if admission storage is unavailable. | Any tool, Q&A execution or confirmation control action. | GOAL-004 | AC-010, AC-011, AC-041 |
| FR-008 | Create and persist an immutable final-release proposal bound to the canonical command, creator, exact session and creation-relative expiry. | Presenter first invokes `advance_stage` with trusted note context. | GOAL-002 | AC-012, AC-013 |
| FR-009 | Record approve/decline/cancel only through authenticated, same-origin, CSRF-protected trusted UI showing the exact proposal. | Presenter explicitly selects a confirmation control. | GOAL-002 | AC-014, AC-015 |
| FR-010 | Execute final simulated release only on an exact approved tool retry, revalidating current API role/scope/revision/gates atomically with the business commit. | Presenter retries approved `advance_stage`. | GOAL-002 | AC-016, AC-017, AC-018, AC-019, AC-020, AC-021 |
| FR-011 | Persist the original release receipt with successful consumption, and return that exact receipt on same-command replay without another business change. | Same presenter/session retries a successful proposal. | GOAL-002 | AC-022, AC-023 |
| FR-012 | Reuse the existing lazy, manifest-approved `IChatClient` provider boundary and correlation behavior without direct fallback or privileged tool credentials. | User explicitly sends one question. | GOAL-003 | AC-024, AC-025 |
| FR-013 | Admit only the reviewed copilot question/structural-context contract below; preserve the investigator gate and keep source bodies out of all model messages. | Before every model attempt, including retries and tool follow-ups. | GOAL-003 | AC-025, AC-026, AC-027 |
| FR-014 | Validate every model-requested tool name, schema and selected-case binding before same-user MCP dispatch; never execute arbitrary URLs, files or model approval. | Model emits a proposed tool call. | GOAL-003 | AC-027, AC-028, AC-042 |
| FR-015 | Bind citation/navigation data only to source/record/version tuples actually returned to this user, and render answers as escaped plain text. | Host renders a model answer or evidence link. | GOAL-003 | AC-029, AC-030 |
| FR-016 | Deliver one-question UI with explicit send, selected-case binding, synthetic-input notice, separate escaped record pane and trusted confirmation; store no multi-turn history. | User opens the copilot surface or sends a question. | GOAL-003 | AC-030, AC-031, AC-032, AC-033 |
| FR-017 | Expose authorized copilot activity independently of existing business history/run/basis projections and suppress unauthorized case activity. | User reads case activity after a copilot attempt. | GOAL-004 | AC-034, AC-035 |
| FR-018 | Return the exact bounded error contracts below and isolate new route-specific execution limits from existing investigator/source behavior. | Invalid, denied, failed or over-budget execution. | GOAL-004 | AC-036, AC-037, AC-038, AC-039, AC-041 |

**Transport and tool schemas (FR-001/FR-014).** Tool names are case-sensitive. Every argument object rejects duplicate keys, extra properties, incorrect types, null required values, and coercion. IDs are nonempty ASCII strings of at most 128 characters; case/source/record IDs additionally require server-side canonical membership and entitlement, not just a regex. Revision numbers are integers in `0..9223372036854775806`. Optional properties must be omitted, not sent as null. No tool accepts identity, role, cookie, CSRF, prompt, template, approval Boolean, endpoint URL or network/file arguments.

| Tool | Exact arguments | API owner / result |
| --- | --- | --- |
| `list_my_cases` | `{}` | Existing scoped case list reprojected to FR-003's structural summary allowlist. No selected case required. |
| `get_case_summary` | `{caseId: string}` | New `GET /api/v1/cases/{caseId}/copilot/summary` safe projection. |
| `get_open_points` | `{caseId: string}` | New `GET /api/v1/cases/{caseId}/copilot/open-points` safe projection. |
| `read_source` | `{caseId: string, sourceId?: enum, operation: enum, recordId?: string}`; `operation` is `summary`, `list`, or `record` | `summary` forbids sourceId/recordId and returns existing whole-case metadata summary; `list` requires sourceId and forbids recordId; `record` requires both. List/record use existing record API. |
| `advance_stage` | `{caseId: string, expectedRevision: integer, proposalId?: string}` | Absent proposalId creates proposal; present proposalId requests exact release/replay. API owns final-release policy and receipt. |

`advance_stage` obtains its note from trusted UI request context, never model arguments. Q&A submission accepts exactly `{question: string, selectedCaseId?: string, releaseNote?: string}`. `releaseNote` is server-only control input: raw length 1..2000 UTF-16 code units, trimmed nonempty, normalized by the same trim rule as ReleaseCommand. It is excluded from every model request, answer, invocation ledger and diagnostic. Without note context, proposal creation returns `400 release_note_required` and the UI requests a note; no proposal or release exists. A new explicit send supplies it. Direct same-origin MCP users obtain the same note context through trusted UI, not a sixth tool.

The host retains only a short-lived opaque execution binding to that trusted context. The immutable proposal stores the normalized note. After approval, a trusted UI retry button supplies the frozen `caseId`, `expectedRevision`, `proposalId` and server-held note binding to the same MCP tool, without invoking the model. It is still a tool retry, not a business-write UI bypass. A changed note requires cancelling and creating a new proposal. Guessing a proposal ID does not establish the execution binding.

New API control-plane prerequisites are authenticated Q&A submission, immutable proposal create/read, approve/decline/cancel, and approved release/replay inside the existing release business path. They are not additional MCP tools. Confirmation URLs are relative same-app paths, formed from server-issued proposal IDs; no untrusted Host reflection. First proposal creation is a 200 control result: the model sees only `{contractVersion:1,httpStatus:200,resultCode:"confirmation_required"}`, while the separate authenticated UI channel receives the immutable displayed tuple and relative confirmation target. A pending exact retry is 409 confirmation_required. The model never receives note, approval state as authority, or a control URL to execute.

| New same-app API route | Exact input / authority |
| --- | --- |
| `POST /api/v1/copilot/questions` | The Q&A body above; server owns per-turn case/note context and model admission. Successful response separates plain answer, validated citation data, UI-only records and optional confirmation control data; none is persisted as conversation history. |
| `GET /api/v1/cases/{caseId}/copilot/summary` | No query parameters; API-owned safe summary allowlist. |
| `GET /api/v1/cases/{caseId}/copilot/open-points` | No query parameters; API-owned domain/field matrix. |
| `POST /api/v1/cases/{caseId}/copilot/advance` | Trusted UI-only body `{expectedRevision:integer,releaseNote:string,proposalId?:string}`. The host invokes the same `advance_stage` MCP tool, with note as private authenticated dispatch context rather than tool/model arguments. This supports UI-assisted direct MCP creation and exact post-approval retry; it is not an additional business-write path. |
| `GET /api/v1/cases/{caseId}/copilot/proposals/{proposalId}` | UI-only exact immutable tuple, lifecycle state, canonical aircraft identity and relative control targets; current user/session/case binding required, no mutation on GET. |
| `POST /api/v1/cases/{caseId}/copilot/proposals/{proposalId}/decision` | Exactly `{decision:enum}` with `approve/decline/cancel`; current presenter/session/scope, valid Origin/Host/CSRF and state/expiry required. No model dispatch and no release. |

These inputs reject extra properties and use the same ID/revision/note validation already defined. UI-assisted advance and Q&A dispatch supply private request-scoped note context only to their actual same-user MCP invocation; no caller-provided provenance header creates that context. No shared mutable/global note/session slot is permitted. Context lasts only for the request/turn; after proposal creation the immutable stored tuple is the sole note authority. Direct raw MCP without trusted note context cannot propose a release. Existing manual release endpoint behavior stays available to its established authorized presenter UI; the copilot release branch adds approval/receipt enforcement without representing those additions as already delivered.

**Role × resource/action matrix (FR-002 through FR-006/FR-010).** “Scoped” means current `CaseScopes` membership and a server-authorized existing case. D denotes `403 forbidden`; A denotes `401 unauthenticated`. Before role detail checks, missing/out-of-scope case responses are identical bodyless 404. All denied attempts reach zero source ports; unauthorized scope checks use only session/authorization metadata, not unauthorized business case data.

| Role | Scoped list / safe summary | Open points | Source summary | technical records | maintenance records | finance records | records records | contracts records | Propose / approve / release |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| technical-reviewer | Structural summary | Technical only | Coarse all five; detail only entitled three | Allow | Allow | D | Allow | D | D / D / D |
| finance-reviewer | Structural summary | Finance only | Coarse all five; detail only entitled two | Allow | D | Allow | D | D | D / D / D |
| legal-reviewer | Structural summary | Legal only | Coarse all five; detail only entitled two | D | D | D | Allow | Allow | D / D / D |
| presenter | Coarse summary | Coarse readiness only; no point collection | Coarse all five only | D | D | D | D | D | Allow / Allow / Allow, subject to all confirmation/gate checks |
| executive | Coarse summary | Coarse readiness only; no point collection | Coarse all five only | D | D | D | D | D | D / D / D |
| unknown role | D | D | D | D | D | D | D | D | D / D / D |
| anonymous | A | A | A | A | A | A | A | A | A / A / A |

**Exact safe data fields (FR-003/FR-004/FR-005/FR-013).** Contract version is integer `1`. Projection payloads contain only the named fields; no incidental serialization of domain objects. Authorization occurs in the API before projection or source-edge work. Bounds are checked at runtime; invalid upstream values yield `502 copilot_context_invalid`, not arbitrary string forwarding.

| Projection | Exact fields and constraints |
| --- | --- |
| List | `{contractVersion, cases}`; cases contains at most the server's published scoped case set (current fixture IDs `cp-case-a`, `cp-case-b`, `cp-case-c`), each with the summary fields below. Empty scope returns `cases: []`. |
| Safe summary, all five allowed roles | `{contractVersion, caseId, aircraftId, caseRevision, status, ownerRole, nextActionId, capabilityLabel, released, releaseBlockers, simulated, synthetic}`. IDs match server-owned signed fixture/published identities; caseRevision is nonnegative Int64 below maximum. ownerRole is null or a known role. nextActionId is null or a registered role-owned action ID from the canonical API projection. released is the API's existing IsReleased Boolean; releaseBlockers is its ordered Blockers result using the three exact codes below, never a new readiness algorithm. Demo flags are literal true. No case name, action text, aircraft free-text display name, currentRunId, activityId, history, document list or note in model context. |
| Canonical `status` | Exactly `{assessment, review, request, finance, legal, acceptance}` from existing API projection, never a new stage ordinal. Values are null or: assessment `Baseline/Request/Review`; review `open/closed`; request `delivered`; finance `approve/reject`; legal `signed-off/rejected`; acceptance `accept/reject`. Unknown canonical tokens fail validation rather than imply readiness. `capabilityLabel` is `AI agent/Rules-based/Simulated/Design-only/Unknown`. |
| Open points, reviewer | `{contractVersion, caseId, caseRevision, domain, points}`; domain is exactly that role's `technical/finance/legal`. Maximum 100 points, ordered by pointId; overflow gives `422 copilot_point_limit`, no truncation/partial points. |
| Each reviewer point | `{pointId, domain, pointType, state, ownerRole, sourceRefs}`. pointId is a registered fixture requirement/reserve/obligation identity; pointType is `technical_requirement/reserve_review/obligation_review` matching domain; state is `open/blocked`; ownerRole matches requesting reviewer. sourceRefs is at most 100 distinct `{sourceId, recordId, recordVersion}` tuples pinned to current API evidence and that role's source entitlements. Versions are integers `1..2147483647`; no descriptions, amounts, contractual prose, finding quotes or instructions. |
| Open points, presenter/executive | `{contractVersion, caseId, caseRevision, status}` only. No domain, points, point IDs, counts, sourceRefs, finding/source prose or reviewer detail. |
| Source summary | Preserve existing `{contractVersion, fixtureVersion, caseId, caseRevision, sources}` and direct API behavior. All roles see each source's `{sourceId, mode, capabilityLabel, coverage}`; only entitled reviewers also see `recordCount`, `pinnedInBasis`, `recordsInUse`. Presenter/executive and non-entitled source entries omit those detail properties, not null-filled detail. `recordsInUse` contains only existing tuple fields. The model-safe projection validates fixtureVersion against the server's registered fixture version, exactly five unique canonical source IDs, mode `available/unavailable/stale/conflicting`, capabilityLabel `Simulated`, Boolean coverage/pinnedInBasis, recordCount integer `0..2147483647`, and at most 100 distinct recordsInUse tuples per source with registered IDs and version `1..2147483647`. Invalid model context fails closed without changing direct summary behavior. |
| Source record tool result to caller/UI | Preserve current API `contractVersion/caseId/sourceId/records` or `record` envelope and failures. Record bodies are untrusted UI data. The model-safe result is a separate projection of returned `{sourceId, recordId, recordVersion}` only, with caseId and the closed outcome code; it never contains payload, asOf strings, kind prose, document text or the raw envelope. |

API open-point membership is defined over the current accepted basis and current API-owned reviewer dispositions. Technical points are unresolved technical requirement identities whose current basis disposition is missing, needs-review or blocked, not satisfied identities. Finance points are reserve review identities lacking current approval; legal points are obligation review identities lacking current sign-off. The API maps pending/rejected review to `open` and a current evidence-unavailable/blocked disposition to `blocked`; accepted/signed-off current identities are excluded. Stale decisions do not close a current-basis point. No model-generated text is a point identity or disposition. No counterpart domain points are included, even if a caller can read a shared source. Missing or unregistered point identity/provenance fails the projection rather than synthesizing a finding.

`sourceRefs` filters non-entitled sources out, and absence is explicit `[]`; filtering never grants the other role's point. A reference in open-points indicates a potential navigation target, not evidence that a record has already been read. Citation authorization additionally requires FR-015's actual successful returned tuple.

**Complete model-input admission (FR-013).** Server-owned system/developer instructions are fixed for this workload; neither question nor API/source/model text can replace them. The original question is a USER message, never promoted to system/developer. Tool schemas are the fixed five-name set above. Every outbound follow-up is constructed from the listed structural projections or closed result codes, not blindly copied MCP content. Source IDs are exactly `technical/maintenance/finance/records/contracts`; roles are exactly `technical-reviewer/finance-reviewer/legal-reviewer/presenter/executive` (unknown/anonymous are never model-admitted); blockers are exactly `technical_review_not_accepted/reserve_not_approved/obligations_not_signed_off`, at most three, ordered by existing API policy. Fixture case/aircraft/point/action/record IDs must match server registries and requesting role/case; structural membership is not inferred from a `synthetic:true` marker.

Only validated five-tool call metadata from this one execution may be retained as conversational mechanics, within the same total token limit; it confers no authority and is never copied into instructions. Do not feed intermediate natural-language assistant text back into another model request. Tool observations are rebuilt as allowlisted structure. Tool-call correlation IDs accept only ASCII `[A-Za-z0-9_-]` of length 1..64; other model-call shapes return 502 model_response_invalid. A model attempt to add releaseNote/note, source payload, document/finding/legal prose or raw audit history cannot enter follow-up construction through metadata. Terminal assistant answer text is UI-only and never reused as input/history. Model-generated refusal/error content is not echoed as a diagnostic. Raw-source exclusion applies to all roles of every outbound message, every retry, every tool result and every schema. Independent question prose may itself contain sensitive or hostile text: notice and explicit send reduce that risk but structural validation does not recognize its semantics.

**Confirmation invariant (FR-008 through FR-011).** Canonical command identity is `(proposalId, authenticatedUserId, exactDemoSessionId, caseId, action=final-release, expectedBusinessRevision, normalizedDisplayedNote)`. The immutable stored proposal also has creator, createdAt, expiresAt and initial state; mutable lifecycle state is separate from that immutable value. Exact session binding may be persisted as a keyed nonreversible session reference but must compare the exact server session, not only user/role. No raw cookie credential is serialized.

Lifecycle states are `pending`, `approved`, `declined`, `cancelled`, `expired`, `consumed`. Expired is effective at `now >= createdAt + 5 minutes`; neither read nor approval resets time. Approve moves pending to approved only before expiry; repeat approve returns the same approval without extending time. Decline/cancel of pending or approved is terminal; repeat identical decline/cancel is a no-op with that state. Conflicting terminal control actions return `409 confirmation_state_conflict`. Unapproved/declined/cancelled/expired proposals cannot release.

Successful release is one serializable SQL transaction, or an equivalent single in-memory critical section shared with the store's business Commit. Inside that boundary the API revalidates current live session, presenter role, scope, immutable tuple, unexpired approval, expected revision, unreleased aircraft and all existing gates. Approval consumption, `aircraft.released`, revision increment, business audit and release receipt are all-or-nothing. A recheck before but outside Commit is insufficient.

Release receipt fields are exactly `{contractVersion:1, proposalId, caseId, decision:"released", revision, auditSequence, simulated:true}`. Store its immutable serialized success bytes and canonical-command fingerprint. Same-session exact replay returns original success bytes, even after original expiry or later business revision changes, without rechecking gates to create a second release; current authentication/role/scope and command identity still apply. Expiry exceptions apply only to an already successful exact replay, never a new release. Receipt support is an addition: the existing release endpoint does not already issue this receipt.

Failures preserve the proposal's prior approval state, receipt set, business revision, basis content/digests, current run/history and release audit. An admission/outcome invocation record is independent and can change on a failed or replayed invocation. Every successful replay receives a new invocation ID/audit, but never a second `aircraft.released`.

**Audit contract (FR-007/FR-017).** Append-only admit/outcome records are application persistence, not prompt telemetry. Each record contains exactly `invocationId` (server UUID), `phase` (`admit/outcome`), `at` (UTC instant), `operation` (the five tool names or `question/approve/decline/cancel/rejected-operation`), `provenance` (`copilot-client/mcp-direct/trusted-ui`), `callerRef` (opaque pseudonymous user reference or null), `sessionRef` (keyed nonreversible exact-session reference or null), `role` (five known roles or `unknown/anonymous`), `authorizedCaseRef` (canonical authorized case ID or null), `outcome` (`admitted/succeeded/denied/failed/cancelled`), `httpStatus` (null on admission, integer on outcome), and `resultCode` (null on admission, a closed catalogue code or `ok/confirmation_required/cancelled` on outcome). Unknown operation names become `rejected-operation`, never raw strings.

The server binds provenance to its actual authenticated dispatch path; a caller header/argument saying “copilot” is ignored/rejected. List/no-case/anonymous/out-of-scope/nonexistent attempts use `authorizedCaseRef:null` and never store the supplied unauthorized case ID. Authorized-case denials have the authorized reference and appear in case activity as **from copilot**, with operation/time/outcome only. Caller/security ledger is not exposed as a global case feed. Raw question, prompt, answer, source body, release note, raw audit history, cookie, CSRF, IP, hostname, user agent and identifying network values are excluded.

Durable admission must complete before source calls, model requests or business actions. If append fails, return `503 audit_unavailable` and perform zero downstream sensitive actions. Outcome append failure returns the same error and stops further dispatch; it cannot undo an already completed read/model call. For release, outcome persistence participates in the successful atomic transaction so failure rolls back business changes too. An unavailable append cannot be represented as recorded success. Operational failure diagnostics are separate from this ledger.

**Error catalogue (FR-018).** New API/control/Q&A envelopes use `{contractVersion:1,error:{code}}`, with only the named additional fields below. Tool execution failures are ordinary MCP results with `isError:true` and structured `{contractVersion:1,httpStatus,error:{code}}`; bodyless case 404 maps to fixed `case_not_found` tool failure indistinguishable across missing/out-of-scope. No raw upstream exception, unauthorized identifier, question, source body, note, credential or approval binding is in errors. Existing direct-route envelopes stay unchanged.

| Condition | HTTP status / exact new code or preserved outcome |
| --- | --- |
| Malformed JSON, duplicate/extra fields, wrong types, unknown argument, invalid enum/ID syntax, tool schema mismatch | `400 invalid_request` |
| Non-JSON request body | `415 unsupported_media_type` |
| Unsupported API contract version | `400 unsupported_contract_version` |
| Unsupported MCP protocol version | `400 unsupported_protocol_version` at transport boundary; malformed JSON-RPC is protocol error `-32700`, invalid RPC `-32600`, unknown RPC method `-32601`, invalid params `-32602` |
| Unknown model tool name | `400 unknown_tool`; zero MCP dispatch |
| Current session missing, expired or signed out | `401 unauthenticated` |
| Invalid/missing/null/untrusted browser Origin, invalid Host | `403 origin_forbidden` / `403 host_forbidden` |
| Missing/wrong CSRF on MCP POST, Q&A POST, proposal/control/retry mutations | `403 anti_forgery_failed` |
| Unknown role or denied role/source/action on an authorized case | `403 forbidden` |
| Missing/out-of-scope case at API | Bodyless `404`; fixed `404 case_not_found` only in tool/Q&A adaptation |
| No selected case for case-specific call | `409 case_selection_required` before case data dispatch; list remains allowed |
| Model case differs from trusted selected case, even another authorized case | `403 selected_case_mismatch` |
| No trusted UI release note / invalid supplied raw or trimmed note | `400 release_note_required` / `400 invalid_release_note` |
| Proposal missing or not bound to current user/session/execution | `404 confirmation_not_found` with no proposal disclosure |
| Exact authenticated proposal tuple altered | `409 confirmation_mismatch` |
| Pending retry / declined / cancelled / expired at or beyond exact expiry | `409 confirmation_required` / `409 confirmation_declined` / `409 confirmation_cancelled` / `410 confirmation_expired` |
| Conflicting lifecycle operation | `409 confirmation_state_conflict` |
| Stale expected business revision / already released by a different proposal | `409 stale_revision` / `409 already_released` |
| Release gates block | `409 release_blocked`; add `blockers` array preserving exact API order and codes `technical_review_not_accepted`, `reserve_not_approved`, `obligations_not_signed_off` |
| Same receipt/proposal replay with different command fingerprint | `409 replay_mismatch` |
| Source admitted read 21 in rolling window or third in flight | Preserved `429 source_read_limit` |
| Source list 101 records, record deadline 1 second | Preserved `422 source_record_limit`, `504 source_read_timeout` |
| Source case mismatch / missing record / unavailable | Preserved `502 source_case_mismatch`, `404 record_not_found`, `503 source_unavailable` |
| Source summary overflow or failure | Preserve `422 source_payload_limit`, `503 source_read_failed` or other current closed source error, and `504 source_read_timeout` |
| Record API query parameters | Preserve `400 invalid_request`; source not in entitlement matrix is `403 forbidden`, not a source-port probe |
| Point collection above 100 / structural context invalid | `422 copilot_point_limit` / `502 copilot_context_invalid` |
| Empty/whitespace question, question over 2000 Unicode scalar values, invalid scalar sequence | `400 invalid_question` |
| Copilot input contract absent/not approved, provider manifest refusal | `503 copilot_input_not_approved` / `503 model_manifest_refused`; zero provider calls |
| Transient model unavailable after allowed retries / invalid model response | `503 model_unavailable` / `502 model_response_invalid` |
| Tool/model attempt or retry quota exhausted | `429 copilot_tool_limit`, `429 copilot_model_limit`, `429 copilot_retry_limit` |
| Estimated/observed input above 16000 or output above 2000 tokens | `422 copilot_input_token_limit` / `422 copilot_output_token_limit`; no partial answer |
| Processing reaches 30 seconds | `504 copilot_timeout`; cancel sensitive work, no partial answer |
| Invalid/fabricated citation or navigation | `502 citation_invalid`; no actionable link and no arbitrary network fetch |
| Invocation admission/outcome or release audit persistence unavailable | `503 audit_unavailable`; no claim that an unpersisted record succeeded |
| Other injected release-store failure | `503 release_unavailable`; no partial consumption/change |
| Client disconnect | Stop execution and release leases; append `cancelled` outcome when storage is available; no invented response to a disconnected client |

For overlapping errors, authenticate and validate Origin/Host/CSRF first, then strict shape and selected-case binding, then API scope and role, then command/confirmation state, then dispatch. Audit admission/outcome surrounds even early denials with no sensitive dispatch; if storage is unavailable, the fail-closed 503 supersedes a would-be denial. Authorized case determination cannot read an unauthorized case merely to choose an audit destination.

---

## Non-Functional Requirements

### Performance and Capacity

**NFR-001 (MUST):** Preserve SRC:DD-024 record-read limits: 20 **admitted** reads per authenticated session+case over rolling 60 seconds, at most two in flight per case across callers, no pagination/query parameters, maximum 100 records with 101 detecting overflow, one-second request deadline. Denied scope/role/schema requests consume no record admission; admitted successes, failures, timeouts and cancellations still count in the window. Direct and tool calls share the exact existing session key/store. No retry/cache resets the budget. Covers FR-006; AC-008, AC-009, AC-040.

**NFR-002 (MUST):** Each question gets existing per-execution defaults: 20 tool attempts (including rejected model tool attempts), eight model attempts including retries, 30 seconds processing, 16000 estimated input tokens per model call including every message and tool schema, 2000 output tokens, at most two retries per failed model operation with 500ms then 1000ms backoff. Exactly 20/8/16000/2000 are within limits; 21/9/16001/2001 fail. Observed usage exceeding limits also terminates without a partial answer. Human waiting ends the turn and consumes no continuing model/time budget; confirmation retry is a new tool invocation without another model call. Covers FR-012/FR-016/FR-018; AC-032, AC-036.

---

### Reliability and Resilience

**NFR-003 (MUST):** Admission is fail-closed; all release persistence is atomic with revalidation and rollback. No partially consumed approval, receipt, audit or revision after injected failure; exact replay returns original bytes. SQL uses the existing serializable-store boundary; in-memory uses a lock shared with Commit, not a separate proposal lock. Covers FR-007 through FR-011; AC-011, AC-018 through AC-023, AC-041.

**NFR-004 (MUST):** Source timeout, cancellation and failure release in-flight leases without removing admitted-window entries. No background source/model retry loop continues after cancellation/time exhaustion. Covers FR-006/FR-018; AC-009, AC-039.

---

### Security

**NFR-005 (MUST):** Current `__Host-demo-session` stays HttpOnly, Secure, SameSite Strict, path `/`; resolve it from DemoSessionStore every request, never from MCP transport identity or cached user data. Same-origin browser requests require exact server-owned origin/host allowlists and the existing session CSRF token for every POST. Non-browser local dispatch uses fixed same-app endpoint context and current per-request credentials; it does not invent a privileged exemption. No permissive CORS, shared CookieContainer, reflected host URLs, model-supplied backend URL or model-visible credential. Covers FR-002/FR-014; AC-002, AC-003, AC-028.

**NFR-006 (MUST):** API-owned case/role/source checks and host selected-case/argument checks produce zero unauthorized data-port calls and zero unapproved business effects. Tool annotations and model instructions are informational, never enforcement. Immutable stored command objects use a defensive immutable copy; current authority and gate checks are inside the business commit boundary. Covers FR-005/FR-008/FR-010/FR-014; AC-007, AC-013, AC-019, AC-042.

---

### Privacy

**NFR-007 (MUST):** Only synthetic fixture context is admitted. A question is a 1..2000 Unicode-scalar free-text USER message, explicitly sent after a notice: “Synthetic demo only. Do not enter personal, confidential, live aircraft or customer data. Your question is sent to the governed model.” No semantic keyword classifier or claim to recognize all personal data. No persisted prompt, answer or multi-turn history in application storage, browser storage, invocation ledger, traces, diagnostics or exception messages. Working buffers end at turn completion/cancellation; the visible single answer/record pane clears on new question, case change, user switch or sign-out. Existing provider-side retention is not changed or promised absent by this PRD. Covers FR-013/FR-016/FR-017; AC-026, AC-031, AC-035.

---

### Scalability and Elasticity

**NFR-008 (MUST):** Concurrent direct/MCP clients share the repository-supported admission/transaction stores in both SQL and in-memory operating modes. Tests must hold two in-flight reads and race two exact releases, proving global per-case admission and single release independently of transport connections. This release has no autoscaling or aggregate throughput SLO. Covers FR-006/FR-010/FR-011; AC-009, AC-020, AC-022.

---

### Maintainability and Operability

**NFR-009 (MUST):** Every new runtime contract is exercised through actual production startup, not a test-local recreation. Fake clock, fake model HTTP/provider edge, counting source edges and fault-injected repository-supported stores are allowed; unit tests must not call clouds, browsers, publish, npm or Docker. Later CI restore/build proves external SDK availability. Preserve existing investigator/source contract tests and fixtures; new Q&A limits are execution-local, not global investigator configuration edits. Covers FR-001/FR-018; AC-037, AC-038, AC-039.

---

### Observability

**NFR-010 (MUST):** Application invocation audit is unsampled durable append-only persistence with the exact FR-007 schema, not a business Commit on reads and not optional diagnostic logging. Operational diagnostics report storage failure independently with OTel `ERROR` severity, fixed body, timestamp and trace/span correlation when available; they must not assert that unavailable audit succeeded. Covers FR-007/FR-017; AC-010, AC-011, AC-034, AC-041.

**NFR-011 (MUST):** Follow Telemetry Foundations for trace/metric/log terminology: resource attributes `service.name`, `service.version`, `deployment.environment`, `telemetry.sdk.name`, `telemetry.sdk.language`, `telemetry.sdk.version`; existing HTTP semantic conventions and `http.server.request.duration` in seconds. No prompt/source/note/credential/network-identifying data, raw user/session/case/record/proposal IDs or arbitrary URLs as attributes or metric dimensions. Use existing bounded HTTP route/status dimensions only, not raw paths. Custom copilot attribute names are not approved here: any addition must follow the skill's proposed-additions intake in a separately human-reviewed change; until then it stays suppressed. Durable ledger field names are application data fields, not new OTel conventions. Covers FR-007/FR-012/FR-017; AC-024, AC-035.

---

### Usability and Accessibility

**NFR-012 (MUST):** The single-question, source pane and confirmation UI have keyboard-operable controls, programmatic labels, visible focus, announced busy/error/result states and no automatic submission on navigation. Approval, decline and cancel are distinct controls. Model answer is plain escaped text, source prose is escaped untrusted data, and validated navigation is independent of prose. WCAG 2.2 AA is the UI review target; no browser pixel/contrast automation is required or represented as completed. Covers FR-009/FR-015/FR-016; AC-014, AC-030, AC-033.

---

### Compatibility and Interoperability

**NFR-013 (MUST):** Pin ModelContextProtocol.AspNetCore **2.3.0** in the allowed backend csproj during eventual implementation and use its versioned SessionMode APIs, not old search summaries or obsolete `Stateless` property examples. Actual production-host wire tests verify protocol 2025-11-25 initialize, tools/list, tools/call and errors without requiring native elicitation, unsolicited notifications or model streaming. Other protocol versions are outside accepted scope. Covers FR-001; AC-001, AC-038.

---

### Portability

**NFR-014 (MUST):** The feature runs within existing same-origin demo hosting and repository-supported SQL/in-memory stores; no new containers, external MCP host, tenant resource, credential, APIM route or infrastructure change is required. Switching store mode must preserve confirmation and invocation invariants. Covers FR-007/FR-010/FR-018; AC-018, AC-037, AC-039.

---

## Constraints

| ID | Constraint | Imposing source / category | Affected boundary, non-negotiability and impact |
| --- | --- | --- | --- |
| CON-001 | Exactly five model tools and one final simulated business write; no reviewer decisions, gate navigation, reset, upload, source-mode change, notice, lessee contact or other business write. | D2; ARR authority boundary / technical | Product/write scope is fixed by user decision; FR-001/FR-010/FR-014 and AC-001/AC-017/AC-042 enforce it. |
| CON-002 | OAuth OBO, MCP APIM routing, full chat/persisted multi-turn history, live data and cloud changes are out of scope and forbidden in this release, not prerequisites. | D1/D5/D8; ARR synthetic scope / technical, operational | Same-origin demo boundary; FR-012/FR-016/FR-018, AC-031/AC-037. Existing model APIM route is unchanged. |
| CON-003 | SRC:DD-023 and SRC:DD-024 are canonical source entitlements and limits; current delivered failure/envelope behavior wins over older narrative. | SRC version 0.9.1 and research C3/C9 / technical | No visibility widening, budget reset or direct-port shortcut; FR-006, AC-008/AC-009/AC-040. |
| CON-004 | Safe role projections, invocation append, proposal/approval and atomic receipt support are API prerequisites inside this feature; MCP contains no new business policy. | F2/F6/F7 and D7 / technical | Existing raw API and release receipt gaps cannot be assumed delivered; FR-004/FR-005/FR-007 through FR-011. |
| CON-005 | wesback's human merge of this PRD is the explicit new copilot input-policy approval; the investigator exact-hash gate and approved provider-manifest boundary are not relaxed. | D9; ARR:DD-025/DD-034; research C10/C11/C14 / organizational, technical | No implementation before merge; FR-012/FR-013, AC-025. No additional named approval is invented. |
| CON-006 | Raw source text is UI-only and absent from every model message; no raw question/answer/note/source/credential diagnostic persistence. | D9 / technical, privacy | Separately project tool results; prompts cannot embed sources; FR-013/FR-015/FR-016, AC-026/AC-027/AC-035. |
| CON-007 | Approval expiry is five minutes from creation, exact expiry rejects, and successful release/consumption/receipt/business audit are atomic; replay cannot release twice. | D7 / technical | Required API transaction/lock and runtime failure/race verification; FR-008 through FR-011, AC-013/AC-015/AC-018 through AC-023. |
| CON-008 | Draft authoring writes only this PRD. Manual HVE RPI owns later planning/security/quality review and implementation, not automatic pipeline story handoff. No story/issue chains, publication, merge or labels now; later work slices stay at most 500 changed lines including tests. | Updated user delivery direction and AGENTS.md human gate / organizational | Human PRD-merge approval remains mandatory. Parent resolves the feature-specific non-pickup route; no skip mechanism is asserted. Draft is not delivery or approval; DD-012 and AC-037. |

---

## Process Models

`diagram_format: none`: explicit flows below avoid duplicating the normative state and transaction tables.

1. **Question/read flow (FR-002/FR-007/FR-013/FR-014):** user explicitly sends one question with a trusted selected case → current session/Origin/Host/CSRF and durable invocation admission → structural-context admission → governed model → deterministic tool/schema/case validation → actual per-turn same-user MCP transport → authorized API projection/source path → separate model-safe tuples and escaped UI-only record data → escaped answer and independently validated citations → append outcome and discard turn buffers.
2. **No-case flow (FR-003/FR-014):** list remains scoped → UI user explicitly selects a returned case → only then permit case-specific calls. A model cannot implicitly select or jump cases.
3. **Release flow (FR-008 through FR-011):** trusted note + first tool call → immutable pending proposal → model processing ends → trusted UI shows exact tuple and expiry → approve/decline/cancel → exact MCP retry → atomic API revalidation/consumption/release/receipt/business audit/outcome → original receipt. Approved navigation alone never releases.
4. **Denied/failure flow (FR-007/FR-017):** persist admission → deny before data ports → append exact outcome to authorized-case activity or caller/security-only ledger. Admission failure stops before sensitive dispatch; separate diagnostics report the outage honestly.

---

## Acceptance Criteria

All 42 criteria have **Status: Not Started**. Test names below are required future runtime cases, not claims that tests or code exist. `ProductionHost` means the app's real startup/registrations with fake model/source/clock edges only; in-memory and supported SQL store semantics are both required for persistence criteria. Each negative test changes only the condition under test from an otherwise valid request.

| ID | Given / When / Then and named verification | Covers |
| --- | --- | --- |
| AC-001 | Given production startup with pinned SDK, when protocol 2025-11-25 initializes and lists/calls tools, then exactly the five names/schemas above are returned, SessionMode is explicitly stateless and no transport ID confers identity. Test `ProductionHost_Mcp20251125_ExactlyFiveTools`; unsupported version returns 400 unsupported_protocol_version; unknown RPC method is -32601. | FR-001 |
| AC-002 | Given valid requests on one transport, when switching user, signing out, expiring session or replaying old MCP IDs, then every later request resolves current session and returns only new user's scope or 401 unauthenticated, never cached identity. Test `ProductionHost_McpIdentity_CurrentSessionAfterSwitchAndSignOut`; source/model/business spies assert zero unauthorized calls. | FR-002 |
| AC-003 | Given otherwise valid same-origin MCP/Q&A/proposal/approval/retry requests, when Origin is missing, null or foreign, Host is untrusted, or CSRF is missing/wrong, then exact catalogue 403 applies with zero sensitive dispatch. Test `ProductionHost_OriginHostCsrf_ContractTable`; valid fixed endpoint/per-request credentials pass, response URLs never reflect Host, shared CookieContainer is not used. | FR-002 |
| AC-004 | Given each allowed role's real session with a canonical scoped fixture set, when listing cases, then exact allowed summary fields, current API statuses and role nextActionId are returned; empty scope is []. Test `ProductionHost_ListMyCases_RoleScopedFields`; unknown role is 403 forbidden and anonymous is 401 unauthenticated with no data-port call. | FR-003 |
| AC-005 | Given selected current cases in each role, when summary is read, then only FR-004's field allowlist is serialized and status/action IDs and released/blocker values equal canonical API values/policy, not a new stage algorithm. Test `ProductionHost_CaseSummary_CanonicalProjection`; invalid upstream enum gives 502 copilot_context_invalid; raw scene/case/finance fields are absent. | FR-004 |
| AC-006 | Given technical, finance and legal current-basis pending/rejected/blocked and accepted dispositions, when open points are requested, then only that role's exact domain/point fields and entitled references appear; accepted current points are absent and stale decisions do not close current points. Test `ProductionHost_OpenPoints_RoleDomainAndCurrentBasis`; no point descriptions or unrelated domain identities appear. | FR-005 |
| AC-007 | Given presenter/executive/unknown/anonymous and unauthorized/missing cases, when reading points, then presenter/executive get only status envelope, unknown gets 403, anonymous gets 401 and missing/out-of-scope are identical bodyless 404 at API. Test `ProductionHost_OpenPoints_DenialNoPorts`; counting source edges remain zero on every denial; 100 points pass and 101 gives 422 copilot_point_limit without partial data. | FR-005 |
| AC-008 | Given all role × five-source combinations, when direct and MCP records/summary are requested, then compare exactly against SRC:DD-023 matrix: technical three, finance two, legal two; presenter/executive summary-only; unknown denied. Test `ProductionHost_SourceEntitlements_CanonicalSrcDD023`; disallowed records give 403 forbidden with zero port calls, out-of-scope/missing bodyless 404, summary detail omissions match current API. | FR-006 |
| AC-009 | Given counting source edges and shared production admission, when mixing 10 direct and 10 MCP reads at t0, then read 21 before t0+60s returns 429 source_read_limit and at t0+60s expired admissions no longer block. Test `ProductionHost_MixedSourceReads_RollingBoundary`; `ProductionHost_SourceReads_TwoInFlightAcrossSessions` holds two calls per case and rejects third. A different session has its own rolling count but not a third per-case lease. | FR-006 |
| AC-010 | Given authorized-case, authorized-role-denied, list/no-case, anonymous, out-of-scope and missing attempts, when invoked, then exactly correlated append-only admit/outcome rows use FR-007 fields and correct destination, without unauthorized case identifier. Test `ProductionHost_InvocationLedger_DestinationAndFields`; read audit never calls business Commit or increments revision. Spoofed provenance header cannot mark direct dispatch as copilot-client. | FR-007 |
| AC-011 | Given unavailable invocation admission storage and valid source/model/release commands, when attempted, then 503 audit_unavailable, zero source/model/business actions and no invented durable success occur. Test `ProductionHost_AuditAdmissionFailure_ZeroDownstream`; separate operational diagnostic is emitted without prompt/identifier data. | FR-007 |
| AC-012 | Given scoped presenter, revision and trusted raw UI note, when first advance_stage runs, then it creates one immutable pending tuple with normalized displayed note and expiresAt=createdAt+5min, no release; UI reads exact aircraft/action/revision/note/expiry/simulation warning. Test `ProductionHost_ReleaseProposal_ExactCanonicalTuple`; null/empty/whitespace, raw 2001-unit note or model note property fails without proposal; valid raw 2000 with nonempty trim passes. | FR-008 |
| AC-013 | Given stored proposal and retained original mutable input references, when those references or a returned projection are mutated, then stored tuple/fingerprint/creation/expiry remain unchanged and changed command retry returns 409 confirmation_mismatch. Test `ProductionHost_ReleaseProposal_MutationAfterStorage`; each tuple component is varied separately. | FR-008 |
| AC-014 | Given valid pending proposal, when trusted UI explicitly approves/declines/cancels, then only corresponding authenticated POST changes lifecycle, and no release occurs. Test `ProductionHost_Confirmation_TrustedExplicitActions`; GET/navigation, model Boolean, elicitation accept, missing CSRF, executive approval or guessed proposal fail with exact catalogue results. | FR-009 |
| AC-015 | Given fake creation clock, when approving/retrying at creation+4m59.999s, exactly +5m, or later, then only first is eligible; exact expiry is 410 confirmation_expired. Test `ProductionHost_Confirmation_ExpiryBoundaryApprovalDoesNotExtend`; repeat approve keeps original expiry, declined/cancelled states reject and conflicting terminal control is 409 confirmation_state_conflict. | FR-009 |
| AC-016 | Given same presenter/session approved tuple, when exact MCP retry is made before expiry, then release is requested through API-owned release path; pending retry is 409 confirmation_required, declined/cancelled exact errors, other live user/session is 404 confirmation_not_found, expired session is 401. Test `ProductionHost_AdvanceStage_ApprovalBinding`; approval link opening alone changes no business state. | FR-010 |
| AC-017 | Given otherwise release-ready states, when presenter exact retries exercise each gate/revision/released condition, then preserve API release rules and blockers in order. Test `ProductionHost_AdvanceStage_ExistingReleasePolicy`: stale revision →409 stale_revision; other proposal's release →409 already_released; technical not accepted →409 release_blocked with technical_review_not_accepted; reserve not approved →reserve_not_approved; legal not signed off →obligations_not_signed_off; all blockers returns all three. No reviewer decision/UI gate write/lessee contact occurs. | FR-010 |
| AC-018 | Given approved proposal, when a successful atomic release commits, then approval consumed, revision +1, exactly one aircraft.released audit, receipt and successful invocation outcome persist together; basis/run/history contents remain unchanged except the intended release audit/revision. Test `ProductionHost_ReleaseAtomicCommit_AllStoredInvariants`, through supported SQL serializable store and shared-lock in-memory store. | FR-010 |
| AC-019 | Given checks initially pass, when a barrier fake changes business revision/gates or revokes current session/scope between initial read and commit, then inside-commit revalidation rejects without release, consumption or receipt. Test `ProductionHost_ReleaseRace_ReadCheckVersusCommit`; every raced invalid condition has its precise stale/auth/scope error rather than a wrong-role setup. | FR-010 |
| AC-020 | Given approved tuple and two parallel exact retries at a barrier, when both execute through the host, then only one Commit/revision increment/business audit/receipt occurs and both return identical original receipt bytes with independent invocation IDs. Test `ProductionHost_ReleaseParallelRetry_OneBusinessChange`. | FR-010 |
| AC-021 | Given approved tuple and valid gates, when fault injection fails Commit, receipt write, business-audit write or transactional invocation outcome, then approval remains approved and prior receipt set/revision/basis digests/run/history/release audit remain byte-for-byte unchanged. Test `ProductionHost_ReleaseAtomicFailure_CommitReceiptAuditRollback`; audit fault is 503 audit_unavailable, other store fault 503 release_unavailable. | FR-010 |
| AC-022 | Given successful original receipt, when exact same user/session command retries after expiry and after unrelated revision change, then original serialized receipt is returned with zero second Commit/release/business audit; a new invocation audit is appended. Test `ProductionHost_ReleaseExactReplay_OriginalReceiptBytes`. | FR-011 |
| AC-023 | Given successful receipt, when a replay changes expected revision, note binding, case/action or fingerprint, then 409 replay_mismatch without change; another session receives nondisclosing 404 confirmation_not_found. Test `ProductionHost_ReleaseReplayMismatch_NoEffects`; exact replay is the separate positive control. | FR-011 |
| AC-024 | Given production provider registration and counting credential/HTTP fakes, when client resolves before send then sends an admitted question, then credentials remain lazy and only the existing governed manifest/gateway boundary is used with existing correlation headers. Test `ProductionHost_CopilotProvider_LazyGovernedOnly`; invalid manifest gives 503 model_manifest_refused and zero provider/fallback calls. | FR-012 |
| AC-025 | Given the existing investigator registry and separate copilot admission, when registered synthetic evidence, empty evidence, changed evidence and generic question/JSON are tested, then the original exact-hash gate's accept/refuse behavior is unchanged and copilot cannot treat generic JSON as investigator evidence. Test `ProductionHost_InputAdmission_SeparateFromInvestigatorGate`; absent merge-approved copilot contract is 503 copilot_input_not_approved with zero model calls. Human approval itself is verified by review of wesback's merge record, not by a runtime model. | FR-012, FR-013 |
| AC-026 | Given fake provider captures every outbound request, when a 1-scalar/2000-scalar question, later tool results and model retries execute, then question is USER-only and each structural field passes membership/type/bounds; 2001, empty/whitespace/invalid Unicode reject 400 invalid_question before provider. Test `ProductionHost_CopilotMessages_QuestionAndStructuralAllowlist`; unregistered ID/free-text finding/legal text/note/raw history/source string/synthetic=true-only object yields 502 copilot_context_invalid, never generic JSON forwarding. | FR-013 |
| AC-027 | Given returned source body containing simulated delimiter/instruction/script text, when read_source and subsequent model/tool/retry calls execute, then every captured outbound model message/schema/result is free of raw source text and payload properties; separate UI pane alone receives escaped records. Test `ProductionHost_CopilotMessages_RawSourcesAbsentEveryRequest`; counting effects prove the body induces zero unintended writes/network/file execution. | FR-013, FR-014 |
| AC-028 | Given hostile fake model, when it emits unknown tool, extra role/user/baseUrl/confirmed=true, wrong types, duplicate keys, cross-selected-case ID, guessed proposal or no selected case, then exact unknown_tool/invalid_request/selected_case_mismatch/confirmation_not_found/case_selection_required results apply. Test `ProductionHost_HostileModel_ToolSchemaSelectedCaseApproval`; client and UI-assisted advance calls actually traverse MCP with current per-turn same-user credentials and private request-scoped note context; no source-port/business shortcut or shared note/session slot. Tool-call ID lengths 1/64 with allowed characters pass; 0/65 or other characters yield 502 model_response_invalid. | FR-014 |
| AC-029 | Given a successfully returned authorized source/record/version tuple, when model suggests that tuple, then the server can create a trusted relative navigation target; unreturned version/record/case or fabricated URL/citation yields 502 citation_invalid, no link/fetch. Test `ProductionHost_Citations_ActuallyReturnedTuples`; cross-user cached citation and point-ref-only citation also fail. | FR-015 |
| AC-030 | Given model output containing HTML, Markdown link syntax or script and raw record markup, when rendering, then answer and record pane display escaped text with no HTML/link execution; validated citation/confirmation controls are separate server data. Test `CopilotSurface_EscapedAnswerRecordAndIndependentNavigation`, using repository-supported component tests and production API output. | FR-015, FR-016 |
| AC-031 | Given single-question UI, when explicit send occurs then the notice is shown and only one question/answer is retained transiently; new question/case/user/sign-out clears buffers and pane. Test `CopilotSurface_SingleQuestionNoticeNoPersistentHistory`; server/browser-storage spies record zero prompt/answer/history writes, no automatic send on navigation. | FR-016 |
| AC-032 | Given model requests proposal, when host returns confirmation_required state/proposal control data, then model turn ends before human wait; UI approval followed by exact tool retry uses zero additional model calls and no continuous timer. Test `ProductionHost_ConfirmationWait_EndsModelTurn`; waiting 5 minutes cannot extend proposal or silently continue execution. | FR-016 |
| AC-033 | Given Q&A, record and confirmation surfaces, when keyboard/component accessibility checks run, then labelled controls, visible focus contract, announced busy/error/result state and explicit approve/decline/cancel controls are present. Test `CopilotSurface_KeyboardLabelsAndLiveState`; WCAG 2.2 AA visual/assistive-use review is separately required and not claimed executed. | FR-016 |
| AC-034 | Given authorized-case copilot success/denial/read/replay, when activity is projected, then it displays fixed “from copilot” provenance, operation/time/outcome while original business revision/history/current run/basis pins and digests are unchanged by read auditing. Test `ProductionHost_CopilotActivity_IndependentBusinessHistory`; unauthorized/no-case attempts never append to a case or disclose its existence. | FR-017 |
| AC-035 | Given captured ledger/log/trace/metric and browser/server storage sinks, when user question, release note, source text, cookie/CSRF and identifying network values are injected, then none persists in invocation diagnostics; ledger field allowlist and OTel suppression rules hold. Test `ProductionHost_CopilotPrivacy_NoContentCredentialTelemetry`; no unapproved custom metric dimensions or raw IDs are emitted. | FR-017 |
| AC-036 | Given counting model fake, fake clock/delay and production limits, when boundary values are exercised, then 20/8 attempts, 16000 input and 2000 output are permitted; 21/9/16001/2001 give catalogue exact limits; retries wait 500ms/1000ms, third retry gives copilot_retry_limit, exactly 30s gives copilot_timeout. Test `ProductionHost_CopilotLimits_ExactBoundaries`; rejected tool calls and model retry attempts count, no partial answer or infinite loop. | FR-018 |
| AC-037 | Given feature prerequisites and exclusions, when author/delivery boundary is reviewed, then only same-origin app capabilities are required, #363 remains closed evidence, no cloud/OAuth/MCP APIM/full-chat/other-write work is introduced and existing model route stays unchanged. Verified by review `DraftAndReleaseScope_OnlyApprovedFeature`; human PRD-merge gate remains mandatory, later HVE RPI work is manual, parent must resolve non-pickup approval route before proceeding, and no pipeline skip mechanism or story plan is claimed. | FR-018 |
| AC-038 | Given implementation CI restores/builds pinned package and production tests exercise catalogue table, when SDK or contract is wrong, then restore/build or exact runtime errors fail verification rather than assuming registry availability. Test `ProductionHost_CopilotErrors_ExactCatalogue`; CI backend restore/build is the external-availability verifier, using research W2/W8/W10 as dated evidence, not a new live claim. | FR-018 |
| AC-039 | Given existing source/investigator suites and production fake edges, when new Q&A failures/cancellation/limits run, then existing suites' defaults and outcomes remain unchanged; cancellation/timeout/unavailable/overflow release source leases and retain admitted-window counts. Test `ProductionHost_SourceFailuresCancellation_LeasesAndWindow`; 100 records succeeds, 101 →422 source_record_limit without partial records, exactly 1s →504 source_read_timeout, case mismatch/missing/unavailable preserve envelopes. | FR-018 |
| AC-040 | Given canonical fixtures, source matrix, direct source errors and role summary output, when tool/direct contracts compare, then canonical case/source/record/version/status identifiers are equal and source error codes/envelopes are preserved in tool adaptation without widening data. Test `ProductionHost_McpDirectContracts_CanonicalIdentityAndErrors`; query parameters fail, unauthenticated/out-of-scope/unknown roles reach zero source edges. | FR-001, FR-006 |
| AC-041 | Given durable admission followed by outcome storage failure, when read/model completes then further dispatch stops with 503 audit_unavailable and diagnostics explicitly distinguish unavailable outcome from recorded admission; no successful outcome is fabricated. Test `ProductionHost_AuditOutcomeFailure_HonestStop`; for release, same fault rolls back consumption/receipt/business audit/revision atomically. | FR-007, FR-018 |
| AC-042 | Given one hostile fake-model production-host suite, when unknown tool, cross-case IDs, fake confirmed=true, guessed proposal, fabricated URL/citation and source-delimiter/instruction fixtures are executed, then deterministic host checks yield **zero unintended effects** and no raw source appears in any outbound model request. Test `ProductionHost_HostileModel_ZeroUnintendedEffects`; a valid entitled read and approved exact release are positive controls, and results are explicitly not live accuracy or universal immunity measurements. | FR-014 |

---

## Traceability Matrix

### Feasibility Candidate Disposition

Not applicable: no feasibility-to-PRD or BRD handoff was supplied. Completed research findings are dispositioned above; they are not unaccepted feasibility candidates or direct downstream stories.

### FR-to-AC Coverage

| FR | AC links |
| --- | --- |
| FR-001 | AC-001, AC-040 |
| FR-002 | AC-002, AC-003 |
| FR-003 | AC-004 |
| FR-004 | AC-005 |
| FR-005 | AC-006, AC-007 |
| FR-006 | AC-008, AC-009, AC-040 |
| FR-007 | AC-010, AC-011, AC-041 |
| FR-008 | AC-012, AC-013 |
| FR-009 | AC-014, AC-015 |
| FR-010 | AC-016, AC-017, AC-018, AC-019, AC-020, AC-021 |
| FR-011 | AC-022, AC-023 |
| FR-012 | AC-024, AC-025 |
| FR-013 | AC-025, AC-026, AC-027 |
| FR-014 | AC-027, AC-028, AC-042 |
| FR-015 | AC-029, AC-030 |
| FR-016 | AC-030, AC-031, AC-032, AC-033 |
| FR-017 | AC-034, AC-035 |
| FR-018 | AC-036, AC-037, AC-038, AC-039, AC-041 |

Coverage formula: `(FR rows with at least one AC / FR rows) * 100` = `18/18 * 100` = **100.0%**, exceeding the 80.0% threshold. This is authored linkage coverage, not 100% test pass rate.

### FR-to-GOAL Alignment

| FR | GOAL links |
| --- | --- |
| FR-001 | GOAL-001 |
| FR-002 | GOAL-001 |
| FR-003 | GOAL-001 |
| FR-004 | GOAL-001 |
| FR-005 | GOAL-001 |
| FR-006 | GOAL-001 |
| FR-007 | GOAL-004 |
| FR-008 | GOAL-002 |
| FR-009 | GOAL-002 |
| FR-010 | GOAL-002 |
| FR-011 | GOAL-002 |
| FR-012 | GOAL-003 |
| FR-013 | GOAL-003 |
| FR-014 | GOAL-003 |
| FR-015 | GOAL-003 |
| FR-016 | GOAL-003 |
| FR-017 | GOAL-004 |
| FR-018 | GOAL-004 |

Coverage formula: `(FR rows with at least one GOAL / FR rows) * 100` = `18/18 * 100` = **100.0%**, meeting the 100.0% target. No waiver is requested. NFR and CON acceptance links are recorded with their statements; DDs identify scope authority and affected contracts.

---

## MVP and Release Framing

The first release includes **FR-001 through FR-018**, **NFR-001 through NFR-014**, and all four goals. The minimum feature is not MCP hosting alone: API projections, durable audit admission, atomic confirmation/receipt, bounded governed model client, one-question UI and UI-only source isolation are required together. No listed requirement is an optional backlog wish.

Prerequisite additions are in-feature API work, not evidence that existing contracts have already been implemented. Safe summary/open-points must precede their consumer dispatch; invocation admission must exist before sensitive dispatch; confirmation and atomic receipt persistence must exist before final release via MCP; input-policy approval must precede the new model workload. These are product dependencies, not issue chains.

Intended delivery is **manual HVE RPI planning and implementation here**, as directed by the user after initial draft creation, not automatic post-merge story handoff. The repository's human PRD-merge approval gate is preserved: wesback must approve the PRD and its input-policy contract before implementation. The pipeline's [documented per-PRD hold](https://github.com/wesback/engineeringpipelinev3/blob/main/docs/manual.md), implemented in [scripts/pause.mjs](https://github.com/wesback/engineeringpipelinev3/blob/main/scripts/pause.mjs), provides the feature-specific non-pickup route. The user confirmed in this manual RPI session that the actual dispatcher reports `docs/prd/role-aware-case-copilot.md` held. The parent has not independently queried that host; a local hold on a different machine would be insufficient. Human PRD approval remains pending, and the hold is not an exemption from that approval.

After that gate is resolved and approval recorded, manual HVE RPI owns planning, security/quality review and implementation, with work slices at most 500 changed lines including tests and all runtime/CI/human UI acceptance obligations intact. This paragraph assigns ownership, not a plan or activation of RPI. No publish, merge, PR, issue, label, live provider validation or cloud operation is performed by draft authoring.

Out of scope: OAuth/OBO/JWT user authentication, generic external connector authorization, MCP APIM routing, gateway/tenant/infrastructure changes, new credentials/providers/direct fallback, full chat or persisted history, raw-source model ingestion, live/real aircraft data, reviewer decisions, intake/investigation commands, UI gate navigation, reset/upload/source-mode writes, notices/lessee contact and every business write except final simulated release.

---

## Success Metrics

These are acceptance measurements, not proposed custom telemetry names or claims about user adoption/model accuracy.

| Measurement | Baseline | Target | Window / data source | Goal |
| --- | --- | --- | --- | --- |
| Scoped discovery contract pass count | New copilot not implemented; no measured pass count | 10/10: AC-001..AC-009, AC-040 | First release acceptance / production-host results | GOAL-001 |
| Confirmation/release invariant pass count | New confirmation/receipt not implemented | 12/12: AC-012..AC-023; zero duplicate business releases/audits | First release acceptance / fake-clock, race and injected-failure results | GOAL-002 |
| Model isolation/effects pass count | New client untested; live accuracy unmeasured | 11/11: AC-024..AC-033, AC-042; zero raw-source outbound leaks/unintended effects | First release acceptance / captured fake-provider requests, counting edges, UI tests/review | GOAL-003 |
| Audit/operational boundary pass count | No copilot invocation ledger exists | 9/9: AC-010, AC-011, AC-034..AC-039, AC-041 | First release acceptance / persistence, diagnostic and CI/review evidence | GOAL-004 |
| Total authored criterion coverage | 42 Not Started; no execution evidence in this draft | 42/42 verified, disjoint goal measurement sets total 10+12+11+9=42 | First release acceptance / consolidated evidence | All |

Operational HTTP duration uses existing telemetry vocabulary, not a new latency SLO. The 30-second processing ceiling and one-second source deadline are enforced limits, not measured availability promises. No success metric claims universal injection resistance, live citation accuracy, airworthiness or regulatory compliance.

---

## Risks and Assumptions

### Key Assumptions

| ID | Assumption | Evidence status | Impact if false | Mitigation | Source |
| --- | --- | --- | --- | --- | --- |
| AS-001 | SDK 2.3.0 can host the selected net10.0 stateless wire contract. | Evidenced externally, not installed/tested here | Feature integration fails | Pin and CI restore/build plus production-host protocol tests | Research W2/W8/W10, AC-001/AC-038 |
| AS-002 | Existing provider manifest/gateway registration remains available to the new approved workload. | Partially supported by code, live behavior unvalidated | Model unavailable | Preserve lazy provider boundary; exact refusal/unavailable errors, no fallback | Research C10/C11, AC-024/AC-025 |
| AS-003 | Synthetic fixture identities and canonical status tokens suffice for a useful structural-context demo. | Partially supported; user usefulness/accuracy unmeasured | Answer quality inadequate | Keep explicit limitations; use evidence pane/manual existing UI; re-review any wider content scope | Research C4/C14, AC-005/AC-026 |
| AS-004 | Users heed the synthetic-input notice. | Untested; not a privacy guarantee | Sensitive prose could be sent in a question | Explicit send/notice, no persistence, human policy review; no semantic-detection claim | D9, NFR-007 |
| AS-005 | Repository-supported SQL/in-memory stores can expose a transaction/lock shared with business Commit. | Existing commit seam evidenced; new confirmation transaction unimplemented | Partial release or replay defect | Atomic prerequisite and race/failure tests; no release if persistence cannot satisfy it | Research C5/C7, AC-018..AC-023 |

### Risk Register

| ID | Risk | Probability / impact | Mitigation and residual boundary |
| --- | --- | --- | --- |
| RISK-001 | Model returns hostile/wrong calls or misleading answers. | High / High | Deterministic five-tool/schema/selected-case/approval/citation checks, plain rendering and hostile fake suite. Semantic accuracy is not guaranteed. |
| RISK-002 | Free question includes personal/confidential data despite notice. | Medium / High | Human-approved narrow input policy, explicit send and no prompt diagnostics/history; no automatic classification claim. Provider-side handling remains its governed existing contract. |
| RISK-003 | Source prose enters a later model tool-result message. | Medium / High | Separate raw UI and model-safe serializers; capture every request, including retries. Initial-prompt-only tests are insufficient. |
| RISK-004 | Approval races revision/scope/gate changes or audit/receipt failures. | Medium / High | Shared atomic commit boundary, exact immutable tuple, defensive copies, fault injection and parallel exact-replay tests. |
| RISK-005 | Audit store outage blocks the feature or prevents outcome recording. | Medium / High | Durable admission fail-closed, separate operational error diagnostic, honest outcome-failure handling; no fictional audit availability guarantee. |
| RISK-006 | Future transport/API/library behavior differs from dated evidence. | Medium / Medium | Pin SDK, accepted 2025-11-25 wire scope and CI integration; separately review future OAuth/APIM/protocol expansion. |
| RISK-007 | Same user is accidentally shared across tool clients or source budgets split. | Medium / High | Per-request live session, per-turn same-user MCP credentials, fixed dispatch target, shared admission store and mixed/concurrent tests. |
| RISK-008 | A normal PRD merge could trigger automatic pickup contrary to requested manual delivery. | High / High | User confirmed the actual dispatcher holds this exact PRD path. Keep that hold effective throughout manual delivery; the parent has not independently queried the host. Manual delivery does not waive the human approval gate. |

---

## Glossary

| Term | Definition |
| --- | --- |
| MCP | Model Context Protocol; transport/tool contract, not identity or workflow authority. |
| Thin adapter | MCP dispatch to API-owned policies/projections/commits without new business rules. |
| Selected case | Case explicitly chosen in trusted UI and bound by server for the question/continuation. |
| Open point | API-owned current reviewer-domain unresolved identity, not a model-authored finding or generic worklist entry. |
| Structural context | Closed enums, bounded numbers, literal demo flags and canonical registered IDs admitted at runtime; no source/document/finding prose. |
| Source tuple | `(sourceId, recordId, recordVersion)`; citation additionally requires an actual authorized successful return. |
| Proposal / approval | Immutable final-release command and separate trusted-UI lifecycle state; neither model acceptance nor navigation is approval. |
| Receipt | New immutable serialized proof of one committed simulated release, returned identically on exact replay. |
| Invocation audit | Independent durable admit/outcome ledger, not a business revision-changing read commit or prompt log. |
| Business audit | Existing `aircraft.released` record inside the release transaction. |
| Synthetic / simulated | Fictional fixture data and demonstration-only actions; no real clearance, financial/legal sign-off or lessee contact. |

---

## Sign-Off

### Approval Checklist

- [x] **Delivery gate resolution: user and parent** — user confirmed that the actual dispatcher reports this exact PRD path held. This records the user's confirmation, not an independent host query or PRD approval.
- [ ] **Product Owner / named human merge approver: wesback** — personally review scope, F1-F10 dispositions, role/data matrices, exact errors/limits, input contract and release/audit invariants; record approval through the human PRD-merge gate only after the parent resolves the non-pickup delivery route.
- [ ] **Copilot input-policy gate: wesback** — that human merge explicitly approves FR-013's separately reviewed question/structural-context contract under unchanged ARR:DD-025/DD-034 and provider-manifest boundaries. Draft authoring is not that approval.
- [ ] **Product Sponsor, Technical Lead, Quality Lead and Legal/Compliance functions** — no additional named approver or sign-off has been supplied or granted. Appropriate product/engineering/security/privacy review remains required through the repository's human gate and subsequent manual HVE RPI review; no regulatory/legal compliance approval is implied.
- [ ] **Release evidence** — execute all acceptance tests/CI checks and required UI review after implementation; no test is Completed in this draft.

Approval date: Not approved. No merge record or sign-off is claimed.

### Waivers

None requested or granted. No coverage gap exists. Unmeasured live accuracy and excluded scope are explicit boundaries, not waived security invariants.

### Handoff Readiness

Draft ready for wesback's human review; **Planning blockers: None for product scope and behavior**. Research is completed and all material findings have one disposition. Authored inventory is **4 GOAL, 18 FR, 14 NFR, 8 CON, 42 AC and 12 DD**; FR-to-AC and FR-to-GOAL authored coverage are both **100.0%**. Zero unresolved product scope questions; executable acceptance, formal quality gates and human approval remain pending. The dispatcher-host hold is user-confirmed, not independently queried by the parent.

No PRD_STANDARD_FINDINGS_V1 or PRD_QUALITY_REPORT_V1 gate has been produced; Validate/Finalize exit is not authorized. No signoff artifact hash, approval timestamp, story plan, backlog handoff, issue or security model is fabricated. Manual HVE RPI owns later planning/review/implementation after the preserved human approval gate is safely resolved; work slices retain the 500-changed-line discipline including tests. No automatic pipeline handoff is requested or asserted disabled. This PRD stops at draft creation.

---

## Disclaimer

> [!CAUTION]
> **Disclaimer:** This agent is an assistive tool only. It does not provide product management approval, technical feasibility validation, or business sign-off and does not replace product managers, engineering leads, business stakeholders, or other qualified human reviewers. The output consists of suggested requirements, acceptance criteria, and product specifications to support a user's own product planning and decision-making. All Product Requirements Documents, functional requirements, non-functional requirements, and constraint definitions generated by this tool must be independently reviewed and validated by appropriate product and engineering reviewers before adoption. Outputs from this tool do not constitute product approval, requirements sign-off, or engineering commitment.

The demonstrator uses synthetic data and simulated human/business actions only. Research is not executable or live-model validation. Future deterministic tests establish the named host invariants, not universal model immunity, real aircraft release, airworthiness, financial approval, legal approval or compliance certification.

---

## Document Metadata

- Template Version: 1.0.0.
- Canonical Template: `requirements-author/templates/prd/prd-full.md`.
- Vendored Template: `docs/templates/prd-template.md`; its full heading/frontmatter structure is retained and guidance removed.
- License: CC-BY 4.0 (Microsoft HVE-Core).
- Attribution: Microsoft HVE-Core Team.
- Product Owner / named human merge approver: wesback.
- Authoring phase: Draft Build/Integrate; no approval or implementation.
