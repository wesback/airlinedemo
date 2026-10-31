<!-- markdownlint-disable-file -->
# Task Research: role-aware-copilot-mcp

| Field | Value |
| --- | --- |
| Date | 2026-10-30 |
| Researcher / agent | rpi-research |
| Output mode | analysis, preparing requirements for a PRD |

## Executive Summary

The feature is feasible as a bounded same-application demo: five MCP tools, a one-question model client, a minimal Q&A UI, API-owned role projections/audit, and trusted final-release confirmation. Raw source text stays in the UI and never enters model context. A separately reviewed copilot input contract permits questions and closed structural API context without weakening the investigator's evidence gate.

Both bounded research cycles completed Wider, Deeper, and Contrarian waves. All requirement-changing user decisions are resolved; research is ready for PRD preparation. MCP APIM routing, OAuth OBO, multi-turn conversation history, and earlier-gate writes are excluded. No application, infrastructure, or live model/gateway behavior was implemented or tested.

## What You May Not Know

The application has no delegated user-token authentication or generic next-stage command. Raw case/finance APIs are not the requested role-specific open-points contract. APIM's managed remote-MCP feature documents OAuth-compatible authorization, not the selected demo cookies. A source-data annotation cannot prove that an absent, future copilot client ignores instructions.

## Findings

### F1: Same-application hosting is supported, but demo sessions are a bounded integration

The official C# SDK provides ASP.NET Core HTTP hosting, and NuGet currently publishes ModelContextProtocol.AspNetCore 2.3.0 with a net10.0 target. This establishes a supported hosting fit, not an OAuth identity implementation.

* Questions: Q2, Q3.
* Evidence state: Evidence-backed finding.
* Evidence: W1, W2, W3, C1.
* Confidence and limits: High for hosting/framework compatibility. Authentication and protocol negotiation still require the Deeper wave.

### F2: Final release and source-read policy already belong to the API

The release endpoint is presenter-only, requires anti-forgery protection, checks the expected case revision and three exact blocker codes, and commits against the checked revision. Source reads apply entitlement checks before their session/case admission budget. A thin adapter must preserve these paths rather than call data ports or reproduce their policies.

* Questions: Q1, Q3.
* Evidence state: Evidence-backed finding.
* Evidence: C2, C3.
* Confidence and limits: High for existing release and source behavior. No generic next-stage endpoint exists in the inspected release contract.

### F3: APIM's managed remote-MCP feature does not document demo-cookie authorization

Microsoft's current remote-MCP documentation requires protocol 2025-06-18 or later and either no authorization or standards-compliant MCP authorization. Its documented managed route therefore is not evidence that the selected cookie-authenticated demo service can be exposed unchanged. Generic HTTP forwarding is a separate option whose same-origin cookie and security behavior would need infrastructure evidence.

* Questions: Q5.
* Evidence state: Evidence-backed finding.
* Evidence: W4.
* Confidence and limits: High for the documented limitation; this does not prove that every custom APIM proxy configuration is impossible.

### F4: Opening a confirmation URL is not release approval

The MCP elicitation contract distinguishes consenting to open a URL from completing the interaction. Tool metadata and a client-supplied acceptance response therefore cannot establish that the presenter approved a specific release. The application must hold the authoritative, immutable proposal and record approval through its trusted, session-authenticated, anti-forgery-protected UI.

* Questions: Q3.
* Evidence state: Evidence-backed finding.
* Evidence: W5, W6, C2, C6.
* Confidence and limits: High. The exact proposal lifecycle, expiry, and commit/approval transaction are product decisions, not protocol guarantees.

### F5: An untrusted-data envelope does not prove that a future model ignores instructions

MCP structuredContent is server-produced data, not model-enforced structured output. Tool annotations describe behavior but are not access-control or instruction-isolation mechanisms. The user therefore included a minimal model client and selected deterministic isolation: source prose remains escaped UI data and is excluded from all model messages. The client also requires independent tool/argument/case/approval checks; there is no claim that a model is universally immune to malicious user inputs.

