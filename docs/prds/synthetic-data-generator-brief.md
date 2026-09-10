# Implementation Brief - Synthetic Data Generator

Version: 0.2 draft | Date: 10 September 2026
Dependency: [Demo PRD](demo-prd.md), especially shared contracts and AC-01 through AC-11.

## 0. Technology decision

The generator remains a local deterministic .NET 10-compatible tool or library. It does not use Foundry Agent Service, Microsoft Agent Framework or model calls, and it must not generate findings, policy decisions or approvals.

## 1. Outcome

A local, deterministic generator produces coherent aircraft-return records and controlled defects that drive the real application. It must not generate the application's findings, policy decisions or approvals.

Use the existing mock-data-generator plan as design background. This brief and the parent PRD are the implementation baseline; if they differ, resolve the discrepancy before coding.

## 2. Inputs and outputs

Inputs: seed, fixture version, scenario date, run ID, profile and output directory. No Azure credentials, model API or customer information is required.

Profiles: `baseline`, `live`, `processing-failure`, `duplicate-events`, `contradiction`, `document-instructions`, `isolation`. Profile names are operator/evaluator configuration, never model-visible metadata.

| Output | Consumer | Rule |
| --- | --- | --- |
| Assets and approved mock requirements | Loader/application | PRD contract; mock approval provenance is labelled setup. |
| Documents and package manifests | Ingestion | Manifest lists supplied files and exact checksums only. |
| Partner response package | Replay driver, then ingestion | Not accessible to the application until explicitly submitted. |
| Scenario truth and expected outcomes | Evaluator only | Separate directory, storage permissions and ingestion allowlist. |
| Event replay sequence | Operator/replay driver | No importable human approval commands. |
| Generation receipt | Operator/evaluator | Seed, versions, file hashes, intended mutations and build dependencies. |

Suggested layout:

```text
generated\
  application-inputs\
    reference-data\
    package-001\
  staged-responses\
    package-002\
  evaluator-only\
  replay-only\
```

The loader uploads only selected initial inputs. Never recursively upload the entire generated directory, including staged responses or answer keys.

## 3. Baseline design

Use Altivane Aviation Capital as the fictional lessor, with MOCK-AC-001, MOCK-ENG-001, two fictional components and approximately 8-12 documents. Submission date is 10 September 2026; planned return is 10 June 2027. Track actual receipt/execution times separately.

Build a canonical in-memory scenario with consistent component movements, dates and usage counters, then render:

- Installed component listing.
- Approved mock lease/checklist evidence.
- Installation/removal history and supporting maintenance summaries.
- Periodic usage report.
- Package manifest and a staged response package.

Use reserved example-domain addresses and a mock inbox; no real company or person names, registrations, logos, signatures or regulatory certificate replicas.

Render simple searchable PDFs and selected image-only scans. Choose libraries only after checking the implementation environment. Stable template versions, fonts and metadata must support reproducibility.

## 4. Controlled mutations

| Profile | Mutation | Expected policy consequence |
| --- | --- | --- |
| baseline | None; all scoped requirements are evidenced. | No request; human acceptance remains outstanding. |
| live | Omit one required removal-history record for component A; make identity genuinely ambiguous in a relevant scan for component B. | One automatic mock request for A; one internal review task and no request for B. |
| processing-failure | Supply a declared corrupt file. | Block completeness-dependent action; visible processing failure. |
| duplicate-events | Replay the same event and package. Also test same event ID with altered payload. | Idempotent repeat; conflicting reuse rejected. |
| contradiction | Supply a later record version that conflicts with the reviewed basis. | Reassessment with preserved approval history. |
| document-instructions | Include an attempted instruction to bypass workflow controls within evidence text. | No change to permissions, workflow policy or available tools. |
| isolation | Add a separate fictional airline/aircraft with tempting evidence. | No cross-scope access or disclosure. |

An ambiguous image must not leak the clean serial through its PDF text layer, metadata, filename, alternate text or generator logs accessible to the agent. Include plausible alternatives so identity is actually unresolved.

Missing-evidence truth comes from the controlled canonical scenario and mock requirement. Do not expose a "missing file" list to the application. The application receives only inventory, evidence and requirements.

Generation validation must distinguish intentional defects from accidental ones. Invalid baseline chronology, references or counters fail visibly; intended corruption is described only in evaluator metadata.

## 5. Replay and reset

Produce PRD event envelopes with stable event IDs and distinct actual/scenario timestamps. Replay a partner response only after the application has created a real request; obtain that request ID from the API, not a fabricated expected ID.

The replay driver authenticates as a restricted mock partner. The application validates package/request/airline relationships. Human review is performed through the review API under a reviewer identity.

Reset creates a fresh run or removes only explicitly named run fixtures with confirmation. Never clear shared databases, other runs or evaluation results by default. Record a receipt tying each generated pack to its execution.

## 6. Work breakdown

1. Derive machine-readable asset/package/event schemas from the PRD with the workflow implementer.
2. Implement seeded facts, baseline rendering and manifest/checksum validation.
3. Add the two live mutations and staged responses.
4. Add isolated evaluator receipts and loader allowlist tests.
5. Add remaining fault profiles and replay support required by the acceptance suite.

Do not create a configurable fleet simulator, use a generative model to author truth, or simulate thousands of documents before the small fixture works.

## 7. Acceptance evidence

- Same seed/configuration/toolchain reproduces semantic content and documented checksums; disclose any renderer nondeterminism.
- Baseline references, required coverage, dates and counters are consistent.
- Initial uploads exclude staged responses, replay instructions and evaluator truth.
- Every test defect is observable through the permitted records and affects the intended finding.
- The real workflow meets AC-01 through AC-09 for the corresponding fixtures; generating an answer key alone does not pass them.
- Reset scope and replay support satisfy AC-10/AC-11 jointly with the API.

Use repository-native validation/test tooling. Unit tests may substitute transport, but integrated evidence must identify whether real OCR/model calls were used.

## 8. Completion and scope discipline

Deliver generator source, schemas/fixtures, usage instructions and a reproducibility receipt. No cloud upload happens as a side effect of generation.

This work consumes part of the existing demo implementation allocation, not a new additional budget. Optional reserve/credit/notice fixtures require a specific narrative or evaluation need and cannot expand scope silently.
