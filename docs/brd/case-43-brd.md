# Case Study 43 - Business Requirements Document

## Document control

| Field | Value |
| --- | --- |
| Programme | Altivane Aircraft Return Readiness and Asset Management |
| Version | 0.2.3 |
| Edition | Public-safe sanitized reference copy; existing BRD identifiers and intent are retained except for privacy/safety redactions and generalized deployment prerequisites. |
| Date | 12 October 2026 |
| Prepared by | Business analysis role |
| Status | Draft; unapproved; production requirements remain proposed |
| Delivery purpose | Bounded capstone reference implementation using GitHub, Azure, AI and Microsoft Agent Framework |
| Capstone milestone | Demonstration and repository readiness; not production acceptance |
| Business outcome anchor | Contractual return acceptance |
| Initial operational scope (future programme) | Records readiness, reserves reconciliation and contract-obligation handling |

Altivane Aviation Capital is an entirely fictional aircraft lessor. Case-study figures are scenario stipulations, not observations about a real customer. No customer access, operational results or business stakeholder approval is asserted.

This BRD connects a credible business story to a bounded capstone deliverable and preserves the operating boundaries and evidence needed for any future investment decision. It is not a production implementation specification, approved budget or authorisation to provision infrastructure, access real systems or contact counterparties. A working Azure deployment is a delivery objective; provisioning and expenditure still require explicit approval. Capstone demonstrations remain synthetic and use a mock outbound sink.

This public-safe reference edition retains the existing BRD identifiers and their intended meaning. It omits personal attribution, internal review history, private source links and deployment-specific configuration; deployment prerequisites are generalized below. No review, approval, sign-off, implementation or production readiness is asserted.

## 1. Executive summary

Altivane needs to establish aircraft-return readiness early enough to resolve critical evidence gaps before handover. Fragmented technical records, lessee reporting and lease obligations make it difficult to distinguish genuine deficiencies from incomplete, conflicting or unprocessed evidence. Late discovery creates rework, uncertain acceptance dates and potential financial exposure.

The programme will secure authorised pre-return evidence access and introduce a shared, evidence-backed process for identifying gaps, assigning resolution ownership and coordinating follow-up. Technical, financial and legal accountability remains with authorised people.

The initial operational pilot will cover a bounded cohort of upcoming returns across several lessees. Records, reserves and contracts enter the pilot together. Controlled routine evidence requests may operate from the start, after the pilot-entry gate; a mandatory shadow phase is not required.

Expansion follows staged evidence gates. Leading indicators and effective controls may support limited expansion. Broader rollout requires completed-return results against matched historical returns, credible net benefits and sustainable operating capacity.

Those paragraphs describe the fictional customer's proposed future programme, not an approved delivery commitment. The capstone is intended to deliver a working, reproducible GitHub solution, deployed on Azure subject to approved deployment: a deep records-readiness journey with bounded AI investigation using Microsoft Agent Framework, supported by thin working reserves-calculation and notice-preparation consumers. Production rollout, real counterparties, fleet-scale operation and validated commercial benefits are not capstone completion criteria. The demonstration must explain how its outputs support the business objective without claiming that it has achieved real acceptance outcomes.

## 2. Business context and problem

The case stipulates an Irish lessor managing 1,200 aircraft with 150 airline lessees globally. Technical records can comprise hundreds of thousands of pages per aircraft. Returns require coordination of records, maintenance conditions, contractual obligations, financial reconciliation and counterparties.

The programme addresses three connected business problems:

- Critical evidence gaps are found too late to support timely corrective action.
- Teams lack a shared view of the evidence basis, outstanding issues, resolution owners and permitted next actions.
- Management cannot reliably distinguish readiness uncertainty, contractual acceptance risk and financial exposure.

Records can directly block acceptance or reveal physical work too late to secure parts or maintenance capacity. Faster records review does not necessarily shorten a return if another dependency dominates the critical path.

The following clocks remain distinct: pre-return preparation, contractual return acceptance, the interval between outgoing and incoming rent streams, and the next operator's entry into service. Progress in one must not be reported as proof of progress in another.

## 3. Working decision register

D-01 through D-09 record the working direction represented by this draft. They do not constitute formal organisational approval. The `D-`, `O-` (open items) and `G-` (gates) prefixes are informal register labels, not requirement identifiers; they are not migrated to the three-digit requirement scheme.

| ID | Agreed decision |
| --- | --- |
| D-01 | Contractual return acceptance anchors the primary transition-predictability measure. |
| D-02 | Securing early authorised access to lessee technical records is within programme business scope, rather than assumed to be already satisfied. |
| D-03 | The initial business pilot covers upcoming returns across several lessees, not a single-lessee or whole-portfolio rollout. |
| D-04 | Records readiness, reserves reconciliation and contract-obligation handling enter the initial operational pilot. |
| D-05 | Expansion follows staged evidence gates: limited expansion may use leading indicators and controls; broader rollout requires completed-return outcomes and validated economics. |
| D-06 | Matched historical returns underpin the business-impact assessment. |
| D-07 | Controlled action operates from the start of the pilot after entry requirements are met; no mandatory shadow phase is imposed. |
| D-08 | The deliverable is a bounded capstone demonstration, not a complete production-ready solution. The fictional business story and production gaps remain explicit. |
| D-09 | Deliver a working GitHub reference implementation intended for Azure, with meaningful AI and Microsoft Agent Framework behaviour. Preserve the existing deep-records/thin-consumers demonstration boundary. This is not deployment authorization. |

## 4. Objectives and success measures

The primary objective is more predictable contractual return acceptance through earlier identification and resolution of critical gaps. Processing speed is a supporting measure, not the business outcome by itself.

All operational measures below are proposed. Baselines, targets, reporting periods and decision thresholds must be agreed before operational results are assessed. They are not prerequisites for completing a synthetic capstone demonstration.