* Questions: Q3.
* Evidence state: Evidence-backed finding.
* Evidence: W6.
* Confidence and limits: High for the protocol boundary. D6/D8/D9 resolve the client and source-isolation scope; live model accuracy remains unmeasured.

### F6: Read auditing needs a separate API-owned persistence operation

The existing SQL business commit is transactional, checks the revision, and records decisions. Its current interface has no invocation-audit append operation. Using a business commit for every read would change the revision that release confirmation must bind. An invocation ledger and authorized-case activity projection must remain API-owned, append-only, and independent of business revisions.

* Questions: Q4.
* Evidence state: Evidence-backed finding.
* Evidence: C5, C7.
* Confidence and limits: High for the missing seam and transaction contract. Durable audit admission must fail closed before a source read or release if storage is unavailable; no system can promise a successful append to an unavailable store.

### F7: Existing raw case/finance reads are not the proposed role-aware open-points API

The raw case route returns full case state after case-scope authorization. Existing reserve and obligation reads check the session/case, but the inspected handlers do not implement the proposed point-detail role matrix. They are not safe substitutes for a deliberately role-projected copilot contract. API-owned summary/open-points projections are feature prerequisites; role filtering must not be added inside MCP tools.

* Questions: Q1.
* Evidence state: Evidence-backed finding.
* Evidence: C4, C8.
* Confidence and limits: High for inspected handlers; this research does not broaden into unrelated API repairs.

### F8: Stateless MCP and API-owned confirmation avoid transport-session authority

SDK 2.3.0 supports the 2025-11-25 protocol as well as 2026-07-28. Its explicit stateless mode avoids assigning an MCP transport session and remains compatible with older initialize-capable clients. Ordinary tool results carrying a same-origin confirmation link, followed by an exact retry after trusted UI approval, do not require native elicitation or unsolicited server notifications.

* Questions: Q2, Q3.
* Evidence state: Evidence-backed finding.
* Evidence: W3, W5, W8, W9, W10.
* Confidence and limits: High. Protocol compatibility must be exercised through the application's production startup with the selected package; no package was installed during research. Stateless transport does not remove application demo-session or approval state.

### F9: Reusing the gateway client does not authorize new copilot content

The application already registers a lazy, correlated Microsoft.Extensions.AI.IChatClient through the governed model gateway. Its investigator-specific gate, however, admits only exact hashes of committed synthetic evidence strings with a constrained shape; it rejects an empty evidence set and unregistered content. Arbitrary questions, raw source JSON, and dynamic case projections are not automatically authorized by that gate. The minimal copilot needs an explicit reviewed input contract, without weakening the investigator gate or creating a direct model-endpoint fallback.

* Questions: Q6, Q7.
* Evidence state: Evidence-backed finding.
* Evidence: C10, C11, C14.
* Confidence and limits: High. The existing approved manifest validates provider configuration; it is not proof that a new content/workload contract is approved.

### F10: The client must constrain effects even when model instructions fail

Official tool-calling examples leave function dispatch and argument validation to the application. Official safety guidance explicitly says mitigations do not eliminate prompt-injection risk. Deterministic host invariants can still reject unknown tools, cross-selected-case calls, arbitrary URLs, user/role changes, and unconfirmed releases, irrespective of model output.

The strongest bounded source-text option is to keep raw record text out of model messages entirely: show it as escaped, untrusted data in the UI while the model receives only an API-owned structural projection. If the model reads source prose, the PRD must define a separately reviewed synthetic corpus/admission policy and cannot claim universal instruction immunity.

* Questions: Q7.
* Evidence state: Evidence-backed finding.
* Evidence: W11, W12, W13, C11, C14.
* Confidence and limits: High for application-owned dispatch and the stated limitation. Fake hostile-model tests prove host invariants, not live model accuracy or universal resistance.

## Recommendation and Alternatives

The confirmed direction is a same-origin stateless endpoint, API-owned role projections and invocation ledger, a bounded one-question client/Q&A UI, and a trusted confirmation link with server-held approval followed by an exact tool retry. Approval expires five minutes after proposal creation and is consumed atomically with successful release. Gateway routing is deferred; source prose is UI-only.

