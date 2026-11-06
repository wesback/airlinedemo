# Task Research: Foundry migration for the airline demo

| Field | Value |
| --- | --- |
| Date | 2026-11-03 |
| Researcher / agent | rpi-research |
| Output mode | convergence |

## Executive Summary

A real private Foundry resource and project can supply managed prompt-agent orchestration, persisted conversations, model management, project-backed evaluations and connected observability. The user explicitly selected managed orchestration and state. Business/tool authorization stays in the application API. Creating a project alone does not integrate these capabilities.

Research completed two Wider, Deeper and Contrarian cycles. Private inbound endpoints alone are insufficient: managed agents and evaluations need outbound subnet injection, storage/identity configuration and private monitoring. Microsoft documents that outbound injection cannot be added to an existing account. The selected managed-agent direction requires a new private Standard agent setup with BYO Cosmos DB, Storage and AI Search, not the evaluation-only topology or a relabeled OpenAI resource.

Sweden Central supports projects, prompt agents, private evaluation and relevant quality/safety evaluators. Hosted red teaming and protected-material evaluators have other regional requirements and should not be silently enabled. The evidence now supports PRD authoring; human PRD approval, current tenant policy/cost checks and real private integration proofs remain required gates.

## What You May Not Know

The existing Terraform platform apply may complete safely, but no standalone-model app release is being advanced. Its SQL, gateway, registry, networking and signing work remains useful.

An in-place OpenAI upgrade is supported, including by the pinned provider. That is not evidence that private cloud evaluation can be retrofitted: the outbound injection restriction changes the recommended migration approach.

Foundry's portal-created AI Gateway is initially public; preserve the repository's internal APIM deployment instead. Managed Agent Service is a separate runtime choice, not a prerequisite for Foundry evaluation of this app.

## Findings

### F11: Real Foundry infrastructure is supported by the pinned provider

AzureRM 4.81.0 exposes `kind = AIServices`, `project_management_enabled`, account managed identity, network injection and `azurerm_cognitive_account_project` with project endpoints. Microsoft documents non-destructive OpenAI/AIServices conversion using AzureRM 4.56 or later.

Questions: Q8. Evidence state: Evidence-backed finding. Evidence: W14, W18, W19; C15. Confidence: high for schema and supported upgrade, not a live migration result.

### F12: Private evaluations change the account creation decision

Cloud evaluations require subnet-delegated outbound injection as well as approved private endpoints, DNS and project permissions. Current networking guidance says existing Foundry deployments cannot add outbound injection later. The official evaluation-only example creates an injected account, project, storage connection, private App Insights and project roles without Cosmos DB or AI Search.

Questions: Q8, Q11. Evidence state: Evidence-backed finding. Evidence: W16, W20, W22, W23. Confidence: high for documented constraints. A new named Foundry account avoids assuming an undocumented conversion-plus-injection upgrade.

The sample and evaluation troubleshooting differ about whether a project capability host is necessary for pure evaluation. The sample makes it optional for data generation, while troubleshooting instructs adding it for stalled evaluations. Adopt a conservative explicit account/project capability-host readiness check and verify a real bounded evaluation before release; do not infer success from ARM provisioning.

### F13: Meaningful evaluations do not require replacing application orchestration

Foundry supports evaluating precomputed query/response datasets, with versioned project datasets and persisted results. The C# examples expose project and evaluation clients; current setup includes prerelease/experimental APIs. The app already has bounded Microsoft Agent Framework orchestration and a deterministic four-arm evaluator. Preserve that as the authority for exact domain assertions and add Foundry quality/safety scoring as a distinct measured layer.

Questions: Q9, Q10. Evidence state: Evidence-backed finding. Evidence: W15, W17, W24, W25; C16. Confidence: high for capability, implementation must pin published packages and prove service compatibility.

Model-judge scores are not proof of authorization or airworthiness. Release checks must fail closed on missing, incomplete, mismatched or failed evaluation evidence; deterministic role/tool/no-write tests remain mandatory.

### F14: Safety and telemetry already exist and must not regress

APIM validates caller identity, approved model and correlation headers. Prompt Shields screens synthetic records through a separate gateway route. Diagnostics redact before Azure Monitor export. Foundry integration should add explicit model content-filter configuration, project linkage and safe GenAI span metadata, not duplicate all existing controls or turn on raw prompt/response collection.

