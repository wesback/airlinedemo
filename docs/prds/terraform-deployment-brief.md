# Implementation Brief - Terraform and Deployment

Version: 0.3 draft | Date: 12 September 2026
Dependency: [Demo PRD](demo-prd.md) and the runtime contracts in [Workflow/API brief](workflow-evidence-api-brief.md).

## 0. Technology decision

Provision the selected .NET 10 ASP.NET Core application as an Azure Container Apps workload (scale-to-zero consumption plan), with Azure SQL for workflow/business/audit state and Blob Storage for document content and versions. Use Azure AI Foundry for model governance/evaluation and a governed Azure OpenAI deployment for bounded extraction/classification. Do not provision Foundry Agent Service or Microsoft Agent Framework runtime infrastructure for the core workflow.

**Revision note (this version):** an earlier draft of this brief specified an isolated Azure Functions host with a Durable Functions backend. The merged application was built and reviewed as a conventional ASP.NET Core web app with its own workflow/state persistence, not as a Functions isolated-worker host, and no Durable Functions orchestration exists or is planned. Rather than retrofit the application onto Functions, this revision repoints the compute target to Azure Container Apps, which runs the application substantially as built and preserves a comparable consumption-based cost profile. Story #48's cost estimate was priced against Functions + Durable Functions hosting and must be re-costed against Container Apps pricing before re-approval.

## 1. Outcome and authority

Provide reproducible infrastructure and a documented path to deploy, seed, exercise, record and remove the small demonstrator.

Target region: Sweden Central (`swedencentral`). Planning ceiling: USD 500/month. Neither the ceiling nor this brief authorises provisioning or spending. Obtain approval against an actual costed configuration before applying.

This is not infrastructure for a 1,200-aircraft production rollout. Production gaps remain explicit rather than hidden behind a "production-ready" label.

## 2. Preflight decisions

| Required input | Why it blocks deployment |
| --- | --- |
| Subscription, tenant and resource naming/prefix | Establish destination and prevent accidental deployment into shared/customer environments. |
| Package versions and Container Apps hosting/runtime compatibility | Establish compatible build, container image and hosting requirements; do not scaffold competing language implementations. |
| Container Apps environment, scaling plan (including scale-to-zero) and container registry | Confirm availability, identity integration, networking, persistence and costs together. |
| Model name/version/deployment type and quota | Confirm actual access and processing geography; resource location alone is insufficient. |
| SQL configuration and connection identity | Price the database and establish least-privilege runtime/migration access. |
| Network and organisational policy | Determine permitted endpoints, private connectivity, DNS and egress before deploy. |
| Rehearsal/run schedule and data retention | Include idle resources, logs, versions and teardown consequences in cost planning. |

Do not silently substitute a region or model when blocked. Document the limitation and request a changed decision.

## 3. Infrastructure scope

Create only the selected candidates:

- Dedicated demo resource group, ownership/cost tags and environment naming.
- Container Apps environment and app (scale-to-zero consumption plan) with a container registry for the application image.
- Evidence storage with private access, versioning and explicit lifecycle behaviour.
- Coordination SQL database and supported authentication configuration.
- Document Intelligence and selected Azure OpenAI/Foundry resources.
- Entra-integrated application authentication and managed identities where supported.
- Application Insights/Log Analytics with bounded retention and sensible data-volume controls.
- Required network resources and role assignments under organisational policy.
- Cost alerts, without treating them as guaranteed spend caps.

Prefer separate evidence and runtime-state storage boundaries: evidence versioning/retention must not unintentionally govern the application's own workflow-state storage. A shared resource must be justified with supported settings and scoped access, not chosen solely to reduce resource count.

No automatic deployment of Fabric, AI Search, AKS, Service Bus, API Management, private registries or reserved model capacity without a documented requirement and price.

## 4. Terraform structure

Use a small root deployment with reusable modules for coherent lifecycle/security boundaries, not one module per individual resource by convention.

Deliver:

- Typed inputs, validation and safe defaults; unresolved security/cost choices require explicit values.
- Pinned compatible Terraform/provider versions and committed dependency lock file.
- A set-once example configuration with no credentials.
- Non-secret outputs sufficient for application deployment.
- Managed-identity/RBAC mappings with minimum practical scope.
- A resource and permission inventory explaining what Terraform creates and what remains externally supplied.