| Outcome | Proposed measure | Measurement boundary |
| --- | --- | --- |
| Predictable acceptance | Percentage of completed returns accepted by the agreed baseline date | Preserve the original baseline, subsequent agreed revisions and reasons. Open returns are reported separately, not counted as completed successes. |
| Less severe overruns | Days late for returns that miss the baseline date | Report the distribution and significant overruns, not only an average. |
| Earlier intervention | Lead time between identifying a critical gap and planned acceptance | Agree what constitutes a critical gap and use recorded discovery timestamps. |
| Better forecasts | Difference between forecast and actual acceptance dates | Retain timestamped forecasts at agreed checkpoints; do not retrospectively replace them. |
| Effective resolution | Resolution time, overdue issues and unresolved critical issues | Show ownership and distinguish evidence, physical-work and contractual dependencies. |
| Lower review effort | Recorded staff effort per comparable evidence package or return | Distinguish staff capacity released from cashable savings. |
| More reliable reserves | Reconciliation exceptions, collection performance and attributable recoveries | Separate correct calculation, invoice approval, cash collection and profit impact. |
| Timely contract handling | Obligations identified before their deadlines and legally approved notices handled on time | Draft preparation is not legal sign-off or delivery of a valid formal notice. |
| Safe, sustainable operation | Unauthorised actions, access incidents, review workload and cost per transition prepared | Safety failures cannot be offset by commercial improvement. |

The case's 40% transition-time reduction and 70% records-review effort reduction remain unvalidated ambitions. They are not approved pilot thresholds. A duration reduction does not by itself establish improved predictability.

### 4.1 Identified goals and evidence boundaries

The identifiers below support traceability, not invented approval. Every BG is a capstone goal with a decidable target at the capstone readiness milestone; each target is met only when the named acceptance and NFR checks have recorded passing evidence; section 11.1 remains the completeness gate requiring every capstone AC. Real business baselines, targets and windows are not BRD goals; they sit in the future-programme register in section 4.2. The existing demo PRD proposes repository readiness around 18 November 2026; this is a planning date, not a confirmed submission deadline. Current implementation baselines are unverified, not presumed zero.

| ID | Goal | Measure and target | Baseline / measurement source | Owner and timeframe | Status |
| --- | --- | --- | --- | --- | --- |
| BG-001 | Show earlier, evidence-backed gap identification and resolution on the synthetic return case. | All AC-002, AC-005 and AC-014 checks pass: seeded gaps are identified with evidence, routed to the correct owner and reassessed on new evidence, and progress is visible. | Versioned fixtures, AC results and business audit history; current results unverified. | Capstone delivery owner; capstone readiness milestone. | Capstone goal; illustrates, does not prove, BRD section 4 outcomes. |
| BG-002 | Show that people review only genuine exceptions while routine requests stay within policy. | All AC-002 and AC-006 checks pass: the eligible routine request is dispatched to the mock inbox without per-request approval, and ambiguous or blocked items reach human review with no dispatch. | Fixture expectations, policy decisions and review records; current results unverified. | Capstone delivery owner; capstone readiness milestone. | Capstone goal; no effort-saving claim. |
| BG-003 | Show a correct, reviewable reserve draft. | All AC-003 checks pass: synthetic reserve results match the fixture, input versions are recorded, missing or stale input blocks calculation and simulated approval issues no invoice. | Versioned rates, utilisation fixtures and review records; current results unverified. | Capstone delivery owner; capstone readiness milestone. | Capstone goal; no collection or profit claim. |
| BG-004 | Preserve authority and evidence integrity throughout the demonstrated journey. | All AC-001, AC-004, AC-005, AC-006, AC-007, AC-012, AC-015 and AC-016 checks and the NFR-001, NFR-002 and NFR-008 checks pass; no forbidden action in the recorded readiness runs. | Synthetic fixtures, policy decisions, review records and action/audit traces; current implementation status unverified. | Capstone delivery owner as demo delivery owner; capstone readiness milestone. | Capstone criterion; not production assurance. |
| BG-005 | Deliver a reproducible working Azure reference implementation in GitHub. | All AC-011 (a)-(c) and NFR-003 checks pass: the repository contains working code, synthetic fixtures, deployment/setup instructions, architecture and limitations, and the documented deployment and smoke test succeed in the approved Azure environment. | Repository revision, deployment record and smoke-test evidence; delivery baseline unverified. | Capstone delivery owner; capstone readiness milestone. | Capstone delivery objective. |
| BG-006 | Demonstrate and explain meaningful AI/Agent Framework value in the business journey. | All AC-008 and AC-012 checks pass, and the selected arm meets the PRD:AC-016 evaluation thresholds on the frozen synthetic set with zero forbidden actions; AC-013 rehearsal outcomes are recorded. | Recorded demonstration, exported traces, expected fixture outcomes and evaluation results; current results unverified. | Capstone delivery owner; capstone readiness milestone. | Capstone delivery objective; no commercial-benefit claim. |
| BG-007 | Show timely, evidence-linked contract handling without formal notice dispatch. | All AC-004 checks pass: an eligible obligation produces one evidence-linked notice draft with simulated legal review, ambiguous evidence does not establish breach and nothing is sent. | Versioned obligation fixtures and review records; current results unverified. | Capstone delivery owner; capstone readiness milestone. | Capstone goal; no legal-compliance claim. |

### 4.2 Future-programme outcome register

The section 4 outcomes are future-programme aspirations outside this BRD's gated goal set. Their baselines, targets and windows are set by the owners below before operational results are assessed; until then they remain unvalidated and are not capstone completion criteria.

| Future outcome | Proposed owner | Baseline and target set under | Target phase |
| --- | --- | --- | --- |
| Predictable acceptance, less severe overruns, earlier intervention, better forecasts | Technical operations | O-05, O-06 | Before G-01 pilot entry |
| Effective resolution and lower review effort | Technical operations | O-05, O-06 | Before G-01 pilot entry |
| More reliable reserves | Finance | O-05, O-06, O-08 | Before G-01 pilot entry |
| Timely contract handling | Legal | O-05, O-06 | Before G-01 pilot entry |
| Safe, sustainable operation | Risk/security with service owner | O-06, O-09, O-11 | Before G-01 pilot entry |
| Credit and sanctions risk exposure (later-phase outcome; not in the section 4 measures table) | CRO | O-12 | Before O-12 progression criteria admit credit/sanctions to operational use, with G-01-equivalent entry evidence |

