# Terraform configuration boundary

This root records the approved, non-secret deployment configuration from
`deployment/preflight.json` and the bounded settings in
`deployment/cost-model.json`. It provisions only the selected disposable
demo workload in reusable modules:

- `modules/demo-boundary` creates the tagged resource group, separate
  Durable Functions host/task-hub storage, and private versioned evidence
  storage. The 90-day evidence management policy is attached only to the
  evidence account.
- `modules/observability` creates the Log Analytics workspace and Application
  Insights resource, both with 90-day retention.
- `modules/functions` creates a Linux Flex Consumption plan and isolated
  .NET Function App v4 backed by the Durable Functions host storage. Its
  public endpoint is HTTPS-only and deny-by-default, with explicit operator
  CIDR ranges required to allow access.
- `modules/sql` creates the serverless General Purpose Azure SQL server,
  database, and the public firewall rule that permits Azure services.
- `modules/document-intelligence` creates the S0 Document Intelligence
  account with a public endpoint and explicit IP firewall.
- `modules/ai` creates the S0 Azure OpenAI account and one Standard model
  deployment. The exact model and version are required inputs; there is no
  regional or model fallback.

Terraform does not bootstrap remote state, create runtime/deployment
identities, deploy the application, or create Fabric, AI Search, AKS, Service
Bus, API Management, private registry, reserved-capacity, Foundry Agent
Service, or Microsoft Agent Framework runtime infrastructure.

The example in `examples/demo.tfvars` contains no subscription or tenant
identifier. Authentication and private deployment inputs are supplied by the
operator's Azure/Terraform environment and are not committed here.

The `region` variable defaults to `swedencentral`. The example sets it
explicitly so changing the deployment region is visible in configuration.

The root exposes resource IDs, public endpoints, and the non-secret
`application_deployment` settings needed by later application deployment. It
does not expose credentials, storage keys, connection strings, provider
authentication values, Terraform state, or evaluator data. SQL Entra
administrator inputs and the exact model/version are supplied privately after
the readiness gate; identity role assignments remain outside this root.

Application package deployment, SQL migrations, and fixture loading are
separate operator steps. No `local-exec` provisioner is used.
