# Aircraft Return Readiness demonstrator

## Purpose

This repository provides a public-safe demonstrator for bounded aircraft return-readiness investigation. A synthetic records package can contain a records gap, an ambiguous engine identity, and blocked documents. The React and TypeScript reviewer interface presents investigation evidence and human-review decisions; the ASP.NET Core API owns workflow state, policy, and deterministic decisions.

The demonstrator is built to make the workflow visible, not to certify an aircraft, a company, a transaction, or a decision.

## Fictional business context

All aircraft, companies, people, records, counterparties, notices, invoices, sanctions decisions, technical decisions, and human decisions in this demonstrator are synthetic or simulated. The scenarios exercise bounded tools, policy checks, and simulated human review using committed synthetic fixtures.

The demonstrator establishes no real operational, airworthiness, financial, legal, contractual, safety, compliance, or customer outcome. It sends no notices, issues no invoices, resolves no sanctions status, and performs no technical acceptance.

## Architecture

The .NET 10 API owns workflow state, policy, and deterministic decisions. The React client is built into the same application image. Azure Container Apps runs that image behind approved network controls. Azure SQL stores workflow state; private Blob storage holds application data; Key Vault holds secrets; and a private Azure OpenAI endpoint supports bounded investigation.

Managed identity and private networking control service access. Azure Container Registry stores immutable images. Protected Terraform state is bootstrapped separately from the deployment inventory.

## Run locally

Prerequisites are .NET 10 SDK and Node.js LTS/npm.

```sh
ASPNETCORE_URLS=http://localhost:5080 dotnet run --project backend/AirlineDemo.Api
npm ci --prefix frontend
npm --prefix frontend run dev
```

Run all local tests from the repository root:

```sh
npm --prefix frontend run build
npm --prefix frontend test -- --run
dotnet test AirlineDemo.sln --no-restore -m:1
python3 scripts/tests/test_deployment_scripts.py
```

Use committed synthetic fixtures only. Local development must not ingest live records or connect to a production service.

## Azure prerequisites

Use an organization-approved Azure subscription, tenant, Entra identity, region, model availability, private connectivity, resource names, quota, budget owner, and deployment window. A platform owner must bootstrap and protect the remote Terraform state backend before deployment initialization. State access requires Entra authentication and blob lease locking; the backend remains outside the Terraform deployment inventory.

Organization owners must approve network CIDRs, permitted egress, private DNS, service grants, model deployment, signing materials, and release identities. Do not enable public data-service access, storage shared keys, public ingress for private services, or unsigned-image deployment.

Before deployment, review `infra/terraform/variables.tf` and `infra/terraform/tests/`. Empty presenter/reviewer CIDRs create an empty ingress allowlist and deny all ingress.

## Configuration

Keep real values in the operator environment and in Key Vault; use template values in tracked examples.

```text
AZURE_SUBSCRIPTION_ID=<subscription id>
AZURE_TENANT_ID=<tenant id>
RESOURCE_GROUP_NAME=<approved resource group>
TFVARS_FILE=<absolute variable file path>
BACKEND_CONFIG_FILE=<absolute backend configuration path>
MODEL_DEPLOYMENT_NAME=<approved model deployment name>
KEY_VAULT_SECRET_URI=<approved Key Vault secret URI>
PRESENTER_REVIEWER_CIDRS=<approved network CIDR list>
REHEARSAL_BASE_URL=<approved deployed HTTPS origin>
REHEARSAL_EXPECTED_ORIGIN=<approved independently verified origin>
```

The operator supplies subscription, tenant, resource-group, file-path, network, deployment, and origin values through protected environment variables or reviewed external files. The passcode value is supplied from Key Vault and never stored in the repository. Configuration contains names or secret URIs, not secret values. State files, plans, tokens, connection strings, real customer records, and private-network addresses must remain outside tracked paths.

For compatibility with the legacy demo scripts, the pre-bootstrap gate also
accepts `AIRLINEDEMO_SUBSCRIPTION_ID` and `AIRLINEDEMO_TENANT_ID` when the
matching `AZURE_*` variables are unset. The deployment script itself uses the
documented `AZURE_*` names.

## Deploy and release

Run `scripts/prebootstrap-deployment.sh` before deployment to verify tools,
Azure authentication, subscription and tenant selection, external Terraform
files, provider registration, and Terraform formatting. It does not plan or
apply infrastructure. Provider registration requires the explicit
`--register-providers` flag.

Use `scripts/bootstrap-deployment.sh` for operator bootstrap. The script initializes protected Terraform state, creates and displays one saved plan, and applies that exact plan only after explicit confirmation. It does not build, sign, push, or deploy an image.

Protected CI builds provenance-backed images. An approved revision must use the existing protected `release` workflow with a successful build run ID and exact image digest. The workflow verifies provenance, requires product-owner approval, verifies and signs the digest, then deploys it through Azure Container Apps. It never accepts a mutable tag or deploys locally.

See the [Aircraft Return Readiness operator guide](docs/aircraft-return-readiness/README.md) for the complete setup, deployment, release, seed, run, rehearsal, and evidence procedures.

## Smoke test, run and reset

From an approved presenter or reviewer network, verify `/api/health`, role boundaries, synthetic case access, private dependency resolution, and the signed revision. Smoke checks require no public ingress when no presenter/reviewer CIDRs are configured.

Exercise the committed synthetic baseline, routine mock-inbox request, ambiguous-evidence review, and blocked-document paths. Do not use live records.

Use the run-specific reset action to start a fresh synthetic run. A reset changes only that run, preserves audit history, and does not ingest evaluation answers. It is not a database wipe. If a live rehearsal segment is unsuitable, use the recorded fallback rather than hiding an error or pending state.

## Teardown

Stop demonstrations and evaluation first. Preserve approved rehearsal, audit, and release evidence according to the approved retention policy. With protected state available and the tenant, subscription, inputs, and resource group confirmed, run:

```sh
scripts/destroy-deployment.sh --yes
```

The script destroys only Terraform deployment-owned resources. The protected state backend is handled separately by its owner. Follow the [full teardown procedure](docs/aircraft-return-readiness/README.md#removal-and-teardown), revoke temporary release access, and remove the Key Vault passcode only when approved.

## Evidence

The rehearsal evidence page records the product owner's actual rehearsal: its trace references, simulated human decisions, fallback use, timings, failures, smoke checks, and approved redacted captures. The evidence lives in [rehearsal evidence](docs/aircraft-return-readiness/rehearsal-evidence.md) and the [rehearsal checklist](docs/evidence/aircraft-return-readiness-rehearsal-checklist.md).

A versioned evaluation and cost report is produced by a rehearsal run and recorded through the rehearsal evidence page, not committed as results. The frozen evaluation format is defined by the operator guide; cost inputs and disclosure are described in [cost model](docs/aircraft-return-readiness/cost-model.md). No versioned result, scored outcome, price, savings claim, or operational readiness claim is invented by the README.

Approved evidence remains in the organization's restricted evidence location. A checklist, template, capture, plan, or output from another revision does not establish the result of the current rehearsal.

## Limitations

This demonstrator does not establish airworthiness, financial, legal, contractual, operational, safety, compliance, availability, model-accuracy, customer-savings, or production-readiness outcomes. All aircraft, companies, counterparties, people, records, notices, invoices, and decisions are synthetic or simulated.

The frozen fixtures, model approval, model manifest, and actual rehearsal determine whether evaluation evidence exists. Infrastructure and model prices remain unapproved until recorded evidence replaces them. The demonstrator sends no external communication, makes no sanctions decision, and performs no technical acceptance.