Questions: Q9, Q11. Evidence state: Evidence-backed finding. Evidence: C15, C17, C18; W15, W16. Confidence: high for observed code. No claim is made that content filtering establishes prompt-injection immunity.

### F15: Regional and network limitations constrain showcase features

Sweden Central supports Foundry projects, VNet evaluations, batch quality/safety evaluations and Groundedness Pro. Current hosted AI red teaming supports East US 2/North Central US; protected-material evaluation supports East US 2. Standard managed agents introduce additional state/resource/network considerations. Existing source/tool permissions must stay in the API even if a future runtime changes.

Questions: Q9-Q11. Evidence state: Evidence-backed finding. Evidence: W20-W23, W26; C16. Confidence: high for current documented availability, live policy/quota checks are deployment gates.

### F16: Managed prompt agents satisfy selected orchestration and state scope

The new Foundry runtime persists named/versioned agent definitions, conversations, response items and tool outputs. C# supports agent creation, conversation creation and response/function-output submission. Use ordinary managed prompt agents, not an ephemeral Responses-only agent, hosted copies of the old loop, or the Copilot harness. The latter enables broader built-ins and rejects several bounded-response settings.

Questions: Q10-Q11. Evidence state: Evidence-backed finding. Evidence: W27-W30, W32. Confidence: high for documented API shape; package versions and real service interoperability need executable proof.

### F17: Private managed state needs the Standard topology

Standard private agents require BYO Cosmos DB, Storage and AI Search; the evaluation-only topology is no longer sufficient. New runtime Cosmos containers differ from Classic. Project capability configuration is creation-time state: changing stores can require project recreation. Documentation inconsistently quotes 3000 RU/s total, five 1000-RU/s containers and Classic-only three-container troubleshooting. Do not prescribe a speculative fixed capacity or promise zero new cost; exact new-runtime provisioning, cost and quota must pass the reviewed deployment preflight before apply.

Questions: Q10-Q11. Evidence state: Evidence-backed finding. Evidence: W28, W31, W33. Confidence: high for resource requirements, conditional for sizing until provider/service plan validation. Keep all resources in Sweden Central unless a person approves another region.

### F18: Gateway placement and managed-model mediation are different contracts

APIM supports governing Responses APIs. The application can send its project agent/conversation operations through an explicitly allowlisted private gateway API, authenticated as the application, with APIM using its own managed identity to Foundry.

This governs API-to-agent traffic, not every internal service-to-model inference. A separate Foundry-to-APIM model connection is documented, but W34 contradicts itself: examples use Responses while its supported-configurations section says Responses is unsupported. Static connection headers also do not establish forwarding of this application's seven per-call correlation headers. Do not require this uncertain path for the first managed-agent release or describe native internal model calls as APIM-mediated.

Questions: Q8, Q10-Q11. Evidence state: Evidence-backed finding plus explicit architecture recommendation. Evidence: W29, W34-W36; C15. Confidence: high for general Responses mediation, conditional for exact project-operation proxy until live tested. PRD must explicitly supersede the old per-inference gateway/call-count contract where managed inference is opaque. Model allowlisting, deployment RAI filters, workload budgets and service usage evidence remain required.

### F19: Function execution can remain behind API policy

Foundry selects declared function calls; the application executes and returns outputs. Keep exactly the current five copilot tools, dispatching through `CopilotMcp.InvokeToolAsync` with the live demo session. Agent configuration, tool names, argument JSON and conversation IDs never confer authority. No remote cookie bridge, public MCP endpoint, privileged tool service account or service-side business-write tool is necessary.

Questions: Q10-Q11. Evidence state: Evidence-backed finding. Evidence: W30; C19-C21. Confidence: high for client-side function execution and current host controls. Existing investigator tools need their own fixed registered definitions and existing synthetic hash/source gates before tool outputs enter managed history.

### F20: Persisted history changes the content and authorization boundary

Conversations persist messages/tool outputs and can be reused across sessions. A project is the shared service-level isolation unit, not a per-reviewer ACL. The API therefore needs durable owner/role/case/entitlement/revision/agent-version bindings and atomic single-writer turn admission before any Foundry history access. Revalidate live identity at each tool/output boundary. Invalidate incompatible histories and reauthorize citations; past access is not permission now.

Questions: Q10-Q11. Evidence state: Evidence-backed finding and application requirement inference. Evidence: W29, W33; C19-C22. Confidence: high. Raw source prose remains excluded from copilot history. Investigations may store only exactly approved synthetic evidence. Human PRD merge must explicitly approve this new persistent-content contract, scoped retention/cleanup and companion amendments. Local telemetry redaction alone cannot prove service-side tracing is content-free.

