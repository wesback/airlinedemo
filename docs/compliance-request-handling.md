# How the product handles compliance requests

Status: explainer for the capstone presentation. Not a requirement document and not legal, accounting or airworthiness advice. The demonstrator runs on synthetic data and does not establish compliance with any regime. Build scope is in [Source Systems PRD](prd/aircraft-source-systems.md) (SRC, v0.9.1).

## The position

We do not certify compliance, and the AI never does. When someone asks for compliance evidence, the product produces the evidence trail behind a human decision and routes the legal question to the person who owns it.

## The five-step route

1. **Classify the request** by area and owner (table below). Legal and specialist areas are never answered by the system.
2. **Pull the decision dossier** (SRC:FR-011): exact document and source-record versions, the requirement and its regime and responsible party (SRC:FR-012), the rule version, who approved, when and why. A hash shows it was not edited after export.
3. **State the limits.** Every dossier says it is not a certification, approval or legal conclusion.
4. **Route to the owner** through a specialist-review flag (SRC:FR-015). The system can say "specialist review required" or "not assessed". It never says cleared, compliant, valid or recoverable.
5. **Record the response** as a human decision in the same audit trail.

## By regulatory area

| Area | What the system provides | Who answers | In the demo | Designed only |
|---|---|---|---|---|
| **EASA / FAA airworthiness records** | Records evidence assessed against requirements tagged by regime (EASA Part-M; FAA 91.417, 121.380) and responsible party (lessee-operator, lessor, CAMO). Regimes are never cross-applied. | CAMO and authorized certifying staff | Cases A to C, regime tags, dossier | Live regulatory text updates; confirming current editions |
| **Cape Town Convention** | `cape_town_interest` evidence (interest and registration references, IDERA present or absent, state declaration noted) plus a `cape_town` flag. No outcome, priority or recoverability field exists. | Counsel | Evidence record and flag routing | Registry search; remedies analysis |
| **IFRS 16** | Rent, maintenance reserve, security deposit and reserve reimbursement kept as separate records. The reserve draft makes no profit, revenue or cash claim. An `ifrs16` flag routes classification and impairment questions. | Finance and accounting | Record separation, reserve draft, flag routing | Lease classification; impairment; recognition |
| **GDPR** | Records marked `containsPersonalData`. Summaries, dossiers and diagnostics carry identifiers, versions and hashes only. | Data protection officer | Marker and exclusion tests on synthetic personal data | Retention, erasure and data-subject request handling |
| **Sanctions (EU, US, UK)** | One flag per regime. A flag for one regime says nothing about another, because OFAC's aggregate 50% ownership rule differs from the EU and UK ownership and control tests. A name match is never clearance. | Sanctions and legal specialists | Per-regime flags and `not_assessed` for the others | Screening; licence handling; transaction-specific checks |

## Answers if asked cold

- **EASA/FAA:** "We assess the records. The CAMO certifies."
- **Cape Town:** "We show the interests on file. Counsel advises on remedies, and a registry entry or IDERA is not automatic repossession."
- **IFRS 16:** "We keep the amounts separate. Accounting decides classification and recognition."
- **GDPR:** "We minimise what we hold and export. The DPO handles rights requests."
- **Sanctions:** "We flag exposure per regime. A specialist clears it, never a name match."
- **What did you demonstrate?** "Controls on synthetic data: reproducible decisions, scope isolation, resistance to hostile content and human authority. Everything else is labelled design-only."

## In-application evidence walkthrough

Open a case, then **Decision dossier > Compliance evidence**. Select a recorded review and a server-provided regime. The view shows applicability ownership, evidence-record identities and versions, the recorded review role and time, and an **Activity** link to its audit entry. The shared-responsibility panel distinguishes platform controls (access control, audit trail and pinned evidence) from customer decisions (applicability, evidence review and specialist advice).

The technical review accepts or rejects findings on a whole basis; it is not a separate regulatory decision for every requirement. An untagged requirement, unassigned owner or missing evidence stays **Needs review**. A document-only journey basis has no source applicability pins and therefore shows no tagged regimes; the view does not manufacture evidence from current source records. Historical decisions retain their original pinned versions.

For an in-scope user, **Open source-system case** navigates to the separate synthetic `case-001`; it does not associate that case's records with a pool aircraft. With `Demo:SeedJourney=true`, startup pins signed source records only when this presenter-owned case is still empty. It creates no assessment or human decision. Existing cases, evidence and reviews are never replaced. A populated case retains its existing basis; new fixture tags appear only on a newly gathered basis, not retroactively on historical decisions.

The presenter-safe projection exposes identifiers and versions, not source content. Its new presenter permission does not grant access to existing source or dossier endpoints. See the [API and permission contract](source-systems-contract.md#case-evidence-projection). Cape Town records do not establish South African jurisdiction or remedies. No IATA applicability record exists in the current signed source set; the view does not invent an IATA regime.

## Azure portal evidence: deployed controls

Read-only inspection of `rg-demoair` in Sweden Central on 2026-10-30 confirmed the resources below. This is infrastructure configuration evidence, not proof of regulatory suitability or successful log ingestion. Do not show secrets, access tokens, source payloads or personal data during the demonstration.

| What to show | Portal path | Verified state and limit |
|---|---|---|
| Private connections | Resource group > `pe-kv`, `pe-acr`, `pe-blob`, `pe-model`, `pe-sql`, `pe-monitor` > Overview / DNS configuration | All six endpoints were provisioned successfully with Approved private-link connections. This does not independently prove every client uses the private route; inspect the applicable connection and private DNS configuration. |
| Key Vault | `kv-demoair-574713` > Networking, Access configuration and Properties | Public network access Disabled; RBAC enabled; soft delete and purge protection enabled. Show configuration, not secret values or key material. No Key Vault diagnostic setting was configured at inspection, so do not claim its audit events are reaching Monitor. |
| Azure Monitor | `log-airline-telemetry` > Overview / Logs; `appi-airline-telemetry` > Properties / Logs | Both resources exist; Application Insights is workspace-linked. Workspace retention is 30 days. Public ingestion and query access are Disabled on both resources; use the approved private-network path. Open Logs and inspect actual available records before claiming ingestion; an empty query is not evidence that no event occurred. |
| Application business audit | Case > Activity, or an evidence decision's Activity link | This is the application's decision history, separate from Azure diagnostics. A matching audit entry is not an exported Application Insights trace. |

`cae-demoair` had an empty Container Apps logging destination and no Log Analytics configuration at inspection. The evaluation activity source is not exported end-to-end to Application Insights. Monitor resources and `pe-monitor` alone do not prove application traces or Container Apps console logs are collected. Enabling missing diagnostics is a separate infrastructure change, not part of this read-only view.

**Stubs / design-only:** the five source-system adapters use signed synthetic fixtures. Live sanctions screening, registry queries, South African jurisdiction checks, IFRS 16 classification and GDPR rights handling are not deployed integrations. The application supplies traceable evidence and controls; the customer owns applicability and the resulting decisions.

## Limits to state out loud

- Requirements, regimes and amounts in the fixtures label scenarios. They are not legal conclusions.
- Which rules apply depends on the actual lease, registration, operator, jurisdiction and state declarations, none of which the demonstrator has.
- Sanctions lists, current legislation and licence conditions must be checked at the time of action.
- Regulatory citations (EASA Part-M, FAA 91.417 and 121.380, Cape Town, IFRS 16, GDPR, EU/US/UK sanctions) come from the research briefing and need confirming against current official texts before they appear on a slide.