## 5. Programme and pilot scope

### 5.1 Target business scope

All five case-study capabilities remain in the programme.

| Capability | Initial operational pilot | Target business responsibility |
| --- | --- | --- |
| Records readiness | In scope | Structure and assess evidence, expose uncertainty, coordinate permitted follow-up and support authorised acceptance. |
| Utilisation and reserves | In scope | Reconcile reports, calculate reserves from applicable lease terms and support finance review. |
| Contract compliance | In scope | Track obligations and deadlines, relate evidence to obligations and prepare notices for legal approval. |
| Transition planning | Existing processes continue initially | Recommend return plans and coordinate records, maintenance and other dependencies; consequential execution remains authorised by people. |
| Credit and sanctions monitoring | Existing processes continue initially | Use appropriate authoritative feeds to identify exposure and route possible matches or risk signals to specialists. |

The programme augments existing records, maintenance and finance platforms. Replacement of those systems is not part of the initial pilot. Build-versus-buy choices remain architectural and procurement decisions; this BRD does not require bespoke implementations of commodity capabilities.

### 5.2 Pilot cohort

Select a bounded set of approaching returns across several airline counterparties. Include variation in record quality, lease conditions, return complexity and responsiveness rather than only cooperative counterparties and clean records.

Selection rules, cohort size, intervention lead time and duration remain open. The planning documents' twelve-month preparation window is a scenario assumption, not a universal access right or an agreed BRD requirement.

Access failures and poor-quality submissions remain visible in onboarding and pilot reporting. Evidence rights and operational permissions must be established before activating a participating case; selecting it does not grant access.

### 5.3 Included enabling work

The programme includes establishing access rights and permitted use, agreeing initial evidence packages and updates, identifying focal points, arranging secure exchange, defining escalation routes, establishing baselines, onboarding operational users and assigning ongoing service ownership.

The lessor cannot assume unilateral rights to airline or MRO records, direct command authority over MROs, live partner APIs, common records standards or partner AI agents.

### 5.4 Exclusions and boundaries

The initial pilot does not provide autonomous airworthiness certification, aircraft release, contractual acceptance, invoice approval, legal sign-off, sanctions determinations, maintenance bookings, ferry-flight execution or Cape Town filings. Existing authorised processes may act on approved outputs; connecting or automating execution requires separately agreed scope and controls.

A routine administrative evidence request is not a formal contractual notice, breach finding or allegation of noncompliance. Records readiness alone does not establish physical readiness or contractual acceptance.

### 5.5 Capstone boundary

The capstone deliverable is a bounded working demonstration and reference repository, not a complete production platform or execution of G-01 through G-03. The companion [Aircraft Return Readiness PRD](../prd/aircraft-return-readiness.md) provides the intended implementation scope; this BRD does not claim implementation or deployment is complete.

The delivery boundary is:

- Implement the records-readiness journey in depth, including clear evidence gaps, ambiguous identity, internal review, controlled mock requests, response ingestion and reassessment.
- Implement reserves calculation and contract-notice preparation as thin working consumers of the shared evidence/state model. Neither issues real invoices or notices.
- Deploy the working application to an explicitly approved Azure environment and document how to reproduce, seed, run and remove the deployment. Do not expand the programme to full-fleet integration.
- Use AI and Microsoft Agent Framework meaningfully in bounded records investigation, with observable tool use and source-linked outputs. Preserve the PRD's deterministic authority/action boundary; a scripted trace is not evidence of agent behaviour.
- Provide synthetic fixtures, evaluation evidence, a live demonstration and recorded fallback, architecture rationale, known limitations and the business narrative in the repository/presentation.
- Explain transition planning and credit/sanctions as future capabilities; do not implement autonomous bookings, sanctions determinations or real partner integration for the capstone.

Any eventual deployment requires organization-approved identity, network and security prerequisites, plus applicable authorization for provisioning and spending. None are approved by this BRD. Tenant-specific configuration and deployment details are intentionally omitted from this public-safe reference; the companion PRD contains the implementation scope.

Synthetic demonstration results cannot establish airline cooperation, production accuracy, legal compliance, operational acceptance outcomes or financial benefit. The demonstrator does not issue real invoices or formal notices. Its infrastructure budget and preparation capacity are not business-programme budget assumptions.

Production availability commitments, disaster recovery, full operational staffing, customer signoff, real access agreements and fleet-scale testing are deferred to an operational programme. Basic access controls, secret handling, safe action boundaries, visible failures and reproducible demo recovery are not deferred.

## 6. Stakeholders and accountability

The fictional case assigns executive sponsorship to the Chief Technical Officer, with the Chief Risk Officer as co-sponsor. The allocations below are proposed for validation; named delegates and approval limits remain open.

| Stakeholder | Proposed accountability |
| --- | --- |
| CTO | Programme sponsorship, technical operating model and recommendation to proceed through gates. |
| CRO | Risk appetite, control expectations and future credit/sanctions capability. |
| CFO and finance | Validate economics, reserve rules and financial reporting treatment; approve invoice release. |
| Technical directors and licensed staff | Define applicable technical evidence requirements and retain accountability for airworthiness-relevant conclusions. |
| Authorised return-acceptance delegate(s) | Record contractual acceptance under the lessor's approved delegation, with required technical and legal input. Technical approval alone must not imply contractual authority. |
| Legal | Validate access rights, contractual obligations, notice requirements and legal sign-off. |
| Technical operations / programme delivery owner | Coordinate readiness issues, airline focal points, escalations and operating procedures; exact ownership remains to be assigned. |
| Airlines and MROs | Provide authorised evidence and coordinate agreed follow-up within their own responsibilities. |
| Compliance, privacy and information security | Validate handling, access, assurance and incident procedures. |
| Platform/service owner | Own support, recovery, monitoring and ongoing operating capacity; named ownership remains open. |

The capstone delivery owner is not an operational approver for the fictional lessor.

For the capstone, the capstone delivery owner is accountable for delivery scope, repository readiness, demonstration preparation and disclosure of limitations. Simulated technical/finance/legal roles exercise review paths without representing genuine stakeholder approval. Any capstone evaluation is separate from fictional-customer acceptance; no evaluation or sign-off result is asserted.