## Recommendation and Alternatives

The user selected managed Agent Service orchestration and agent state. Recommend ordinary versioned managed prompt agents plus a private Standard setup, private APIM at the application-to-project boundary, existing API-owned function execution, private evaluation, explicit safety configuration and redacted observability. Include a presenter-only, read-only AI assurance view showing actual evidence status and links, not decorative claims that controls are active. Migrate both the case copilot and live investigator; retain deterministic validators and four-arm scoring.

| Option | Benefits | Costs and risks | Evidence | Disposition |
| --- | --- | --- | --- | --- |
| New private Foundry account/project with evaluation injection; keep app runtime/APIM | Full lifecycle showcase without replacing business authority | Does not satisfy subsequently selected managed orchestration | F11-F15 | Superseded by user choice |
| In-place account upgrade plus project only | Smallest model migration | Does not establish private cloud evaluation readiness | F11-F12 | Rejected as sufficient for requested lifecycle demo |
| Private Standard managed prompt agents with BYO state and API-owned tools | Selected managed runtime/state and lifecycle showcase | New Cosmos/Search/storage cost, persistent-content contract and exact conversation isolation | F16-F20 | Selected direction; PRD approval required |
| Keep standalone OpenAI | No migration work | Does not satisfy explicit user target | D10 | Rejected |

Retire the old standalone model resources only after a reviewed migration plan and successful replacement checks. Never destroy the whole application again as a shortcut.

## Scope and Questions

Goal: establish a supported, private, reproducible Foundry target and meaningful safety, evaluation and observability capabilities for this flagship demo.

Audience: product owner, PRD approvers, implementation team. In scope: Terraform/model/gateway/client contracts, Foundry projects, evaluation, safety, tracing, identity, network isolation and deployment automation. Out of scope: implementation before the repository PRD merge gate, production customer data, exposing private services, automatic protected-release approval.

Criteria: demonstrable user value, supported APIs, exact testable outcomes, regional/private compatibility, few deployment inputs and retained authorization/audit semantics.

| ID | Question | Source | Status |
| --- | --- | --- | --- |
| Q8 | How does the model/gateway path become a real Foundry resource/project? | User | Answered F11-F12 |
| Q9 | Which safety, evaluation and tracing features add value? | User | Answered F13-F15 |
| Q10 | Must the app migrate to Agent Service? | Scope inference/user decision | Not technically required, but explicitly selected; F16-F20 |
| Q11 | Which private/identity/regional/lifecycle constraints change requirements? | Deployment | Answered F12/F15 |

## Decisions and Feedback

| Group | Decision | Status | Owner | Rationale | Evidence | Impact |
| --- | --- | --- | --- | --- | --- | --- |
| D10 | Adopt Foundry rather than standalone OpenAI | Confirmed | User | Explicit 2026-11-03 request | Conversation | Target platform changes |
| D11 | Include managed Agent Service orchestration and agent state | Confirmed | User | Exact answer: Also migrate orchestration and agent state to managed Foundry Agent Service | F13/F15 | Requires Cycle 2 on state, tools and runtime |
| D12 | New private injected Standard Foundry account/project with BYO state | Architecture consequence for PRD approval | PRD approver | Selected private managed orchestration requires full Standard topology | F12/F17 | New resource/cost preflight before apply |

### D11-D12: Foundry feature and runtime scope

Historical decision prompt:

- A (recommended): Foundry models/project, private evaluation runs, explicit safety configuration, redacted tracing, presenter assurance view; preserve C# runtime and APIM. Provision a new injected account, retain old model until verified cutover.
- B: include managed Agent Service orchestration too; perform additional runtime/state/tool research before drafting final requirements.

User chose B. Cycle 2 below resolves the additional research; no new provisioning or cost approval is implied.

## Risks and Open Questions

