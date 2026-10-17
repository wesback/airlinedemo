# Aircraft Return Readiness

## Purpose and business story

This demonstrator tells a fictional aircraft-return story: a records package has a routine evidence gap, an ambiguous engine identity, and blocked documents. It shows how bounded investigation, policy, reviewers, and a mock partner inbox can make the work visible. It is a synthetic case study, not an operational service or evidence of customer outcomes.

All aircraft, counterparties, people, records, and decisions in the case are synthetic or simulated. See the [case design](../prd/aircraft-return-readiness.md) and [scored-evaluation format](../../backend/AirlineDemo.Api.Tests/Fixtures/Evaluation/README.md).

## AI, human in the loop and autonomy

The AI investigator uses bounded, read-only tools and returns typed claims. Deterministic validation checks each claim against document pages. The AI sends nothing and decides nothing. Deterministic rules handle policy, requests to the mock inbox, reserves, obligation status, and notice drafts. Ambiguous or conflicting evidence, technical acceptance, finance review, and legal review require a person. These reviewer roles are simulated; decisions are recorded as human actor events.

| Capability | Case-study autonomy | Demo autonomy |
|---|---|---|
| Records investigation | L4, autonomous | L3: acts only on routine evidence requests within policy to the mock inbox; a person decides ambiguous cases and technical acceptance |
| Utilisation and reserves | L3, acts (invoices) with finance review | L2: drafts a reserve calculation; a simulated finance review decides; no invoice |
| Contract compliance | L3, acts (notices) with legal sign-off | L2: drafts an obligation status and notice; a simulated legal review decides; no notice sent |
| Transition planning | L2, recommend | design-only: not implemented in this demo |
| Credit and sanctions monitoring | L2, recommend | design-only: not implemented in this demo |
| Portfolio supervisor | not in the case-study table | design-only: not implemented in this demo |