## 7. Functional business requirements

Functional requirements use `FR`, acceptance criteria `AC`, non-functional requirements `NFR`, business rules `BR`, constraints `CON` and goals `BG`, with at least three numeric digits. Identifiers from the demo PRD are written with a `PRD:` prefix (for example `PRD:AC-016`); unprefixed identifiers are BRD identifiers. Requirements describe intended behaviour, not verified implementation. For the capstone, "Must" applies only to the stated demo extent. Future programme requirements remain proposed and are not silently promoted into capstone obligations.

| ID | Requirement | Capstone extent / priority | Future programme extent |
| --- | --- | --- | --- |
| FR-001 | Establish contract-specific evidence access and expose limitations. | Must: simulate permissions, inventory and submission status for synthetic cases. | Establish actual rights, exchange arrangements, schedules, focal points and escalation before activating leases. |
| FR-002 | Assess technical-record readiness and expose actionable gaps. | Must: deep records journey with evidence-linked findings and ambiguity/processing states. | Apply qualified-owner-approved checklists and coordinate operational resolution. |
| FR-003 | Reconcile utilisation and calculate maintenance reserves. | Must: thin deterministic consumer with a draft and discrepancies. | Validate lease terms/rates and require finance approval before any invoice release. |
| FR-004 | Track lease obligations and prepare notice drafts. | Must: thin consumer with evidence-linked draft and simulated legal review. | Validate interpretations and notice rules; require legal signoff before actual dispatch. |
| FR-005 | Maintain version-bound evidence and decision history. | Must: record the basis and reassess on new evidence without erasing history. | Apply operational approval/delegation and records-retention rules. |
| FR-006 | Dispatch only permitted routine evidence requests. | Must: deterministic checks and duplicate suppression; mock inbox only. | Establish real recipients, templates, action limits and delivery reconciliation. |
| FR-007 | Distinguish readiness, review and accountability states. | Must: distinguish records, finance, contractual and review states for the synthetic case, so records readiness is never shown as finance approval or contractual acceptance. | Support return/cohort ownership, deadlines and external dependency reporting. |
| FR-008 | Capture measurement and control evidence. | Must: capture demo/evaluation events, exceptions, timing and cost with synthetic labels. | Establish matched historical comparisons, staff effort and actual acceptance/benefit measures. |
| FR-009 | Support transition-planning recommendations. | Will not implement: represented only as a read-only design-only card in the FR-014 view (verified by AC-016) and in design material. | Later phase: expose assumptions and owners; people authorise consequential execution. |
| FR-010 | Support credit and sanctions monitoring. | Will not implement: represented only as a read-only design-only card in the FR-014 view (verified by AC-016) and in design material explaining separate authoritative feeds and specialist resolution. | Later phase: provenance/freshness-aware signals, not autonomous risk clearance. |
| FR-011 | Provide a reproducible reference deployment that satisfies CON-002. | Must: (a) code, fixtures and setup/deployment instructions in GitHub that produce a working deployment and passing smoke test in an approved Azure environment; (b) a README; (c) a publication-safe repository with no credentials or real data. Each sub-item is verified by the matching AC-011 clause. | Production deployment/support model requires separate approval. |
| FR-012 | Perform bounded agentic records investigation that satisfies CON-002's Agent Framework mandate. | Must: case-scoped read-only tools, evidence citations and a real investigation trace. | Any wider agent authority or autonomy requires separately evaluated scope. |
| FR-013 | Provide an executable demonstration and its evidence package. | Must: (a) both records paths, (b) both thin consumers, (c) evaluation results, (d) live run and recorded fallback. Sub-items (a)-(c) are verified by the matching AC-013 clause and (d) by AC-013 (d) and (e). | No synthetic result substitutes for operational assurance or outcome evidence. |
| FR-014 | Provide an operating view of the case across capabilities. | Must: show what each capability is progressing, the recorded agent, rules-based, simulated and human activity, and the decisions that remain with the signed-in person's role. | Cohort-level operating view with external dependencies and service ownership. |
| FR-015 | Label each capability and activity truthfully. | Must: label each capability, work item and activity as AI agent, rules-based, simulated or design-only from backend state, using the AC-015 mapping. Rules-based or simulated work is never labelled AI agent, and design-only capabilities never appear as working. | Labels updated as capabilities are validated and released. |

### 7.1 Separately identified acceptance criteria

The following criteria define observable capstone checks. They do not certify production readiness. Future-only criteria state the intended behaviour for later validation and are excluded from the executable capstone checklist.