| Priority | Risk | Impact | Smallest required action |
| --- | --- | --- | --- |
| High | Existing account cannot gain outbound injection | Evaluation migration failure | New account creation with injection, reviewed plan |
| High | Current tenant policies/model quota not yet checked for new Foundry topology | Provisioning blocked or duplicate quota unavailable | Authenticated policy gate and exact plan/capacity validation before provisioning |
| High | C# evaluation APIs include preview dependencies | SDK/service mismatch | Pin published versions and run a real synthetic project evaluation through CI/private executor |
| High | Evaluation data sent to linked App Insights | Existing content-export policy regression | Keep runtime spans metadata-only; isolate exact approved synthetic evaluation records; test exporter boundary |
| Medium | Capability-host guidance differs | Stalled evaluation | Explicit readiness check and real evaluation; never accept provisioning alone |
| Medium | Model judge variability/cost | Unreliable gate | Fixed dataset/model/version, bounded run and exact deterministic checks; agree score thresholds in PRD |
| Medium | Service feature rollout changes | Stale assumption | Revalidate cited schemas/availability at implementation/deploy |

## Planning Readiness and Next Step

| Field | Record |
| --- | --- |
| Research disposition | executed |
| Decision participation | user-owned; manual research |
| Planning Readiness | Ready for PRD authoring, not implementation authorization |
| Research depth and helpers | Two completed Wider/Deeper/Contrarian cycles; no helpers |
| Blockers | None for PRD authoring; approval/live gates remain |
| Output mode and planning support | convergence; supports requirements after decision |
| Continuation owner | User and repository PRD gate |
| Required gates | Scope decision; PRD built with HVE prd-builder, human-approved PRD merge, implementation planning, Azure policy/validation and personal release approval |
| Next action | Hand evidence and selected scope to HVE prd-builder; no implementation before human PRD merge |
| Primary evidence file | docs/research/2026-11-03/foundry-migration-research.md; docs root from repository instruction; date from user timestamp |

## Research Record

### Method and Boundaries

Balanced default posture, convergence mode. No helpers or deadlines. Research read-only except evidence and session tracking. Internal candidates: infra/terraform, backend model/gateway, deterministic evaluation and diagnostics. External scope: Microsoft Learn, official Foundry samples and pinned HashiCorp provider docs.

Numbering continues docs/research/2026-10-30/role-aware-copilot-mcp-research.md: C15 onward, W14 onward, F11 onward, D10 onward and Q8 onward. That earlier evidence supplies existing model-input/security boundaries, not Foundry compatibility proof.

Resolved root: docs (trusted evidence root from repository research instruction). Date: 2026-11-03 (current user timestamp). Initial opening used temporary local Q1-Q4/C1-C3/W1-W6 IDs; this synthesis normalizes them to the session continuation IDs above.

### Extensions and Participation

| Kind | Candidate | Selection and authority |
| --- | --- | --- |
| Requirement | PRD research and approval | Docs evidence and human PRD approval before implementation |
| Skill | microsoft-foundry | Selected: platform/network guidance; dependency check passed |
| Skill | azure-aigateway | Selected: preserve private APIM governance |
| Skill | belux-azure-policy-gate | Relevant but not executed for new topology in research; required before provisioning |
| Skill | requirements-author | Deferred until research closes |
| Instruction | Copilot tracking instructions | No matching files found in repository discovery |

Direction changed from rebuilding existing solution to Foundry. No secrets requested. Existing infrastructure operation may finish; standalone release/application creation not advanced. Intake sufficient without preliminary questions; user-owned material scope question follows evidence.

### Research Cycle Log

#### Cycle 2

Active direction: the user selected managed Foundry Agent Service orchestration and agent state, not the smaller lifecycle-only option. Preserve the API as authority for business state, source entitlement, tool admission and human approval. Agent conversation state is distinct from aircraft workflow state. Research these constraints before drafting requirements.

Wider: W27-W29 identify ordinary managed prompt agents, Standard private infrastructure and durable conversations. Reflection: do not confuse hosted code or ephemeral responses with the requested managed orchestration.

Deeper: W30-W33 verify C# functions, client-side execution, new-runtime storage, region support and project-level sharing. C19-C22 establish existing tool guards, bounded execution, synthetic admission and nonpersistent demo sessions. Reflection: managed history must not bypass owner/case/source policy, and restart durability cannot be claimed for the existing in-memory session store.

Contrarian: W34 documents an APIM model connection but contradicts itself on Responses support; W32 shows Copilot harness restrictions incompatible with current bounds. W31 mixes Classic/new container capacities. W35-W36 establish general Responses gateway capability, not turnkey agent proxy or internal inference mediation. The guessed conversation-state and agent-privacy URLs returned 404; W29 supplies actual runtime-state/security guidance. The Terraform sample's guessed `main.tf` returned 404 and authenticated GitHub directory listing was SAML-blocked; no conclusions rely on that sample's unseen content.

