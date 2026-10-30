# Aircraft Return Readiness demonstrator

## Purpose

This repository provides a public-safe demonstrator for bounded aircraft return-readiness investigation. A synthetic records package can contain a records gap, an ambiguous engine identity, and blocked documents. The React and TypeScript reviewer interface presents investigation evidence and human-review decisions; the ASP.NET Core API owns workflow state, policy, and deterministic decisions.

The demonstrator is built to make the workflow visible, not to certify an aircraft, a company, a transaction, or a decision.

## Fictional business context

All aircraft, companies, people, records, counterparties, notices, invoices, sanctions decisions, technical decisions, and human decisions in this demonstrator are synthetic or simulated. The scenarios exercise bounded tools, policy checks, and simulated human review using committed synthetic fixtures.

The demonstrator establishes no real operational, airworthiness, financial, legal, contractual, safety, compliance, or customer outcome. It sends no notices, issues no invoices, resolves no sanctions status, and performs no technical acceptance.

## Architecture

The .NET 10 API owns workflow state, policy, and deterministic decisions. The React client is built into the same application image. Azure Container Apps runs that image behind approved network controls. Azure SQL stores workflow state; private Blob storage holds application data; Key Vault holds secrets; and a private Azure OpenAI endpoint, reached through an internal API Management gateway, supports bounded investigation. Log Analytics and Application Insights collect diagnostics.

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
python3 scripts/tests/test_deploy_script.py
```

Use committed synthetic fixtures only. Local development must not ingest live records or connect to a production service.

For a walk-through demo, start the API with `Demo__SeedCases=true Demo__SeedJourney=true Demo__Passcode=<passcode>`. The journey seeder places the three case-pack aircraft at different gates (waiting for technical review, waiting for finance, waiting for legal) with recorded AI agent events, so the journey board and the 3D hangar have something to show. To finish the journey, open `cp-case-a`, switch to the legal reviewer to sign off, then switch to the presenter (transition lead) and release the aircraft to the next lessee; the release is gate-checked, audited and simulated. Terraform sets both seed flags for the demo Container App. The journey seeder only advances cases that are still empty (revision 0); it does not overwrite existing case work.

The interface opens on the journey board: every aircraft in the return pool sits at its current gate, and each case opens as a step-by-step journey that marks AI agent steps, automated rules and human decisions. The 3D hangar button in the top bar switches to the walkable hangar for the same aircraft.

## Quick deploy

For a demonstration environment, sign in with `az login` and run one command twice. Everything Azure CLI already knows (tenant, subscription, your object id, name and email) is read from the sign-in, every resource name and network range derives from `--prefix`, and the protected Terraform state backend is created for you (Entra-only, no shared keys, versioned, public network access disabled and reached through an enforced network security perimeter `nsp-<prefix>-tfstate` that admits only this machine's address; a rerun updates the address).

```sh
az login --tenant <tenant-id>
scripts/deploy.sh --presenter-cidrs 84.196.152.13/32 \
  --allow-my-ip --vault-allow-my-ip --register-providers
```

The first run creates the platform only: the network, registry, Key Vault, data services, model gateway and identities, but no Container App, because the image must be pushed into this deployment's private registry first. At the end it prints the next steps:

1. Store the demo passcode in the vault (`az keyvault secret set --vault-name kv-<prefix>-<suffix> --name demo-passcode ...`) and, with `--build-runner`, the GitHub runner token as `github-runner-token`.
2. Run the build workflow on the `private-acr` runner to push the first image, then run the protected release workflow with `bootstrap_only=true` to sign and verify it without updating the not-yet-created Container App. Product-owner approval and every provenance and signature check still apply.
3. Run the same command again with `--image-digest sha256:<64 hex> --passcode-secret-uri https://kv-<prefix>-<suffix>.vault.azure.net/secrets/demo-passcode` to create the Container App.

Give both options or neither. After the app exists, the release workflow deploys each new signed digest (`az containerapp update`), and Terraform ignores the running image so a later apply does not roll it back.

The script writes the Terraform variable and backend files to `~/.local/state/airlinedemo/<prefix>/` (outside the repository, mode 600), runs the pre-bootstrap checks, shows one saved plan and applies it only after you type `yes`. `--prepare-only` writes the files and changes nothing in Azure. `--existing-backend <storage account>` uses a state backend an owner already created instead. `--vault-allow-my-ip` admits this machine's address (`deployer_ip_cidrs`) to the Key Vault through an enforced network security perimeter (public network access stays disabled, as managed-tenant policy requires) so Terraform can create the release signing certificate from outside the virtual network; without it, or a run from inside the network, that step cannot reach the vault. A rerun keeps the first `budget_start_date`, so the budget is not replaced every month. `scripts/deploy.sh --help` lists every option. The image is still built, signed and released through the protected `release` workflow. Terraform also creates the Entra application the model gateway trusts (identifier URI `api://<client id>`, app role `Gateway.Invoke` assigned to the app identity), so the deployer needs the right to register applications in the tenant (the Application Developer role when user app registration is turned off).

The rest of this file describes the controlled path for organization deployments, where each input is approved and supplied separately.

## Azure prerequisites

Use an organization-approved Azure subscription, tenant, Entra identity, region, model availability, private connectivity, resource names, quota, budget owner, and deployment window. A platform owner must bootstrap and protect the remote Terraform state backend before deployment initialization. State access requires Entra authentication and blob lease locking; the backend remains outside the Terraform deployment inventory.