| Option | Benefit | Cost or limit | Evidence | State |
| --- | --- | --- | --- | --- |
| Same-origin stateless MCP with existing demo sessions | Preserves current identity and avoids a privileged MCP credential | Not an interoperable OAuth connector for external clients | C6, W3, W7, W10 | Matches confirmed direction |
| Managed APIM remote-MCP route now | Gateway governance | Documented authentication model does not establish demo-cookie compatibility | W4 | Deferred by D5 |
| Trusted UI approval plus exact tool retry | Consent state belongs to the application, not model arguments | Requires API-owned immutable proposal and atomic approval/release persistence | C2, C7, W5 | Confirmed by D7 |
| Free-text question with structural API context; source prose UI-only | Conversational entrypoint without source-instruction influence | Separate reviewed input contract; no automatic semantic personal-data classification claimed | C11, C14, W12 | Confirmed by D9 |
| Only annotations to enforce source safety | Little implementation work | Does not enforce model behavior | W6 | Rejected as sufficient enforcement |

## Scope and Questions

* Goal: establish evidence-backed requirements for five thin, role-aware MCP tools over the existing case API.
* Audience and use: the product owner, PRD builder, and post-approval pipeline planner.
* In scope: current API contracts, role visibility, shared read limits, hosted .NET MCP transport, trusted release confirmation, safe invocation audit, and a minimal governed model client with deterministic source-instruction safeguards.
* Out of scope: implementation during research, live cloud changes, MCP APIM routing, OAuth OBO, new business gate rules, earlier-gate writes, and the full conversational chat panel.
* Criteria: preserve API-owned authorization and gates; prevent cross-user session sharing, unconfirmed writes, unauthorized disclosure, and source-text instruction execution.
* Requested output: dated primary research and material finding dispositions for the PRD.

| ID | Question | Source | Status |
| --- | --- | --- | --- |
| Q1 | Which existing routes support the proposed tools, and where are API prerequisites missing? | User request and current main | Answered: F2, F6, F7; add API-owned projected reads/audit/confirmation, not MCP business rules |
| Q2 | Which supported .NET MCP server transport and package contract fit same-application hosting? | External SDK and protocol documentation | Answered: F1/F8; supported net10.0 package and explicit stateless mode |
| Q3 | How can demo-session authorization and a trusted confirmation UI protect final release without model-supplied consent? | User-confirmed scope and protocol security guidance | Evidence answered: F4/F5/F8; D6/D7 require product decisions |
| Q4 | How can every invocation be audited without writing to an unauthorized case or changing business revisions on reads? | User-confirmed audit split and persistence contracts | Answered: F6; API-owned independent ledger, scoped projection, and fail-closed admission |
| Q5 | Can the existing private APIM gateway route the session-authenticated MCP service without widening this release or violating current managed-tenant controls? | User gateway goal and current official gateway sources | Answered within scope: managed-route cookie compatibility is not established; D5 defers MCP routing and leaves the existing model gateway unchanged |
| Q6 | What is the smallest model-client entrypoint that supports the example questions without adding a full chat panel? | User scope expansion after F5 | Answered: D8 selects a one-question API and minimal Q&A UI, without stored multi-turn history |
| Q7 | Which existing gateway/model-client seams can be reused, and what deterministic source/approval controls can the host enforce regardless of model output? | User requirement for model-client source safeguards | Answered: F9/F10, D9; reuse provider/limits, add reviewed structural-context admission and host controls, exclude source prose from model messages |

## Decisions and Feedback