| ID | Criterion | Verification scope |
| --- | --- | --- |
| AC-001 | Given a synthetic case with configured lease/recipient scope, access exposes only that scope; absent inventory or processing coverage is visible and is not represented as evidence of absence. | Capstone fixture check; actual access agreements deferred. |
| AC-002 | Given complete, missing-history, ambiguous-identity and blocked-processing fixtures, findings match the documented expected states, cite the relevant evidence or coverage basis and route ambiguity to internal review without declaring acceptance. | Capstone records checks. |
| AC-003 | Given versioned synthetic rates and utilisation, the draft reserve calculation matches the fixture's expected result and records input versions; missing/stale utilisation blocks calculation rather than becoming zero; simulated approval issues no invoice. | Capstone thin consumer checks. |
| AC-004 | Given a versioned synthetic obligation and an eligible processed finding, the notice draft links the basis and records simulated legal review; ambiguous/unprocessed evidence does not establish breach or create a formal notice; no notice is sent. | Capstone thin consumer checks. |
| AC-005 | Given new relevant evidence, reassessment records the new basis and retains the previous finding/decision history; affected approvals are marked for review rather than silently reused or contractual acceptance automatically reversed. | Capstone version-change check. |
| AC-006 | Given an eligible routine request, deterministic policy records its permitted basis and sends only to the mock inbox; an equivalent repeated evaluation produces no duplicate; ambiguous identity, incomplete processing and forbidden recipients produce no dispatch. | Capstone positive and negative action checks. |
| AC-007 | Given the headline records paths and the reserve and notice drafts, when a reviewer opens the case, then the reviewer view exposes finding state, evidence basis, owner/review task and action state; records readiness is not displayed as finance approval or contractual acceptance; and reserve and notice draft or decision states are shown separately from invoice issuance and notice dispatch, which do not occur. | Capstone walkthrough. |
| AC-008 | Given a demo/evaluation run, recorded events support elapsed-time, model/tool-cost and control-exception reporting; unavailable measurements are labelled missing and all outcome/benefit claims distinguish synthetic evidence from operational results. | Capstone evidence export. |
| AC-009 | Future transition recommendations identify records, maintenance, scheduling and partner dependencies with assumptions and accountable owners; consequential execution requires human authorisation. | Future programme; design explanation only for capstone. |
| AC-010 | Future credit/sanctions findings expose authoritative feed provenance, freshness and scope and route potential matches to specialists; no signal is not a clearance. | Future programme; design explanation only for capstone. |
| AC-011 | From the documented repository revision: (a) code, synthetic fixtures and setup/deployment instructions are present in GitHub, the deployment and seeding instructions run in an organization-approved Azure environment and the documented end-to-end smoke test completes, with revision and deployment evidence recorded; (b) the README covers purpose, architecture, prerequisites, run/reset/removal steps and limitations; (c) no credentials or real customer data are committed. | Capstone repository/deployment check; each clause passes or fails separately. Organization-approved identity, network and security prerequisites are required; none are approved by this BRD. |
| AC-012 | A real run invokes Microsoft Agent Framework investigation with case-scoped read-only tools and evidence-linked output; its exported trace identifies tool calls and citation checks. Deterministic policy, not model output, decides external actions. Unavailable model/tool execution is reported rather than replaced with a fabricated successful trace. | Capstone agent trace and boundary check. |
| AC-013 | The readiness package includes: (a) working missing-history and ambiguous-identity paths; (b) both thin consumers; (c) synthetic evaluation results; (d) the recorded fallback; (e) all outcomes of the PRD's proposed five consecutive headline-path rehearsals, with no forbidden action. Fixture scope, model limits and timing/cost observations are disclosed. | Capstone rehearsal criterion; each clause passes or fails separately; finite checks are not a production accuracy claim. |
| AC-014 | Given the seeded synthetic case, when a person opens the operating view, then (a) each implemented capability shows its current work items with state, owner and next human decision owner; (b) an ordered timeline shows recorded agent, rules-based, simulated and human actions with actor and basis version; (c) the decision queue lists only the decisions the signed-in role may take; and (d) taking a decision updates the queue and timeline from recorded state. | Capstone walkthrough and API comparison; each clause passes or fails separately. |
| AC-015 | Given the seeded synthetic case, when the operating view is displayed, then every capability, work item and activity shows exactly one label from the backend: records investigation is AI agent; policy checks, request dispatch, reserves calculation, obligation status and notice drafting are rules-based; the mock partner inbox, its responses and the simulated reviewer roles are simulated; transition planning, credit/sanctions, the portfolio supervisor, invoice issuance and formal notice dispatch are design-only. No rules-based or simulated item is labelled AI agent, and a missing or unrecognised label is shown as unknown, never upgraded. | Capstone label check, including a tampered-label negative test. |
| AC-016 | Given the operating view, when a design-only capability is shown, then it appears as a read-only card that explains its purpose, intended autonomy and human authority and is marked as not implemented, with no work items, counts or actions. | Capstone walkthrough; covers FR-009 and FR-010 representation. |

### 7.2 Traceability matrix

#### Functional requirements to acceptance criteria

| Source ID | Source title | Target ID(s) | Coverage |
| --- | --- | --- | --- |
| FR-001 | Evidence access | AC-001 | 1 |
| FR-002 | Records readiness | AC-002 | 1 |
| FR-003 | Reserves | AC-003 | 1 |
| FR-004 | Notice preparation | AC-004 | 1 |
| FR-005 | Evidence history | AC-005 | 1 |
| FR-006 | Routine requests | AC-006 | 1 |
| FR-007 | Readiness states | AC-007 | 1 |
| FR-008 | Measurement | AC-008 | 1 |
| FR-009 | Transition planning | AC-009, AC-016 | 2 |
| FR-010 | Credit/sanctions | AC-010, AC-016 | 2 |
| FR-011 | Repository/deployment | AC-011 | 1 |
| FR-012 | AI investigation | AC-012 | 1 |
| FR-013 | Demonstration evidence | AC-013 | 1 |
| FR-014 | Operating view | AC-014 | 1 |
| FR-015 | Capability labelling | AC-015, AC-016 | 2 |

#### Functional requirements to goals

| Source ID | Source title | Target ID(s) | Coverage |
| --- | --- | --- | --- |
| FR-001 | Evidence access | BG-001, BG-004 | 2 |
| FR-002 | Records readiness | BG-001, BG-002, BG-006 | 3 |
| FR-003 | Reserves | BG-003, BG-006 | 2 |
| FR-004 | Notice preparation | BG-004, BG-006, BG-007 | 3 |
| FR-005 | Evidence history | BG-001, BG-004 | 2 |
| FR-006 | Routine requests | BG-002, BG-004 | 2 |
| FR-007 | Readiness states | BG-001, BG-004 | 2 |
| FR-008 | Measurement | BG-005, BG-006 | 2 |
| FR-009 | Transition planning | BG-004 (truthful design-only labelling only) | 1 |
| FR-010 | Credit/sanctions | BG-004 (truthful design-only labelling only) | 1 |
| FR-011 | Repository/deployment | BG-005 | 1 |
| FR-012 | AI investigation | BG-004, BG-006 | 2 |
| FR-013 | Demonstration evidence | BG-004, BG-005, BG-006 | 3 |
| FR-014 | Operating view | BG-001, BG-002 | 2 |
| FR-015 | Capability labelling | BG-004, BG-006 | 2 |

Structural FR-to-AC and FR-to-BG linkage is 15/15 (100%) in each matrix. This measures documented links, not passing tests or approved requirements. Thirteen FRs have executable capstone criteria. FR-009 and FR-010 remain future scope; their only capstone check is the design-only card in AC-016, and their BG-004 link covers truthful labelling only. The CRO-owned risk-exposure outcome sits in the section 4.2 register.