Organization owners must approve network CIDRs, permitted egress, private DNS, service grants, model deployment, signing materials, and release identities. Do not enable public data-service access, storage shared keys, public ingress for private services, or unsigned-image deployment.

Before deployment, review `infra/terraform/variables.tf` and `infra/terraform/tests/`. Empty presenter/reviewer CIDRs create an empty ingress allowlist and deny all ingress. Terraform 1.17 or later is required.

The model gateway (API Management, internal virtual network mode) gets its own subnet, `gateway_subnet_cidr`, which defaults to `cidrsubnet(vnet_address_space[0], 8, 3)`. It must be in the deployment `location`; `gateway_region`, `gateway_model_deployment` and `gateway_approved_application_identity` default to the location, the created model deployment and the application's managed identity. Key Vault keeps public network access disabled unless `deployer_ip_cidrs` lists addresses that may reach its data plane. Log Analytics and Application Insights accept ingestion and queries only through an Azure Monitor Private Link Scope with a private endpoint in the virtual network; query them from a network that resolves the `privatelink.*` monitor zones.

The registry and Key Vault have no public endpoint, so the `private-acr` jobs in the build and release workflows need a self-hosted runner inside the virtual network. Set `enable_build_runner` (or pass `--build-runner <owner/repo>` to `scripts/deploy.sh`) to create one: an Ubuntu VM with Docker and Azure CLI in its own subnet (`build_runner_subnet_cidr`, default `cidrsubnet(vnet_address_space[0], 8, 4)`), no public address or inbound access, and outbound through a NAT gateway. At boot it reads a GitHub token from the Key Vault secret `build_runner_token_secret_name` (default `github-runner-token`) and registers with the label `private-acr`. Terraform creates that secret with the value `unset` and never overwrites it; set the real value afterwards (`az keyvault secret set`) and the VM, which retries every minute for 24 hours, registers itself. Use a fine-grained token limited to the repository, with Administration read and write permission. The VM's identity can read only that secret; the jobs still sign in with their own OIDC identities.

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
accepts `AIRLINEDEMO_SUBSCRIPTION_ID`, `AIRLINEDEMO_TENANT_ID`,
`AIRLINEDEMO_RESOURCE_GROUP_NAME` and `AIRLINEDEMO_OPERATOR_VARS_FILE` when
`AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `RESOURCE_GROUP_NAME` and
`TFVARS_FILE` are unset. The deployment script itself uses the
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

For an environment created with `scripts/deploy.sh --prefix <prefix>`, export these first (`deploy.sh` exports them only in its own process):

```sh
export AZURE_TENANT_ID=$(az account show --query tenantId -o tsv)
export AZURE_SUBSCRIPTION_ID=$(az account show --query id -o tsv)
export RESOURCE_GROUP_NAME=rg-<prefix>
export TFVARS_FILE=~/.local/state/airlinedemo/<prefix>/deploy.tfvars.json
export BACKEND_CONFIG_FILE=~/.local/state/airlinedemo/<prefix>/backend.hcl
```

The script destroys only Terraform deployment-owned resources. The protected state backend is handled separately by its owner. Follow the [full teardown procedure](docs/aircraft-return-readiness/README.md#removal-and-teardown), revoke temporary release access, and remove the Key Vault passcode only when approved.

The destroy soft-deletes the Key Vault, and purge protection keeps its name, secrets and signing certificate for 90 days. Redeploying with the same prefix in that window works: `scripts/deploy.sh` recovers the soft-deleted vault, grants the deployer the Key Vault Certificates Officer and Secrets Officer roles again, and writes `adopt_key_vault` so Terraform imports the vault, its roles, the signing certificate and the runner token secret instead of creating them. Pass `--vault-allow-my-ip` when you run it from outside the virtual network.

## Evidence

The rehearsal evidence page records the product owner's actual rehearsal: its trace references, simulated human decisions, fallback use, timings, failures, smoke checks, and approved redacted captures. The evidence lives in [rehearsal evidence](docs/aircraft-return-readiness/rehearsal-evidence.md) and the [rehearsal checklist](docs/evidence/aircraft-return-readiness-rehearsal-checklist.md).

A versioned evaluation and cost report is produced by a rehearsal run and recorded through the rehearsal evidence page, not committed as results. The frozen evaluation format is defined by the operator guide; cost inputs and disclosure are described in [cost model](docs/aircraft-return-readiness/cost-model.md). No versioned result, scored outcome, price, savings claim, or operational readiness claim is invented by the README.

Approved evidence remains in the organization's restricted evidence location. A checklist, template, capture, plan, or output from another revision does not establish the result of the current rehearsal.

## Limitations

This demonstrator does not establish airworthiness, financial, legal, contractual, operational, safety, compliance, availability, model-accuracy, customer-savings, or production-readiness outcomes. All aircraft, companies, counterparties, people, records, notices, invoices, and decisions are synthetic or simulated.

The frozen fixtures, model approval, model manifest, and actual rehearsal determine whether evaluation evidence exists. Infrastructure and model prices remain unapproved until recorded evidence replaces them. The demonstrator sends no external communication, makes no sanctions decision, and performs no technical acceptance.
