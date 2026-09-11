# Terraform configuration boundary

This root records the approved, non-secret deployment configuration from
`deployment/preflight.json` and the bounded settings in
`deployment/cost-model.json`. It intentionally does not provision resources,
bootstrap remote state, or create runtime/deployment identities. Resource
definitions and the separate application deployment step consume the outputs
from this root later.

The example in `examples/demo.tfvars` contains no subscription or tenant
identifier. Authentication and private deployment inputs are supplied by the
operator's Azure/Terraform environment and are not committed here.

The `region` variable defaults to `swedencentral`. The example sets it
explicitly so changing the deployment region is visible in configuration.

The root exposes only `deployment_name`, `resource_group_name`, `region`, and
the non-secret `application_deployment` settings needed by later application
deployment. It does not expose credentials, provider authentication values,
resource state, or evaluator data.