#### Identifier migration

The former functional rows `BR-01` through `BR-10` map respectively to `FR-001` through `FR-010`; their meaning is preserved with an explicit capstone extent. In older material, a two-digit `BR-0x` label means a functional requirement (FR), not a business rule. Former `NFR-01` through `NFR-08` map respectively to `NFR-001` through `NFR-008`. The new three-digit `BR` identifiers below denote actual business rules, not those legacy functional rows. Other documents are not renumbered by this revision; use this mapping when updating dependent material.

## 8. Controlled-action policy and exception handling

Controlled action begins at pilot launch after the entry gate. This removes the requirement for an operational shadow phase, not the requirement to establish authority and evaluate controls before activation.

| Situation | Required business response |
| --- | --- |
| Routine request meets all approved conditions | Dispatch within the permitted case, recipient, template and action limits; retain a delivery and decision record. No per-request human approval is required within that envelope. |
| Identity or evidence is ambiguous or conflicting | Route to internal review before external contact. |
| Evidence cannot be processed or coverage is incomplete | Record an exception or unknown state; do not interpret it as proof of missing evidence. An approved manual follow-up process remains available. |
| Retrieval finds no result | Establish inventory and processing coverage before concluding evidence was not located; a search miss alone is insufficient. |
| A request or submission is repeated | Reuse the existing equivalent action/case where appropriate; prevent duplicate dispatch. A new assessment alone does not justify another request. |
| Delivery is uncertain | Expose the uncertain state and reconcile it; do not blindly resend. |
| New evidence changes the basis | Reassess affected findings and refer affected approvals to their authorised owners. |
| Finance or legal action is consequential | Require the relevant approval and preserve separation from routine administrative requests. |
| Unauthorised action or disclosure occurs, or control effectiveness is uncertain | Proposed stop rule: suspend affected automated actions, preserve evidence, invoke incident handling and require authorised reactivation. |

Action limits, escalation response times, suspension authority and reactivation criteria must be agreed before launch. Stopping automation must not hide existing issues or prevent authorised manual operations.

For the capstone, "dispatch" means delivery to the mock inbox. Fixture policies define the permitted envelope and simulated reviewer roles. This does not confer operational authority or satisfy a future pilot-entry gate.

### 8.1 Business rules and enforcing requirements

| ID | Business rule | Enforcing FR(s) |
| --- | --- | --- |
| BR-001 | Evidence assessment and draft outputs do not constitute technical, financial, legal or contractual approval. | FR-002, FR-003, FR-004, FR-007, FR-014 |
| BR-002 | Ambiguous identity or unprocessed/incomplete coverage cannot justify automatic external contact or a breach finding. | FR-002, FR-004, FR-006, FR-012 |
| BR-003 | Only deterministic policy may permit routine requests within the configured envelope; equivalent active requests must not be duplicated. | FR-006, FR-012 |
| BR-004 | New material evidence invalidates reuse of an outdated approval basis until the responsible review path resolves it; history is retained. | FR-005 |

### 8.2 Delivery constraints

| ID | Constraint | Category | Imposing source, change authority and affected boundary |
| --- | --- | --- | --- |
| CON-001 | The capstone uses synthetic data and mock partner delivery only; no real invoices, formal notices, aircraft release or sanctions decisions. | Data/action scope | D-08 and the fictional-case boundary (Altivane is fictional; no customer access is asserted). Non-negotiable for the demonstrator; the demo PRD implements it. |
| CON-002 | The delivered demonstrator must run on Azure, be maintained in GitHub and use AI with Microsoft Agent Framework. | Platform | Working capstone direction represented in this draft. This constraint is not authorization to procure, provision or deploy. |
| CON-003 | Any eventual deployment requires organization-approved identity, network and security prerequisites, and applicable authorization for provisioning, access and publication; none are approved by this BRD. | Governance | Organization deployment, system-access and publication authorities; this BRD grants no permission. |

## 9. Non-functional and assurance requirements

These requirements prescribe observable qualities, not additional Azure services. Demo checks are limited to the documented synthetic suite; passing them does not establish a production SLA, compliance or full-fleet capacity.

| ID | Requirement | Capstone verification | Deferred operational obligation |
| --- | --- | --- | --- |
| NFR-001 | Enforce configured access scope server-side. | Positive/negative role and case-scope checks deny out-of-scope reads/actions; a UI role selector or run identifier cannot grant authority. | Actual counterparties, access-transfer policy and production identity lifecycle. |
| NFR-002 | Preserve reconstructable evidence/action provenance. | Each exercised finding, draft, review and mock action records actor, evidence/rule versions and subsequent changes; inspect the exported business audit record, not just diagnostic logs. | Retention, completeness and audit assurance at operational scale. |
| NFR-003 | Exclude real personal/customer data and committed secrets. | Inspect supplied fixtures and repository for synthetic-only data and credential handling; document configuration and access prerequisites without secret values. | Qualified-owner-approved retention, deletion, legal holds, cross-border processing and privacy controls. |
| NFR-004 | Expose failure and recover without duplicate actions. | Exercise blocked processing and retry/restart paths; failures remain visible and an equivalent request is not sent twice. Document reset/recovery and recorded fallback. | Production uptime, RTO/RPO, service support and manual continuity commitments. |
| NFR-005 | Measure demo responsiveness rather than claim production throughput. | Meet the PRD:AC-012 timing (outcome within 20 seconds; live segment within 90 seconds or a transparent switch to the recorded fallback) and record actual run/model/tool timing under PRD:NFR-005. Disclose timeout/fallback behaviour and incomplete runs. | Business-window, latency, throughput and reviewer-capacity targets for an operational cohort. |
| NFR-006 | Check correctness against explicit synthetic expectations. | Version expected fixture outcomes; check reserve arithmetic, false/missed gaps, ambiguity, blocked processing and source citations. All defined forbidden-action checks must pass; quality/latency/cost trade-offs remain visible. | Owner-approved operational checklists/rates, representative datasets and production accuracy thresholds. |
| NFR-007 | Report deployment and processing cost against the approved estimate. | Document an itemised costed estimate for the approved configuration (O-14) and measure demo/evaluation usage against it; distinguish estimates from actuals, disclose and explain any variance, and provide resource-removal instructions. No numerical cost ceiling currently exists, and spending needs separate approval. | Migration/full-fleet capacity, programme economics and validated cost per operational transition. |
| NFR-008 | Prevent source/model content from changing authority. | Negative fixtures attempt recipient/policy override; deterministic policy rejects it. Record model, rule and fixture versions and rerun affected checks after material changes. | Formal change approval, operational release controls and ongoing assurance. |
| NFR-009 | Make the review and operating views usable without sight of colour or a mouse. | Pass the PRD:NFR-009 bounded checks on the evidence review and operating views: keyboard-only operation without traps, visible focus, labelled controls, capability labels and status shown as text rather than icon or colour alone, and a screen-reader smoke check including queue and timeline updates. | Formal accessibility conformance target for an operational service (see PRD:OQ-007). |

