# Rehearsal evidence

**Rehearsal status: NOT PERFORMED.** The product owner has not recorded an observed rehearsal in this checkout. This is an unpopulated evidence record, not a rehearsal result. Do not treat blank fields as observed, infer values, or commit generated evidence. Complete it from an actual run only after the approved environment and model manifest are available, following the [rehearsal checklist](rehearsal-checklist.md).

All counterparties and case data must be synthetic; reviewer decisions are simulated and recorded as human actor events. Commit references and redacted metadata only. Keep raw traces and audit evidence in the approved restricted evidence location. Do not include secrets, credentials, connection strings, access keys, real customer records, raw document text, prompts, or responses.

## Run identity and frozen versions

- Repository revision:
- Application version and immutable image digest:
- Fixture ID:
- Fixture hash:
- Suite version:
- Suite hash:
- Model manifest version and approval reference:
- Deployment configuration (environment name and non-secret configuration reference):
- Build run ID:
- Release run ID:
- Run IDs (demo and evaluation arms, if evaluation was performed):

## Observed timings and results

- Actual timings (outcome, live segment, and trace capture):
- Outcome timing (trigger to first visible outcome; measured value and unit):
- Live-segment timing (first action through decision and reassessment; measured value and unit):
- Trace-capture timing (recording duration; measured value and unit):
- Observed results (outcome, smoke checks, failures, and evaluation outputs):
- Observed outcome and readiness state:
- Smoke-check results:
- Failures, pending/error states, and limitations:
- Evaluation outputs:

## Trace and business audit evidence

- Real investigation trace reference (restricted evidence location and run correlation):
- Trace resource attributes (`service.name`, `service.version`, `deployment.environment`):
- Simulated decision (reviewer role, consequential action, reason):
- Decision business-audit reference:
- Synthetic response reference:
- Response-driven reassessment reference and observed result:
- Reviewer verification of trace and audit evidence (name and date):

Trace and audit references must reconstruct the actual investigation and reviewer action. Verify the run and frozen versions match this record, the trace is from the running application, and no raw document, prompt, or response content is present. A missing trace or audit event is not replaced by a reconstructed account.

## Recorded fallback

- Fallback used and reason:
- Fallback fixture ID:
- Fallback application version:
- Fallback evidence location:
- Fallback limitations:
- Reviewer verification that the fallback is reproducible (name and date):

A fallback is acceptable only when its reproducible fixture ID and application version are both recorded, its evidence location is accessible to the authorized reviewer, and its limitations are disclosed. An unavailable or unverified fallback leaves the rehearsal incomplete.

## Review and evidence location

- Product owner:
- Rehearsal date:
- Approved restricted evidence location:
- Evidence-record commit:
