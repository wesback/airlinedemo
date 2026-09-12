# Terraform configuration boundary

This root records the approved, non-secret deployment configuration from
`deployment/preflight.json` and the bounded settings in
`deployment/cost-model.json`. It provisions only the selected disposable
demo workload in reusable modules:

- `modules/demo-boundary` creates the tagged resource group and private
  versioned evidence storage. The 90-day evidence management policy is
  attached only to the evidence account.
- `modules/observability` creates the Log Analytics workspace and Application
  Insights resource, both with 90-day retention.
- `modules/container-apps` creates a Consumption Container Apps environment,
  one application Container App, and one Basic application registry with
  registry admin credentials disabled. The app uses an explicit image,
  target port, one-replica maximum and zero-replica minimum. Public ingress
  is restricted to explicitly supplied CIDR ranges. The app has a
  system-assigned runtime identity and a distinct Terraform-managed
  user-assigned migration identity. Terraform grants the runtime only
  `Storage Blob Data Reader` on evidence storage, `Cognitive Services User`
  on Document Intelligence, and `Cognitive Services OpenAI User` on the
  Azure OpenAI account. Each assignment targets one resource.
- `modules/sql` creates the serverless General Purpose Azure SQL server,
  database, and the public firewall rule that permits Azure services.
- `modules/document-intelligence` creates the S0 Document Intelligence
  account with a public endpoint and explicit IP firewall.
- `modules/ai` creates the S0 Azure OpenAI account and one Standard model
  deployment. The exact model and version are required inputs; there is no
  regional or model fallback.

Terraform does not bootstrap remote state, create runtime/deployment
identities, deploy the application, or create Fabric, AI Search, AKS, Service
Bus, API Management, additional private-network resources, reserved-capacity,
Foundry Agent Service, or Microsoft Agent Framework runtime infrastructure.

Remote state is created by the independent, owner-approved configuration in
`terraform/bootstrap`. Apply that configuration first, then initialize this
root with its Entra-authenticated `azurerm` backend. The state resource group
and container have a separate owner, cost allocation, retention policy, and
cleanup path; disposable demo teardown must not delete or import them.

The example in `examples/demo.tfvars` contains no subscription or tenant
identifier. Authentication and private deployment inputs are supplied by the
operator's Azure/Terraform environment and are not committed here.

The `region` variable defaults to `swedencentral`. The example sets it
explicitly so changing the deployment region is visible in configuration.

The root exposes resource IDs, public service endpoints, identity principal
and client IDs, and the non-secret `application_deployment` settings needed by
later application deployment. It does not expose credentials, storage keys,
connection strings, provider authentication values, Terraform state, or
evaluator data. SQL Entra administrator inputs and the exact model/version are
supplied privately after the readiness gate. The runtime data-plane
assignments are Terraform-managed; schema migration permission for the separate
migration identity is supplied through the SQL Entra administrator path.

The complete lifecycle, scope, prohibited reuse, and assignment ownership
mapping is checked in at `deployment/identity-mapping.json`. The documented
versioned schema-change procedure is
`deployment/sql-migration-procedure.md`; infrastructure apply does not run it.

The complete resource and permission inventory is checked in at
[`deployment/resource-inventory.json`](../deployment/resource-inventory.json).
It covers both the 18 resources in this disposable workload root and the four
resources in the separately owned protected-state bootstrap. Resources that do
not support Azure tags (for example role assignments, firewall rules, and the
model deployment) inherit cost attribution from their tagged parent resource
and dedicated resource group; the inventory records that boundary rather than
claiming unsupported direct tags.

Application lifecycle operations are separate epic-owned steps. Terraform only
provisions the declared Azure resources and does not embed application
execution or data-loading commands.