Compatibility, maintainability and portability should be addressed proportionately in the PRD and repository documentation where relevant. Do not invent multi-cloud support, enterprise SLAs or additional platform components merely to populate a quality-category checklist.

Case references to airworthiness rules, sanctions regimes, GDPR, financial reporting and Cape Town obligations identify discovery and assurance needs, not a claim of legal compliance. Applicability and control requirements must be validated by qualified owners before operational use.

## 10. Measurement and benefits assessment

The capstone reports synthetic quality, observed latency, model/tool usage, estimated deployment cost and control-test results under BG-001 through BG-007. It may illustrate the following operational measurement approach, but need not obtain real historical/customer data or prove monetary benefits. The operational analysis below is a future-programme proposal.

### 10.1 Matched historical comparison

Agree matching criteria and historical eligibility before inspecting pilot outcomes. Consider aircraft/engine characteristics, lease return conditions, maintenance complexity, evidence quality, access timing and available records. Document exclusions, sample size and differences between historical and pilot cohorts.

Use consistent definitions of the planned and actual contractual acceptance dates. Preserve rebaselines and their reasons. Missing discovery timestamps, forecast history or effort logs remain limitations; they are not reconstructed from guesses.

Track external delay drivers such as parts, MRO capacity, negotiations and next-lessee readiness. Report evidence-related contributions separately where supported. An observational historical comparison supports a qualified contribution estimate, not an unqualified causal claim.

Pilot reporting must also show access/onboarding failures, open returns and unresolved issues so completed or easy cases do not obscure the whole cohort's performance. Analysis methods and material changes require documented review.

### 10.2 Financial benefits

| Benefit category | Required treatment |
| --- | --- |
| Additional collectible rent | Validate rent commencement and cessation effects, displaced receipts and outgoing-lessee late-return liability. Avoid assuming every recovered day creates new revenue. |
| Avoided incremental costs | Identify costs genuinely prevented, with timing and an attributable basis. |
| Staff capacity released | Measure effort saved; record separately from payroll or other cash savings unless a real cashable change is approved. |
| Reserve recovery | Separate calculation corrections, collectible amounts, collection timing and profit impact; avoid double counting. |
| Risk reduction | Use an evidenced, qualified exposure assessment; do not invent a monetary loss or claim a synthetic case proves reduced default risk. |
| Programme costs | Include implementation, access negotiation, onboarding, integration, migration, infrastructure, processing, storage, review, support and assurance costs. |

The case stipulates EUR 250,000 per idle aircraft-month and EUR 60 million annual value across 150 transitions with a 40% duration reduction. The arithmetic implies 1.6 months saved per transition and a four-month baseline; neither that baseline nor full monetisation is established. These figures remain scenario claims, not a validated programme business case or funding ceiling.

Prepare low, base and high cases with explicit cohort eligibility, adoption, attributable improvement, collection assumptions and ongoing costs. Budget, benefit hurdle, payback expectations and downside tolerance remain open for finance and sponsor approval.

## 11. Staged evidence gates

The gate structure follows D-05. Evidence thresholds and authorised decision-makers must be fixed before evaluating each gate. Approval enables only the next bounded stage.

| Gate | Required evidence | Decision enabled |
| --- | --- | --- |
| G-01: Pilot entry | Authorised access for activated cases; approved requirements, rates, templates, recipients and action limits; named operators and approvers; established measurement baseline; evaluated control effectiveness; support and exception procedures. | Begin the bounded three-capability pilot with controlled routine actions. |
| G-02: Selective expansion | Measured improvement in agreed leading indicators; controls effective; disclosed incidents and exceptions; manageable review workload and unit cost; continued evidence access. | Add a specifically approved limited cohort. Completed-return benefit claims are not required for this gate and must not be implied. |
| G-03: Broader rollout | Completed-return results against matched historical baselines; credible net-benefit analysis; effective controls; sustainable capacity, support and adoption; remaining limitations disclosed. | Release further funding and widen deployment within an approved scope. |

Proposed assurance contributions: technical operations validates readiness evidence; finance validates reserves and economics; legal validates rights and notice controls; risk/security/compliance validates relevant controls; sponsors decide progression under an approved delegation. This is not yet an agreed approval matrix.

No operational gate above is satisfied solely by a synthetic demonstration, a successful technical deployment or the case's aspirational percentages. A safety failure cannot be offset by benefit. Hold, remediation, narrowing scope or stopping must remain available decisions.

### 11.1 Separate capstone readiness checkpoint

The capstone delivery owner can prepare the submission when AC-001 through AC-008 and AC-011 through AC-016, plus the capstone NFR checks, have recorded passing evidence; FR-009/FR-010 require design explanations and the AC-016 design-only card only. Repository and Azure evidence must identify the tested revision/configuration, and known limitations and unresolved demo parameters must be disclosed. Deployment permission must precede provisioning; an unmet permission dependency is a blocker, not a successful deployment.

This checkpoint concerns demonstration readiness only; it does not assert evaluation, approval, business sign-off or production readiness. Applicable evaluation criteria remain separate from this BRD. A complete production BRD may remain unresolved on deferred operational goals and thresholds even when a capstone implementation is ready.