| Group | Decision | Status | Owner | Evidence / rationale | Impact |
| --- | --- | --- | --- | --- | --- |
| D1 | Use existing demo sessions; defer real OAuth OBO. | Confirmed | User | Answer: identity=existing-demo-session | No new Entra user-authentication prerequisite. |
| D2 | The only business write is the presenter's final aircraft release. | Confirmed | User | Answer: advance_scope=final-release-only | No technical, finance, legal, intake, or investigation commands through MCP. |
| D3 | Include a minimal trusted confirmation UI; exclude the full chat panel. | Confirmed | User | Answer: confirmation=include-minimal-confirmation-ui | Human consent must be verifiable independently of tool arguments. |
| D4 | Authorized case invocations use case activity; no-case, anonymous, and cross-case attempts use caller/security audit. | Confirmed | User | Answer: audit_split=true | No unauthorized case writes or existence disclosure. |
| D5 | Defer MCP APIM routing; use a same-origin demo release. | Confirmed | User | Answer: Defer MCP APIM routing; same-origin demo release. | No infrastructure story or resource change in this PRD; existing model gateway remains unchanged. |
| D6 | Include a minimal model client now; reopen feature scope. | Confirmed | User | Answer: Include a minimal model client now; reopen feature scope. | Requires Cycle 2 and client-side behavioral requirements in this PRD; full chat panel remains excluded. |
| D7 | Trusted UI approves; exact tool retry performs release; proposal expires after five minutes. | Confirmed | User | Answer: confirmation_flow=Trusted UI approves; exact tool retry performs release, approval_lifetime_minutes=5. | Bind approval to immutable user/session/case/action/revision/note; consumption and successful release are atomic; successful retries return the original receipt. |
| D8 | Add a minimal question-and-answer UI as well as the one-question model-client API. | Confirmed | User | Answer: Minimal question-and-answer UI as well. | Include one question/answer surface; the offered no-history boundary remains, and a full multi-turn chat panel is still excluded. |
| D9 | Free-text questions and structured API context; raw sources UI-only; reviewed copilot input contract. | Confirmed | User | Exact answer: Free-text questions and structured API context; raw sources UI-only; reviewed copilot input contract. | A separate reviewed admission policy is required; preserve the investigator gate and exclude raw record prose from model messages. |

## Risks and Open Questions

| Priority | Type | Item | Smallest evidence needed |
| --- | --- | --- | --- |
| High | Risk | Model requests can be wrong or hostile even when source prose is excluded. | Deterministic allowlist, schema, selected-case, entitlement, approval, and output-rendering tests through the real host. |
| High | Risk | Free questions could contain data outside the synthetic-demo policy; prose classification is not a structural guarantee. | Explicit question-send notice, no prompt/history logging, separately reviewed admission contract, and no claim of automatic semantic personal-data detection. |
| High | Risk | Audit storage can be unavailable. | Persist admission before dispatch; fail closed with no source call/release, and disclose the operational audit failure. |
| High | Risk | Approval consumption can race case updates or fail independently of release. | API store transaction/lock must atomically bind immutable approval, expected revision, gate check, release, receipt, and business audit; inject race/failure tests. |
| Medium | Deferred research | Future APIM/OAuth route requires its own compatible identity and current policy assessment. | Separate feature; not a blocker for this same-origin release. |

## Planning Readiness and Next Step

| Field | Record |
| --- | --- |
| Research disposition | Executed; both cycles synthesized |
| Decision participation | User-owned; explicit scope decisions recorded above |
| Planning Readiness | Ready for PRD preparation; F1-F10 and D1-D9 cover all material questions |
| Research depth and helpers | Two complete Wider/Deeper/Contrarian cycles; no helpers used |
| Blockers | None |
| Output mode and planning support | Analysis; supports PRD preparation once material questions are answered |
| Continuation owner | Requirements-author parent; the pipeline owns post-merge story planning |
| Required gates or confirmations | Named person's PRD merge explicitly approves the copilot input contract before implementation; provider-manifest approval remains unchanged; no cloud changes authorized |
| Next action | Research closes; requirements-author parent prepares the PRD with the required PRD builder. No RPI Plan handoff: post-merge pipeline owns story planning |
| Primary evidence file | docs/research/2026-10-30/role-aware-copilot-mcp-research.md; date from the user's current_datetime |

## Research Record

### Method and Boundaries

