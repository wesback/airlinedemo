# Terraform configuration boundary

This root records the approved, non-secret deployment configuration from
`deployment/preflight.json` and the bounded settings in
`deployment/cost-model.json`. It provisions only the disposable demo
foundations in two reusable modules:

- `modules/demo-boundary` creates the tagged resource group, separate
  Durable Functions host/task-hub storage, and private versioned evidence
  storage. The 90-day evidence management policy is attached only to the
  evidence account.
- `modules/observability` creates the Log Analytics workspace and Application
  Insights resource, both with 90-day retention.

Terraform does not bootstrap remote state, create runtime/deployment
identities, deploy the application, or create Fabric, AI Search, AKS, Service
Bus, API Management, private registry, reserved-capacity, Foundry Agent
Service, or Microsoft Agent Framework runtime infrastructure.

The example in `examples/demo.tfvars` contains no subscription or tenant
identifier. Authentication and private deployment inputs are supplied by the
operator's Azure/Terraform environment and are not committed here.

The `region` variable defaults to `swedencentral`. The example sets it
explicitly so changing the deployment region is visible in configuration.

The root exposes resource IDs and names plus the non-secret
`application_deployment` settings needed by later application deployment. It
does not expose credentials, storage keys, connection strings, provider
authentication values, Terraform state, or evaluator data.