## 12. Dependencies, risks and responses

| Dependency or risk | Business consequence | Proposed response |
| --- | --- | --- |
| Access rights or airline cooperation are delayed | Earlier intervention may not be possible. | Establish rights and focal points; disclose unavailable cases and narrow claims where access is late. |
| Records are incomplete, unreadable or incorrectly associated | False certainty, inappropriate requests or missed issues. | Separate evidence and processing states, retain provenance and route ambiguity to review. |
| Physical work dominates the return critical path | Faster review may not change acceptance timing. | Track dependency causes and distinguish records contribution from overall delay. |
| Review workload or exception volume exceeds capacity | Backlogs move rather than disappear. | Measure reviewer load, fund operating capacity and limit expansion. |
| Historical data is incomplete or cohorts differ materially | Benefits cannot be reliably estimated. | Disclose limitations, restrict conclusions and improve prospective measurement. |
| Counterparty information is exposed or an action exceeds authority | Legal, safety and reputational exposure. | Enforce access/action boundaries, suspend affected automation and invoke incident procedures. |
| Fleet-wide backlog processing is uneconomic | Unit economics invalidate the rollout case. | Separate cohort and incremental needs from migration; compare existing or bought capabilities. |
| Draft outputs are mistaken for approved decisions | Incorrect acceptance, invoices or notices. | Make assessment, approval and execution distinct; retain designated human accountability. |
| Operating ownership or adoption is weak | Benefits do not persist after the pilot. | Assign service and process owners, train users and include support/adoption evidence in gates. |

## 13. Open items and validation ownership

Owners below are proposed role allocations. No amounts, dates or tolerances should be invented to close these items.

O-01 through O-12 concern a future operational programme. They do not require genuine fictional-customer validation before a synthetic demo. Their demo counterparts must instead be explicit fixture assumptions or bounded PRD decisions. No deferral below closes an operational issue or represents a waiver of formal business approval.

| ID | Open item | Proposed validating owner |
| --- | --- | --- |
| O-01 | Pilot size, selected counterparties, selection rules and return mix | Technical operations with sponsors |
| O-02 | Intervention lead time, duration and submission/update cadence | Technical operations, legal and counterparties |
| O-03 | Access rights, permitted use and approved exchange arrangements | Legal, privacy/security and counterparties |
| O-04 | Named operational owner, approvers, acceptance delegation and gate decision-makers | Sponsors with technical, finance and legal leadership |
| O-05 | Historical data availability, matching rules and KPI definitions | Technical operations and finance |
| O-06 | Numerical performance, quality and workload thresholds | Relevant business owners before results are assessed |
| O-07 | Programme budget, total cost envelope, benefit hurdle and payback expectations | CFO/finance with sponsors |
| O-08 | Reserve calculation conventions and authoritative source-system boundaries | Finance with legal and technical operations |
| O-09 | Request limits, escalation times, incident stop rules and reactivation authority | Technical operations, legal and risk/security |
| O-10 | Retention, legal hold, cross-border handling and applicable assurance requirements | Legal, privacy, compliance and records owners |
| O-11 | Availability, recovery, throughput and support objectives | Operational and service owners |
| O-12 | Later-phase requirements and progression criteria for transition planning and credit/sanctions | CTO/CRO with relevant business owners |

### 13.1 Disposition of operational open items

| Open items | Capstone treatment | Deferral rationale and target phase |
| --- | --- | --- |
| O-01, O-02 | Use the PRD's bounded synthetic case and package schedule; label assumptions. | Real cohort selection/cooperation cannot be validated in the capstone; Operations before pilot entry. |
| O-03, O-04 | Configure mock permissions and reviewer roles; capstone delivery owner owns delivery, not business approval. | Actual rights, delegations and operating ownership need qualified stakeholders; Operations before pilot entry. |
| O-05, O-07 | Show measurement/economics methodology; use synthetic evaluation and demo cost evidence only. | Real baseline and benefit hurdles need operational data and sponsor decisions; Operations before outcome assessment/funding gates. |
| O-06, O-11 | Define bounded fixture expectations, timeout/fallback and rehearsal checks in the PRD; disclose actual measurements. | Production thresholds and SLAs require representative workloads; Operations before pilot activation. |
| O-08, O-09 | Use versioned synthetic rate/obligation fixtures and deterministic mock action limits. | Actual rates, notice interpretations and action authority need finance/legal/operations; Operations before pilot entry. |
| O-10 | Synthetic-only data, secret controls and documented demo reset/removal remain required. | Production records/privacy/legal-hold policy needs qualified owners; Operations before real-data use. |
| O-12 | Explain future transition and credit/sanctions integration without implementing it. | Outside the working demo boundary; Future-Release. |

### 13.2 Capstone decisions still needed

| ID | Open decision / evidence | Owner and target phase |
| --- | --- | --- |
| O-13 | Confirm applicable capstone evaluation criteria, submission date and evaluation arrangements against the PRD's planning assumptions. | Capstone delivery owner; PRD before final submission planning. |
| O-14 | Confirm organization-approved identity, network and security prerequisites for any eventual Azure deployment; this BRD approves no environment, prerequisites or deployment action. | Capstone delivery owner with organization deployment approver; Implementation before any provisioning. |
| O-15 | Finalise synthetic fixture/evaluation expectations, model/tool limits, timeout behaviour and demo/fallback timings before rehearsal. | Capstone delivery owner; PRD before implementation/evaluation acceptance. |
| O-16 | Confirm repository publication audience and obtain applicable publication authorization before publishing material. | Capstone delivery owner with publication authority; Implementation before publication. |

## 14. Sources and document relationships

- Companion product requirements: [Aircraft Return Readiness PRD](../prd/aircraft-return-readiness.md).

Private source-local links, internal review records and non-public reference materials from the source document are omitted from this public-safe copy.

This document records proposed capstone direction and a future business programme while retaining unresolved parameters. It does not silently amend the companion PRD. The PRD remains the companion artifact for detailed capstone implementation scope and limits; reconcile changes using the identifier migration and traceability tables above. Subsequent changes should maintain traceability between the BRD, implementation requirements, evaluation evidence and executive claims.