| Field | Record |
| --- | --- |
| Research posture and provenance | Balanced; default, bounded to questions that can change this feature's requirements |
| Completion basis | Material claims supported, all three waves completed, and no unresolved requirement-changing question |
| Explicit limits or deadline | Research phase only; no user-supplied time or source ceiling |
| Codebase and external scope | Tracked backend/frontend/infra/docs; official MCP/.NET/APIM documentation and authenticated managed-tenant controls |
| Initial candidate areas | Auth, SourceSystems, CasePack, Workspace, Release, Persistence, MCP C# SDK, protocol security, and APIM MCP routing |
| Evidence root | docs; explicitly required as trusted evidence root by repository instructions |
| Constraints and excluded sources | No secrets, unrelated untracked files, implementation, infrastructure changes, or automatic story planning |
| Prior knowledge | Current-main routes and source summary #363 were inspected before research intake; verify applicable details against source before synthesis |

### Extensions and Participation

#### Extension Registry

| Kind | Candidate | Provenance and scoped contract | Selected or skipped reason |
| --- | --- | --- | --- |
| Instruction | Repository AI-SDLC PRD-first and research gate | Repository custom instructions | Selected: research and PRD approval precede large feature implementation. |
| Skill | requirements-author | Available skill and user feature scope | Selected parent: requirements and stable traceability. |
| Skill | belux-azure-policy-gate | Available user skill; existing managed-tenant APIM goal | Selected for current authenticated policy evidence only; no resource change authorized. |
| Skill | entra-app-registration | Available skill | Skipped: user explicitly deferred OAuth OBO. |
| Skill | backlog-plan | Available skill | Skipped: post-merge pipeline owns story planning. |

#### Direction and Participation Log

| Checkpoint | Direction | Answer | Effect |
| --- | --- | --- | --- |
| Intake | Clarify identity, writes, confirmation, and audit scope. | Existing sessions, final release only, minimal confirmation UI, and audit split. | Supersedes the initial OBO and every-stage interpretations. |
| Research intake | Resolve root/date and phase. | Repository requires docs/research; user date is 2026-10-30. | Primary path fixed; research-only boundary active. |
| Gateway decision | Managed APIM cookie compatibility is not established. | Defer MCP APIM routing; same-origin demo release. | D5 resolved; no gateway/tenant design required for this release. |
| Source-safety decision | A server-only envelope cannot establish absent model-client behavior. | Include a minimal model client now; reopen feature scope. | D6 adds Q6/Q7; execute a new bounded Wider/Deeper/Contrarian cycle. |
| Client-surface decision | Headless one-question API was offered without stored history or a chat panel. | Minimal question-and-answer UI as well. | D8 includes a small Q&A surface over that client, not a persistent multi-turn chat product. |
| Model-input decision | Existing exact-hash evidence gate does not authorize generic questions/API JSON. | Free-text questions and structured API context; raw sources UI-only; reviewed copilot input contract. | D9 resolves source/model data flow; define closed structural context and user-message input, with no claim of semantic personal-data detection. |
| Confirmation decision | Approval must be independently recorded and exactly bound. | Trusted UI approves; exact tool retry performs release; expiry is five minutes from proposal creation. | D7 resolves lifecycle; no approval extension, no model-supplied consent, and one atomic release/receipt. |

### Research Cycle Log

#### Cycle 1

* Active controls: confirmed D1 through D4, analysis mode, research-only writes.
* Wider focus: official C# hosting, protocol transport, API release/source paths, and APIM remote-MCP support.
* Wider evidence: W1/W2 establish SDK and net10.0 compatibility; W3 establishes Origin validation and optional stateful sessions; C2/C3 establish existing gate and read-budget ownership; W4 requires a standards-compliant authorization mode.
* Wider reflection: investigate stateless request identity, API-owned read projections, confirmation completion, and audit revisions. Do not assume a managed APIM MCP route accepts demo cookies.
* Deeper focus: SDK session/identity semantics, out-of-band confirmation completion, API transactions, read admission, and raw projection visibility.
* Deeper evidence: W5 distinguishes URL navigation from completed approval; W6 distinguishes data/schema/annotations from enforcement; W7 documents optional standardized OAuth rather than demo-cookie interoperability; W8 documents SDK 2.3.0's SessionMode API and stateless default; C6/C7/C8 establish the actual demo-session, transaction, and projection constraints.
* Deeper reflection: challenge protocol-version compatibility, cross-user replay, caller-supplied origin markers, metadata-only injection promises, and read-audit revision coupling. Keep internal managed-tenant control contents out of public artifacts.
* Contrarian focus: challenge assumed cookie/OAuth interoperability, latest-SDK examples, metadata-only instruction guarantees, client-reported approval, and business-audit reuse.
* Contrarian evidence: W4/W7 do not establish managed APIM cookie compatibility; W10 verifies older protocol support and changed latest-protocol bootstrap; W5 says navigation acceptance is not interaction completion; W6 provides no annotation-enforced model isolation; C5/C7 show invocation audit cannot safely be implemented as a routine business commit.
* Contrarian reflection: keep a bounded demo-only contract, avoid native elicitation dependencies, add API-owned projections/ledger/approval transaction, and explicitly disposition gateway and future-client scope.