Synthesis/re-entry: choose ordinary managed prompt agents, BYO Standard state and private API-to-project gateway routing. Retain the five API-dispatched tools and authoritative business state. Require explicit companion PRD amendments for persistence and per-response rather than invisible internal-model budgets. Avoid managed memory, harness built-ins, remote MCP, BYOM gateway chaining and cross-region features in this release. Exact quota/cost/provider/SDK/service proofs are fail-closed deployment acceptance, not unresolved product scope. Additional research is required only if these boundaries change.

#### Cycle 1

Wider: W14/W15 established upgrade and lifecycle capabilities. Repository discovery revealed existing evaluations and privacy-preserving telemetry. Reflection: determine project/provider schemas and avoid confusing Foundry with mandatory Agent Service.

Deeper: W16-W19 and C15-C18 verified pinned account/project support, evaluation SDK and current boundaries. Reflection: private evaluations require more than a project; challenge migration and runtime assumptions.

Contrarian: W20-W26 establish private evaluation-only alternative, regional exclusions and inability to add outbound injection later. W22 troubleshooting and W23 sample disagree on capability-host necessity; preserve the discrepancy and require real readiness/evaluation proof. A guessed evaluation-approach URL returned 404; W25 evaluation overview supplied the authoritative replacement. No unavailable source is treated as evidence.

Synthesis: accept Foundry resource/project/evaluation viability; reject account-kind-only conversion as sufficient for this target; defer managed-agent runtime and cross-region red teaming. A new cycle is unnecessary for recommended scope once the user selects it. If managed Agent Service is selected, execute another full cycle on runtime/state/tool contracts. Live quota/policy and executable integration are future gates, not facts established by this research.

### Evidence Log

All external sources retrieved 2026-11-03. No helper output used.