Use existing approved Azure modules where their interfaces fit. Avoid adopting a large module/framework merely to satisfy branding or adding preview provider functionality unnecessarily.

Region defaults to `swedencentral`; changing it is deliberate and visible. Demo-only controls must not be accidentally enabled by a production-sounding variable default.

## 5. Terraform state and secrets

Use a protected remote backend with locking and Entra-based access. State is sensitive even when no credentials are explicitly configured. Restrict read/write permissions and never commit state, plan files containing secrets or credentials.

Resolve the backend bootstrap problem explicitly: document pre-provisioned storage or an independently approved bootstrap step before `terraform init`. Do not make the main deployment depend on a backend that does not yet exist.

Keep backend state storage outside the disposable demo resource group's teardown scope. Define its owner, cost and eventual cleanup separately.

Prefer identity-based connections. Document any unsupported identity integration before introducing a secret store or credential. Do not grant the running application subscription-wide privileges or reuse deployment credentials at runtime.

An app registration or tenant role assignment may require rights the participant lacks; make it a prerequisite or an approved separately managed resource, not a hidden assumption.

## 6. Application and schema deployment are separate

Infrastructure apply does not deploy working application behaviour by itself. Provide an explicit sequence:

1. Preflight and approve the configuration and budget.
2. Bootstrap/use the protected backend.
3. Initialise, validate and plan Terraform; review destination, cost drivers and changes.
4. After explicit approval, apply infrastructure and verify required endpoints/identity paths.
5. Build/deploy the application package with a supported deployment identity.
6. Apply versioned SQL migrations with appropriate migration privileges.
7. Load approved mock requirements and selected application-input fixtures.
8. Authenticate reviewer/mock-partner roles and exercise the two demo paths.
9. Record app/fixture/resource versions and capture the backup when ready.

No broad `local-exec` chain should obscure application deployment, migration or data import inside Terraform. Do not embed the generator's answer key in app settings, deployment packages or evidence storage.

Keep schema migration capability separate from the runtime identity where practical. If demo constraints require an exception, document it and do not present it as the production design.

## 7. Costs and retention

Produce an itemised monthly estimate using current Sweden Central pricing and actual usage assumptions: Container Apps hosting (including registry), database, OCR/pages, model tokens, evidence versions, logs, networking and remote state.

Reserve contingency below USD 500; do not size exactly to the ceiling. Model/page limits, bounded retries and restricted demo access control variable use. Keep minimum charges and resources that remain billable when compute stops visible.

Propose budget notifications and an operator action before the ceiling is approached. Alerts are not hard enforcement. No pricing estimate is supplied or implied by this brief.

Use synthetic-only retention suitable for repeated rehearsal. Do not lock long WORM retention on disposable evidence. The production design must separately address legal hold, retention obligations, recovery and GDPR-related governance.

## 8. Teardown and recovery

Before removal, confirm environment/run, stop new work, cancel or drain pending demo actions, and preserve the needed evaluation/recording receipts.

Destroy only resources owned by this deployment. Do not delete a shared subscription resource, shared network, retained state backend or other user's data.

Document residual costs from retained storage, database backups or monitoring. A successful teardown command alone does not prove the monthly bill stopped.

Provide an application rollback/redeploy path and demonstrate state-safe worker restart. Document backup/restore and production recovery targets as separate design work; do not claim regional disaster recovery from a single-region demo.

## 9. Acceptance evidence

- Formatting/validation and a reviewed plan are necessary but not proof of deployability.
- After approval, a fresh deployment reaches authenticated operation and both demonstration routes using the configured services.
- No unauthorised users can fetch evidence or mutate cases; managed identities have expected scope.
- Model/SKU/geography configuration matches the approved selection, with no silent fallback.
- Repeated apply is stable apart from explained drift; application re-deploy does not fabricate/reset business results.
- The synthetic-data load excludes staged responses and evaluator data.
- Teardown is demonstrated within owned scope, with retained resources/costs documented.
- Capture the actual deployment and smoke-test outcomes; do not claim them before execution.

Deliver Terraform, configuration examples, build/deploy/migration/seed instructions, permission inventory, cost model and teardown procedure. Reuse the repository's existing tooling; no additional test/build framework without need.