| Material | Evidence | Disposition | Effect |
| --- | --- | --- | --- |
| Stateless same-application MCP | C1, C6, W1-W3, W8-W10 | Accepted as evidence-supported | F1/F8; user identity remains request-bound |
| Reuse source admission and final release policy | C2, C3, C9 | Accepted | F2; no MCP-owned business logic |
| Raw case/finance reads as role-filtered points | C4, C8 | Rejected | F7; projected API prerequisites |
| Business commits for read invocation audit | C5, C7 | Rejected | F6; independent append-only API ledger |
| Annotation-only source-instruction guarantee | W6 | Rejected | F5; D6 owns future-client disposition |
| Immediate managed APIM cookie route | W4, W7 | Not established | F3; D5 owns scope |

* Another complete three-wave cycle needed: no, unless the user selects a materially wider gateway/client design.
* Stop basis: all bounded questions have source-supported answers or an explicit product decision. Further protocol/SDK sources are redundant for the selected same-application scope.
* Readiness effect: Cycle 1 completed; D6 then reopened scope and triggered Cycle 2.

#### Cycle 2

* Trigger: user-confirmed D6 adds a minimal model client.
* Active controls: demo sessions, final release only, trusted UI, split audit, no MCP APIM route, no full chat panel.
* Wider focus: a one-question API plus minimal Q&A UI, official application-owned tool calling, and existing gateway/provider infrastructure.
* Wider evidence: C10 provides a lazy correlated IChatClient factory; C13 supplies a session-owned React shell; W11/W13 describe application-owned function execution.
* Wider reflection: reuse the provider boundary, not the investigator's task/output contract or unconditional function dispatch.
* Deeper focus: exact content admission, per-turn execution limits, message trust, selected-case binding, and human release authority.
* Deeper evidence: C11 requires exact committed synthetic content and a nonempty evidence set; C12 supplies 20-tool/8-model/30-second execution defaults; C14 constrains the entire product to synthetic data and independent deterministic validation.
* Deeper reflection: a new copilot content contract is a real prerequisite; raw API JSON cannot be sent by assuming the investigator's gate approves it.
* Contrarian focus: assume the model returns hostile source instructions, an unknown function, a different selected case, forged approval, or a fabricated link.
* Contrarian evidence: W11 requires argument validation; W12 expressly says guardrails do not remove all injection risk; C11 disproves blind gate reuse. API authorization, exact selected-case/tool schemas, server-owned confirmation, and plain-text rendering remain independently enforceable.
* Contrarian reflection: favor UI-only raw source text and structural model context for deterministic isolation; user D9 must disposition free-text input versus existing corpus restrictions.
* Synthesis: accept existing provider/limits/UI seams (C10/C12/C13), reject blind content-gate reuse (C11), reject universal model immunity (W12), and require explicit input/approval contracts (D7/D9).
* Another complete cycle needed: no; D9 selected structural context with raw sources UI-only, and D7 fixed confirmation behavior.
* Stop basis: selected scope is covered by API, provider, protocol, and security evidence; remaining risks are explicit requirements and approval gates, not unresolved research choices.
* Readiness effect: all seven questions answered and all nine material decisions confirmed; Ready for PRD preparation.

### Evidence Log

