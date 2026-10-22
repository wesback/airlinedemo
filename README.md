# airlinedemo

This repository is a fictional aircraft-return readiness demonstrator. Its synthetic case models a records gap, ambiguous engine identity, and blocked documents. It has no real customer data, operational aircraft records, sanctions decisions, notices, invoices, or airworthiness decisions. The React and TypeScript reviewer interface presents bounded investigation and human review; ASP.NET Core owns workflow state, policy, and deterministic decisions.

## Architecture

The API and React client run from the same Container Apps application image. Azure Container Apps hosts the application; SQL owns workflow state; private Blob storage and Key Vault hold application data and secrets; a private Azure OpenAI model endpoint provides bounded investigation; and managed identity and private networking control service access. Container Registry stores immutable images. Protected Terraform state is separately bootstrapped outside the deployment inventory.

## Local setup

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

## Azure prerequisites

Use an organization-approved Azure subscription, tenant, Entra identity, region, resource names, model availability, private connectivity, quota, and budget owner. Log in only with an approved identity:

```sh
az login --tenant "$AZURE_TENANT_ID"
az account set --subscription "$AZURE_SUBSCRIPTION_ID"
az account show
```

A platform owner must bootstrap and protect the remote Terraform state backend before initialization. Backend Entra authentication and blob lease locking are required; the backend and its protection remain outside Terraform deployment resources. Do not create deployment-owned resources manually.

Review these existing Terraform inputs and resources before deployment: required names, location, VNet and private endpoint CIDRs, presenter/reviewer CIDRs, egress destinations, SQL, storage, model, Key Vault secret URI, Container Registry, Container Apps environment and app, budget, and alert settings. Empty presenter/reviewer CIDRs create an empty ingress allowlist, which denies all ingress.

Keep `TFVARS_FILE` and `BACKEND_CONFIG_FILE` outside tracked paths and set only non-secret values:

```sh
export AZURE_SUBSCRIPTION_ID='<approved-subscription-id>'
export AZURE_TENANT_ID='<approved-tenant-id>'
export RESOURCE_GROUP_NAME='<approved-resource-group>'
export TFVARS_FILE='/absolute/path/readiness.tfvars'
export BACKEND_CONFIG_FILE='/absolute/path/backend.hcl'
```

Do not commit secrets, passcodes, access keys, connection strings, Terraform state or plans, real customer data, private endpoint addresses, or private-network addresses. The passcode value must be held in Key Vault; configuration may contain only its secret URI. Do not enable public data-service access or storage shared keys.

## Bootstrap and manual release

Bootstrap creates deployment resources through Terraform only. The script initializes protected state, creates a saved plan, displays it, and applies that exact plan only after typing `yes`:

```sh
scripts/bootstrap-deployment.sh
```

Review the plan and repeat it until no unexplained drift remains before release. Terraform test `infra/terraform/tests/network-boundaries.tftest.hcl` and `infra/terraform/tests/container-hosting.tftest.hcl` remain the network and Container Apps configuration checks; `infra/terraform/tests` must not be used for local Azure provisioning.

Builds and images use the protected workflows. On an approved revision, start the existing `release` workflow manually with a successful build run ID and its exact `sha256` image digest. The workflow verifies provenance, requires product-owner approval, verifies and signs the digest, and deploys it through Azure Container Apps. It never accepts an image tag or builds and deploys locally.

## Smoke test, reset, and teardown

From an approved presenter/reviewer network, verify `/api/health`, role boundaries, synthetic case access, private dependency resolution, and the signed revision. Smoke checks require no public ingress when no presenter/reviewer CIDRs are configured.

Use the run-specific reset in the application to start a fresh synthetic run. It changes only that run and preserves audit history; it is not a database wipe. Use no live records.

Stop demonstrations before teardown. With protected state available, confirm the resource group and inputs, then run:

```sh
scripts/destroy-deployment.sh --yes
```

This destroys only Terraform deployment-owned resources. Preserve approved evidence, follow backend-owner retention, revoke temporary release access, and remove the Key Vault passcode only when approved.

## Limitations and evidence

This is not a production service, legal or finance advice, an airworthiness determination, or evidence of model accuracy, customer savings, availability, or production readiness. It sends no notices and makes no sanctions or technical-acceptance decisions.

For operator guidance, see [Aircraft Return Readiness deployment guide](docs/aircraft-return-readiness/README.md). Evaluation status is summarized at [Evaluation and results](docs/aircraft-return-readiness/README.md#evaluation-and-results). Trace capture is documented by [the trace capture script](docs/evidence/scripts/aircraft-return-readiness-trace-capture.sh); simulated decisions and fallback evidence use [the rehearsal evidence template](docs/aircraft-return-readiness/rehearsal-evidence.md).

No evaluated results or rehearsal evidence are claimed until they are actually recorded in the approved evidence location. Model and infrastructure prices are unapproved values; no scored result exists until the model manifest is approved and an actual run is recorded.