The demo stops short of the case-study levels: it has no invoices, notices, sanctions decisions, or airworthiness decisions, and uses synthetic data only. Evidence for AI value comes from the [four-arm evaluation](#evaluation-and-results); no scored result exists until the model manifest is approved.

## Architecture

The .NET 10 API owns workflow state, policy, and decisions; the React client is built into the same application image. The Azure design runs that image in Container Apps with SQL state, private Blob storage, a private model endpoint, Key Vault, and a private container registry. Managed identity and private endpoints provide service access. The investigator is only one bounded part of this rules- and person-led workflow.

## Prerequisites

- An organization-approved Azure subscription, resource names, region/model availability, quota, budget owner, and deployment window.
- An approved Entra identity for Terraform, SQL administration, the application, and the protected state backend; release OIDC identities and the private-network release runner.
- Approved presenter/reviewer ingress CIDRs, private endpoint/DNS connectivity, and required restricted egress destinations. The app allowlist is empty-deny.
- Terraform 1.9 or later, Azure CLI, Git, .NET 10 SDK, Node.js LTS/npm, and access to the repository's protected build and release workflows.
- An approved model deployment and reviewed model manifest before investigation or scored evaluation.

Stop if identity, private connectivity, model approval, or organization approval is missing. Do not substitute public endpoints or personal credentials.

## Safe configuration

Use the approved Entra/OIDC identity and a protected remote Terraform state backend with Entra authentication and blob lease locking. A separately protected bootstrap pipeline must provision and protect that backend before this Terraform root is initialized; this prerequisite is outside the deployment-owned inventory. Do not bootstrap it by hand in the portal or put state credentials in this repository.

Keep non-secret Terraform inputs in a reviewed file outside tracked paths and pass it with `-var-file`. Keep the backend configuration outside the repository as well. Pass only a Key Vault secret URI for the demo passcode; store its value in Key Vault. Never commit passcodes, tokens, access keys, connection strings, state, plans, logs, or secret-bearing configuration. Terraform uses managed identity for app access; do not enable storage shared keys or public data-service access.

## Setup and deployment

1. Check out the approved repository revision and record `git rev-parse HEAD`; use a clean worktree and retain the revision with the evidence.
2. Have the authorized platform owner complete and verify the protected state bootstrap. Confirm Entra-only state access and locking. No manual portal resource creation is part of provisioning.
3. Review non-secret inputs, Entra identity grants, private connectivity, CIDRs, Key Vault reference, and immutable image digest. Run `terraform -chdir=infra/terraform fmt -check -recursive`, `terraform -chdir=infra/terraform init -backend-config=/absolute/path/backend.hcl`, and `terraform -chdir=infra/terraform validate`. Keep backend configuration and variable files outside the repository.
4. Create and review a saved Terraform plan for that revision and inputs. Apply only the reviewed plan; retain the plan and apply evidence in the approved restricted location. Provisioning of deployment-owned resources uses Terraform only:

   ```sh
   terraform -chdir=infra/terraform plan -var-file=/absolute/path/readiness.tfvars -out=/absolute/path/reviewed.tfplan
   terraform -chdir=infra/terraform apply /absolute/path/reviewed.tfplan
   ```

5. Run a repeated Terraform plan with the same revision and inputs. Record its output and explain any drift; require no unexplained changes before release:

   ```sh
   terraform -chdir=infra/terraform plan -var-file=/absolute/path/readiness.tfvars -out=/absolute/path/repeated.tfplan
   ```
6. Build and retain the CI supply-chain evidence. Use the protected `release` workflow with the successful build run ID and exact image digest. Its private runner verifies provenance and signature, requires product-owner approval, and deploys the signed digest; do not deploy a mutable tag.
7. From an allowlisted network, verify the release succeeded, `/api/health` is healthy, sign-in and role boundaries work, private dependencies resolve, and the synthetic case can be opened. Record the deployment and smoke-check results.
8. Follow [Seed, run and reset](#seed-run-and-reset), then complete the [rehearsal evidence](rehearsal-evidence.md). Teardown is in [Removal and teardown](#removal-and-teardown).

The documented lifecycle is revision, protected state bootstrap, Terraform plan/apply/repeated-plan evidence, signed deployment, smoke checks, seeding, operation, evaluation, and teardown. No deployment-owned resource is created manually in the portal.

## Seed, run and reset

The application loads its committed synthetic source fixtures and embedded demo fixture at startup. Confirm the fixtures and their hashes at the recorded revision; do not replace them with live records. Sign in using the approved demo passcode and synthetic demo-user roles obtained through the authorized secret-distribution process. Exercise the baseline, routine mock-inbox request, ambiguous-evidence review, and blocked-document paths.

Use the run-specific reset action to start a fresh demo run. Confirm that it changes only that run, preserves audit history, and does not ingest evaluation answers. A reset is not a database wipe. If a live run is unsuitable, stop and use the recorded fallback rather than hiding an error or pending state.

## Evaluation and results

The [scored-evaluation format](../../backend/AirlineDemo.Api.Tests/Fixtures/Evaluation/README.md) describes the frozen synthetic suite and scoring inputs. After the model manifest and annotations are approved, run all four arms against the same frozen suite and retain the exact manifests, hashes, run IDs, traces, eligible counts, failures, and output. Report per-arm precision and recall, citation validity, forbidden actions, p50/p95 latency, and cost per page and case. Label each figure by its evidence kind; an unmeasured value is “not measured.” No result is reported here: a scored result does not exist until model approval and an actual run.

## Cost disclosure

No numeric estimate or spending cap is approved. The Terraform budget notification is an alert, not a spending cap. Every unapproved price remains `STUB`; replace a value only after approval and evidence.

| Cost item | Price |
|---|---|
| Azure compute | STUB |
| Azure SQL | STUB |
| Azure Blob storage | STUB |
| Azure Container Registry | STUB |
| Model input tokens | STUB |
| Model output tokens | STUB |

## Removal and teardown

Stop demonstrations and evaluation first. Preserve required rehearsal, audit, and release evidence under the approved retention policy. The authorized Terraform identity must review and apply a saved destroy plan for the same state and inputs:

```sh
terraform -chdir=infra/terraform plan -destroy -var-file=/absolute/path/readiness.tfvars -out=/absolute/path/destroy.tfplan
terraform -chdir=infra/terraform apply /absolute/path/destroy.tfplan
```

Verify the resource group and its resources are removed; handle the protected state backend through its separate owner and retention policy, not this stack's destroy. Revoke temporary release access and remove the demo passcode from Key Vault when its owner approves.

## Limitations

This demonstrator uses synthetic evidence and simulated counterparties and reviewer roles. It is not a production aircraft-return process, legal or finance advice, an airworthiness determination, or proof of model accuracy, customer savings, availability, or production readiness. It sends no formal notices, issues no invoices, makes no sanctions decisions, and performs no technical acceptance. Model, infrastructure, and token prices are unapproved `STUB` values; no scored results are available before model-manifest approval.