| ID | Claim | Source | Retrieved / version | Tool | Confidence / limits |
| --- | --- | --- | --- | --- | --- |
| C1 | The application targets net10.0. | backend/AirlineDemo.Api/AirlineDemo.Api.csproj, TargetFramework | Not applicable | view | High |
| C2 | Presenter-only final release uses CSRF, exact blockers, and expected-revision commit. | backend/AirlineDemo.Api/Decisions/ReleaseEndpoints.cs, Release and Blockers | Not applicable | view | High |
| C3 | Source authorization precedes shared session/case admission and data-port reads. | backend/AirlineDemo.Api/SourceSystems/SourceReadEndpoints.cs, Read and Allowed | Not applicable | view | High |
| C4 | Worklist is role-owned work items; scene includes history and object/evidence references, not a dedicated role-specific open-points contract. | backend/AirlineDemo.Api/Workspace/WorkspaceEndpoints.cs, GetWorklist; backend/AirlineDemo.Api/Projection/CasePackSceneProjection.cs, Build | Not applicable | view | High for inspected projections |
| C5 | Current persistence exposes business Commit, work-item mutations, and AppendTrace but no invocation-audit append. | backend/AirlineDemo.Api/Persistence/ICaseStateStore.cs; backend/AirlineDemo.Api/Persistence/SqlCaseStateStore.cs | Not applicable | rg | Requires transaction detail in Deeper wave |
| C6 | Demo authentication resolves an HttpOnly/Secure/SameSite Strict host cookie; mutations validate X-CSRF-Token. | backend/AirlineDemo.Api/Auth/DemoAuthEndpoints.cs, CurrentSession and HasValidAntiForgery | Not applicable | view | High; no bearer/OBO claims |
| C7 | SQL Commit uses a serializable transaction and exact receipt-fingerprint replay. | backend/AirlineDemo.Api/Persistence/SqlCaseStateStore.cs, Commit | Not applicable | view | High; current release does not issue a receipt |
| C8 | Raw case/finance projections do not implement proposed point-detail role filtering. | backend/AirlineDemo.Api/Auth/CaseEndpoints.cs, GetCase; backend/AirlineDemo.Api/Finance/FinanceEndpoints.cs, GetReserve and GetObligations | Not applicable | view and rg | High for inspected routes |
| C9 | Source budget is 20 admitted requests/session/case/rolling 60s, 2 in-flight/case, and a 1s deadline. | backend/AirlineDemo.Api/SourceSystems/SourceReadBudget.cs; SourceReadEndpoints.Read | Not applicable | view | High; direct/tool calls must share the existing admission store |
| C10 | Existing gateway IChatClient factory is lazy, manifest-configured, correlated, and does not use a direct model endpoint. | backend/AirlineDemo.Api/Program.cs, lines 69-114; backend/AirlineDemo.Api/Evaluation/ModelGatewayTokenomics.cs, RequirePrivateGatewayEndpoint and GatewayCorrelation | Not applicable | view and rg | High; not new workload/content approval |
| C11 | Investigator gate requires exact committed synthetic evidence; its task and claim output are not generic copilot chat. | backend/AirlineDemo.Api/Investigation/ApprovedModelManifest.cs, SyntheticContentRegistry and ModelCallGate.Authorize; BoundedInvestigator.Instructions and GatedModelClient | Not applicable | view | High |
| C12 | Existing execution defaults bound tools/model attempts/time/tokens/retries. | backend/AirlineDemo.Api/Investigation/InvestigationLimits.cs, InvestigationLimitOptions and InvestigationLimits | Not applicable | view | High; each new turn needs independent limits |
| C13 | Signed-in React shell owns the session and scoped case routing; frontend uses Vitest/Testing Library. | frontend/src/App.tsx, SignedInShell; frontend/package.json | Not applicable | view | High |
| C14 | Canonical demonstrator scope is synthetic-only; source/model text is data, not workflow authority. | docs/prd/aircraft-return-readiness.md, DD-025/DD-034, security lines 182-186, CON-001 | Not applicable | rg | High; identifiers are local to ARR PRD |
| W1 | Official C# SDK provides ASP.NET Core HTTP server package. | [Official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), Packages | 2026-10-30; current README | web_fetch | High |
| W2 | Stable ASP.NET Core package 2.3.0 includes net10.0. | [NuGet ModelContextProtocol.AspNetCore](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) | 2026-10-30; 2.3.0 | web_fetch | High; package availability verified, not installed |
| W3 | Streamable HTTP requires Origin validation and supports optional sessions. | [MCP Transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) | 2026-10-30; 2025-11-25 | web_fetch | High |
| W4 | APIM remote MCP documents protocol/authentication limits and streaming-body restrictions. | [Connect and Govern Existing MCP server](https://learn.microsoft.com/en-us/azure/api-management/expose-existing-mcp-server) | 2026-10-30; page updated 2026-06-25 | web_fetch | High; not a deployed-route verification |
| W5 | URL elicitation acceptance is navigation consent, not completed approval; retries may follow out-of-band completion. | [MCP Elicitation](https://modelcontextprotocol.io/specification/2025-11-25/client/elicitation) | 2026-10-30; 2025-11-25 | web_fetch | High; native elicitation is optional |
| W6 | Tool structured results are data; annotations are not enforcement; execution errors use isError. | [MCP Tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools) | 2026-10-30; 2025-11-25 | web_fetch | High |
| W7 | Standard HTTP MCP authorization is optional but, when implemented, is OAuth-based. | [MCP Authorization](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization) | 2026-10-30; 2025-11-25 | web_fetch | High; demo sessions are not an OAuth implementation |
| W8 | SDK 2.3.0 supports ASP.NET Core MapMcp/WithHttpTransport and explicit SessionMode with stateless default. | [SDK 2.3.0 transport guide](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.3.0/docs/concepts/transports/transports.md) | 2026-10-30; tag v2.3.0 | web_fetch | High; older Stateless property examples are not the current API |
| W9 | Every request needs identity verification; a transport session ID is not authentication. | [MCP Security Best Practices](https://modelcontextprotocol.io/specification/2025-11-25/basic/security_best_practices) | 2026-10-30; 2025-11-25 | web_fetch | High; source examples remained inert |
| W10 | SDK 2.3.0 supports 2025-11-25 and 2026-07-28; stateless mode preserves older-client compatibility without session authority. | [Protocol versions](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.3.0/src/Common/McpProtocolVersions.cs); [Stateless/stateful guide](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.3.0/docs/concepts/stateless/stateless.md) | 2026-10-30; tag v2.3.0 | gh tree discovery and web_fetch | High; SDK 2026 defaults differ from earlier examples |
| W11 | The application dispatches tool calls and must validate hallucinated arguments. | [Official OpenAI .NET function-calling example](https://github.com/openai/openai-dotnet/blob/main/examples/Chat/Example03_FunctionCalling.cs) | 2026-10-30; current official example | web_fetch | High; example architecture, not authorization enforcement |
| W12 | Instruction isolation, structured flows, and approval mitigate but do not eliminate prompt injection. | [OpenAI Safety in building agents](https://developers.openai.com/api/docs/guides/agent-builder-safety) | 2026-10-30; current guidance | web_fetch | High; guidance only, no Agent Builder adoption proposed |
| W13 | Microsoft.Extensions.AI supports function-calling orchestration independent of the provider SDK. | [Microsoft .NET function-calling quickstart](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/use-function-calling) | 2026-10-30; page updated 2026-03-11 | web_fetch | High; tutorial provisioning instructions remained inert |

#### Contradictions and Conflicts

The initial search summary reported SDK 1.4.0 as latest and linked a non-authoritative protocol domain. Those claims are rejected: the official repository and NuGet establish the actual package and documentation sources. Search summaries are discovery aids, not canonical evidence.

Authenticated managed-tenant policy material was consulted read-only. It is not copied into this public-facing research artifact or used to assert deployment approval; no Azure changes are in the approved scope.

### Artifact Self-Check

All seven questions map to ten findings and stable C/W evidence. Both cycles record Wider, Deeper, and Contrarian in order, reflections, dispositions, and an evidence-based stop. All nine material user decisions are persisted. No source code, infrastructure, secrets, or internal policy catalog contents were written. Source examples remained inert. This is an artifact self-check, not executable or live-model validation. Research is complete and the phase is closed.
