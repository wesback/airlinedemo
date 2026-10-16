# How the product handles compliance requests

Status: explainer for the capstone presentation. Not a requirement document and not legal, accounting or airworthiness advice. The demonstrator runs on synthetic data and does not establish compliance with any regime. Build scope is in [Source Systems PRD](prd/aircraft-source-systems.md) (SRC, v0.3.0).

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

## Limits to state out loud

- Requirements, regimes and amounts in the fixtures label scenarios. They are not legal conclusions.
- Which rules apply depends on the actual lease, registration, operator, jurisdiction and state declarations, none of which the demonstrator has.
- Sanctions lists, current legislation and licence conditions must be checked at the time of action.
- Regulatory dates and scope claims (for example EU AI Act timing, DORA/NIS2 applicability) come from the research briefing and need confirming against official sources before they appear on a slide.