| ID | Claim | Source / exact location | Confidence and version |
| --- | --- | --- | --- |
| C15 | OpenAI account; APIM identity/model/correlation guards; separate Prompt Shields route | infra/terraform/main.tf `azurerm_cognitive_account.model`, `gateway_chat_completions`, `content_safety` | High; current main |
| C16 | Existing bounded Agent Framework execution and four-arm evaluator | backend/AirlineDemo.Api/Evaluation/FourArmEvaluationRunner.cs `ProductionEvaluationArmExecutor` | High; current main |
| C17 | Redaction precedes Azure Monitor exporters | backend/AirlineDemo.Api/Diagnostics/DiagnosticsSetup.cs `AddAirlineDiagnostics` | High; current main |
| C18 | Record screening is gateway-mediated with exact bounded states | backend/AirlineDemo.Api/Copilot/CopilotRecordScreener.cs `ScreenAsync` | High; current main |
| W14 | In-place conversion preserves endpoint; private DNS changes | [Upgrade guidance](https://learn.microsoft.com/azure/foundry/how-to/upgrade-azure-openai) | High; updated 2026-08 |
| W15 | Evaluation, tracing and monitoring cover apps/agents | [Observability](https://learn.microsoft.com/azure/foundry/concepts/observability) | High; updated 2026-10 |
| W16 | Private network requirements; outbound injection immutable; portal APIM warning | [Private networking](https://learn.microsoft.com/azure/foundry/how-to/configure-private-link) | High; updated 2026-10 |
| W17 | C# evaluation client/project result support | [Cloud evaluation](https://learn.microsoft.com/azure/foundry/observability/how-to/cloud-evaluation) | High; updated 2026-09 |
| W18 | AIServices upgrade, project management and injection schema | [Pinned account provider](https://raw.githubusercontent.com/hashicorp/terraform-provider-azurerm/v4.81.0/website/docs/r/cognitive_account.html.markdown) | High; 4.81.0 |
| W19 | Project resource and endpoints schema | [Pinned project provider](https://raw.githubusercontent.com/hashicorp/terraform-provider-azurerm/v4.81.0/website/docs/r/cognitive_account_project.html.markdown) | High; 4.81.0 |
| W20 | Sweden evaluation support, hosted red-team/protected-material regional exclusions | [Evaluation regions](https://learn.microsoft.com/azure/foundry/concepts/evaluation-regions-limits-virtual-network) | High; updated 2026-08 |
| W21 | Sweden project support; check feature-specific availability | [Foundry regions](https://learn.microsoft.com/azure/foundry/reference/region-support) | High; updated 2026-08 |
| W22 | Evaluation requires injection/project roles; troubleshooting capability hosts | [Private evaluation requirements](https://learn.microsoft.com/azure/foundry/concepts/evaluation-virtual-network) | High; updated 2026-09 |
| W23 | Evaluation-only private topology, storage/monitoring, no Cosmos/Search | [Official sample](https://github.com/microsoft-foundry/foundry-samples/tree/main/infrastructure/infrastructure-setup-bicep/15a-private-network-evaluation-only-setup); `main.bicep`, `README.md` | High; main retrieved 2026-11-03 |
| W24 | Versioned datasets and precomputed response evaluation | [Dataset evaluation](https://learn.microsoft.com/azure/foundry/observability/how-to/cloud-evaluation-datasets) | High; updated 2026-09 |
| W25 | Evaluation can target existing outputs or application interactions | [Evaluation overview](https://learn.microsoft.com/azure/foundry/concepts/evaluation-overview) | High; updated 2026-10 |
| W26 | Separate submitting, workload and project identities/permissions | [Evaluation permissions](https://learn.microsoft.com/azure/foundry/observability/how-to/evaluation-permissions) | High; updated 2026-09 |
| C19 | Copilot has 30-second/20-tool/8-model-request bounds and API dispatch | backend/AirlineDemo.Api/Copilot/CopilotQuestions.cs `Turn`, `DispatchAsync` | High; current main |
| C20 | Copilot excludes prose and permits only closed structural observations | backend/AirlineDemo.Api/Copilot/CopilotInputContract.cs `Observe`, `SystemInstructions` | High; current main |
| C21 | Investigator has gated synthetic content and verified claims | backend/AirlineDemo.Api/Investigation/BoundedInvestigator.cs `InvestigateAsync`; ApprovedModelManifest.cs `SyntheticContentRegistry` | High; current main |
| C22 | Demo sessions are in memory with sliding 8-hour expiry | backend/AirlineDemo.Api/Auth/DemoSessionStore.cs `Find`, `Create` | High; current main |
| W27 | Ordinary prompt agents provide managed orchestration; hosted/ephemeral differ | [Agent Service overview](https://learn.microsoft.com/azure/foundry/agents/overview) | High; updated 2026-10 |
| W28 | Private Standard setup requires BYO Cosmos, Storage, Search and delegated subnet | [Private agents](https://learn.microsoft.com/azure/foundry/agents/how-to/virtual-networks) | High; updated 2026-10 |
| W29 | Named versioned agents, durable conversation items, C#/REST routes, privacy | [Runtime components](https://learn.microsoft.com/azure/foundry/agents/concepts/runtime-components), Create an agent / Conversations / Security and data handling | High; updated 2026-10 |
| W30 | Function selection is managed; app executes and submits outputs; C# examples | [Function calling](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/function-calling) | High; updated 2026-10 |
| W31 | New/Classic Cosmos containers differ; creation-time capability settings | [Standard setup](https://learn.microsoft.com/azure/foundry/agents/concepts/standard-agent-setup) | High for topology; capacity numbers internally inconsistent |
| W32 | Sweden private agents supported; harness rejects bounded-response settings | [Agent limits and regions](https://learn.microsoft.com/azure/foundry/agents/concepts/limits-quotas-regions) | High; updated 2026-10 |
| W33 | Project is state-sharing/isolation unit; Standard vs Basic | [Environment setup](https://learn.microsoft.com/azure/foundry/agents/environment-setup) | High; updated 2026-08 |
| W34 | APIM MI model connections supported for prompt agents; conflicting Responses wording | [Agent AI gateway](https://learn.microsoft.com/azure/foundry/agents/how-to/ai-gateway), Configure managed identity / Supported configurations | High for stated contradiction; no compatibility conclusion |
| W35 | AI gateway governs OpenAI Responses endpoints | [APIM AI gateway](https://learn.microsoft.com/azure/api-management/genai-gateway-capabilities) | High; updated 2026-06 |
| W36 | APIM can use MI for Foundry backend endpoints | [Import Foundry API](https://learn.microsoft.com/azure/api-management/azure-ai-foundry-api) | High for inference; project proxy must be live verified |

### Artifact Self-Check

User-facing synthesis, selected managed-agent scope, alternatives, risks, evidence, two ordered research cycles and source limits recorded. Every material finding has evidence state and confidence. No user scope question remains; material requirement decisions are proposed for human PRD approval. No implementation or successful Foundry deployment is claimed.
