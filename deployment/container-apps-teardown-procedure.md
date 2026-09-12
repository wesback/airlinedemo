# Container Apps demonstration teardown procedure

Status: execution-ready procedure, not an execution receipt  
Version: 1.0  
Workload: `airlinedemo-swc-demo` in `swedencentral`

This procedure removes only the Terraform-owned disposable Container Apps
demonstration after an approved run has ended. It complements
[`cost-model.json`](cost-model.json), [`budget-response-procedure.md`](budget-response-procedure.md),
and the protected-state contract in [`../terraform/bootstrap/README.md`](../terraform/bootstrap/README.md).
It does not redefine their cost estimates, state ownership, retention policy,
or independent bootstrap cleanup path. No execution, evaluation result, or
billing outcome is claimed by this document.

## Required inputs and receipts

The operator must use these checked-in, non-secret sources:

- `deployment/preflight.json` for the approved region, deployment name, and
  disposable resource group.
- `deployment/resource-inventory.json` for ownership and resource boundaries.
- `deployment/cost-model.json` and
  `deployment/budget-response-procedure.md` for residual-cost categories and
  cost-recording rules.
- The approved Terraform workload root and its existing remote state. Do not
  initialize or select the `terraform/bootstrap` state as the workload state.

The operator separately supplies the subscription, tenant, authenticated
deployment identity, Terraform variable file, and target run identifier. Keep
those values and the Terraform plan outside the repository. A teardown receipt
must record the target environment, target run, operator approval reference,
Terraform plan hash, destroy result, post-teardown verification results, and
the residual-cost record. Do not put credentials, access tokens, state
contents, or evaluator-only truth in the receipt or this procedure.

## Ordered safeguards

These are blocking checkpoints. Complete and record each checkpoint in order;
an affirmative result at a later checkpoint does not waive an earlier one.

### 1. Confirm the exact environment and run

Before changing anything, the operator and an authorized approver must
confirm all of the following against `deployment/preflight.json`,
Terraform outputs, and the authoritative application state:

- The environment is the disposable `demo` environment,
  `airlinedemo-swc-demo`, in `swedencentral`.
- The resource group is exactly `rg-airlinedemo-swc-demo`; the subscription
  and tenant are the privately supplied destination, not a shared or
  production destination.
- The selected Terraform root is `terraform/`, its backend is the protected
  workload state key, and the state resource group is not the target.
- `TARGET_RUN_ID` identifies the completed or explicitly cancelled synthetic
  run in this environment. It must not identify another user's run, another
  tenant's data, or a run whose authoritative state cannot be read.
- The operator approval names this environment and run and authorizes
  destruction of disposable demo data after the retention decision.

Use read-only checks to establish the destination before planning teardown.
Never infer the target from a UI-selected resource, a caller-supplied
subscription label, or a mutable resource name alone:

```bash
REPOSITORY_ROOT="$(git rev-parse --show-toplevel)"
TERRAFORM_ROOT="$REPOSITORY_ROOT/terraform"
CONFIG_FILE="$TERRAFORM_ROOT/examples/demo.tfvars"
OPERATOR_VARS_FILE="${AIRLINEDEMO_OPERATOR_VARS_FILE:?Set the approved operator-only Terraform vars file}"
TARGET_RUN_ID="${AIRLINEDEMO_TARGET_RUN_ID:?Set the approved target run identifier}"
DESTROY_PLAN="${AIRLINEDEMO_DESTROY_PLAN_FILE:?Set an operator-only destroy-plan path outside the repository}"

az account show --query "{subscriptionId:id,tenantId:tenantId}" -o json
terraform -chdir="$TERRAFORM_ROOT" output -raw resource_group_name
terraform -chdir="$TERRAFORM_ROOT" output -raw deployment_name
```

Record the observed subscription, tenant, resource group, deployment name,
region, state key, target run, and approval reference. Stop if any value does
not match the approved record.

### 2. Stop and drain new demo work

After the environment and run have been confirmed, stop all new work before
destroy planning:

1. Disable the demo submission or scheduling path and record the UTC time.
2. Stop new fixture loads, migrations, mock-partner response delivery, and
   reviewer mutations for this environment.
3. Allow active Container Apps replicas and in-flight runs to reach their
   safe terminal boundary, or cancel them through the approved application
   workflow. Confirm the authoritative state contains no active run.
4. Confirm no deployment, rollback, restore, or SQL migration is in progress.
   A scale-to-zero observation alone is not proof that application work has
   drained.

Record the drain observation, active-run query result, and cancellation or
completion references. If work remains active or a state read fails, stop and
do not run a destroy plan.

### 3. Preserve evaluation and recording receipts

Before destroying any resource, preserve the receipts required to evaluate and
record the target run:

- The authoritative run and case identifiers, final state snapshot, and
  migration version.
- The synthetic fixture manifest hash and staged-response references needed
  to explain the run, without copying evaluator-only truth into application
  state.
- The evaluation receipt: target run, expected scenario, observed outcome,
  evaluator reference, and approval or disposition.
- The recording receipt: recording or capture reference, UTC timestamps,
  source revision/image or Container Apps revision, and SHA-256 hash where
  applicable.

Store these receipts in the approved operator-controlled evidence location and
record their immutable references in the teardown receipt. A missing,
unreadable, or unapproved receipt blocks destruction. Do not delete evaluator
truth, recordings, audit evidence, or required receipts to make teardown
complete.

### 4. Review the bounded Terraform destroy plan

Run the destroy plan only from the workload root, with the exact approved
configuration and private operator variables:

```bash
terraform -chdir="$TERRAFORM_ROOT" init
terraform -chdir="$TERRAFORM_ROOT" plan \
  -destroy \
  -var-file="$CONFIG_FILE" \
  -var-file="$OPERATOR_VARS_FILE" \
  -out="$DESTROY_PLAN"
terraform -chdir="$TERRAFORM_ROOT" show "$DESTROY_PLAN"
```

Review the complete plan and verify that every planned address is one of the
18 disposable resources below:

| Terraform address | Disposable resource |
| --- | --- |
| `module.demo_boundary.azurerm_resource_group.this` | Demo resource group |
| `module.demo_boundary.azurerm_storage_account.evidence` | Evidence storage account |
| `module.demo_boundary.azurerm_storage_management_policy.evidence` | Evidence retention policy |
| `module.observability.azurerm_log_analytics_workspace.this` | Log Analytics workspace |
| `module.observability.azurerm_application_insights.this` | Application Insights |
| `module.container_apps.azurerm_container_registry.application` | Application registry |
| `module.container_apps.azurerm_container_app_environment.this` | Container Apps environment |
| `module.container_apps.azurerm_container_app.this` | Container App |
| `module.container_apps.azurerm_user_assigned_identity.migration` | Migration identity |
| `module.container_apps.azurerm_role_assignment.runtime_evidence_reader` | Runtime evidence-reader assignment |
| `module.container_apps.azurerm_role_assignment.runtime_document_intelligence_user` | Runtime Document Intelligence assignment |
| `module.container_apps.azurerm_role_assignment.runtime_azure_openai_user` | Runtime Azure OpenAI assignment |
| `module.sql.azurerm_mssql_server.this` | Demo SQL server |
| `module.sql.azurerm_mssql_database.this` | Demo SQL database |
| `module.sql.azurerm_mssql_firewall_rule.azure_services` | Demo SQL firewall rule |
| `module.document_intelligence.azurerm_cognitive_account.this` | Demo Document Intelligence account |
| `module.ai.azurerm_cognitive_account.this` | Demo Azure OpenAI account |
| `module.ai.azurerm_cognitive_deployment.this` | Demo Azure OpenAI deployment |

The plan must not include an address outside this set, a resource group
outside `rg-airlinedemo-swc-demo`, or a resource that is not managed by this
Terraform root. Do not replace the reviewed plan with a broad
`az group delete`, subscription-scope deletion, import, or a plan from
`terraform/bootstrap`. An unexpected address, destroy-time error, state-lock
problem, or mixed-user data stops the procedure and is escalated.

An authorized operator must record the plan hash and explicitly approve this
bounded scope before applying it. The plan is a review artifact, not evidence
that destruction has happened.

### 5. Apply the approved destroy plan

Apply only the reviewed plan after all four preceding checkpoints are recorded:

```bash
terraform -chdir="$TERRAFORM_ROOT" apply "$DESTROY_PLAN"
```

Record the exact command result, UTC completion time, and Terraform state
serial. A successful `terraform destroy` or successful apply of a destroy plan
only reports Terraform's result; it does not prove that Azure control-plane
deletion is complete or that billing has stopped.

## Explicit exclusions

This procedure has no authority to destroy, import, mutate, or inspect beyond
the approved disposable resource set:

- **Protected remote state:** never destroy
  `rg-airlinedemo-state`, `stairlinedemostate`, the private `tfstate`
  container, its blob versions or leases, or its container-scoped
  `Storage Blob Data Contributor` assignment. Do not run
  `terraform/bootstrap` destroy. The platform owner controls its separate
  retention and cleanup path.
- **Shared subscription and tenant:** never delete, change policy in, or
  alter the privately supplied subscription or tenant. Do not run a
  subscription-scope or tenant-scope cleanup.
- **Shared network and policy resources:** never delete or change an
  organizational virtual network, subnet, firewall, private endpoint, policy,
  monitoring workspace, or other shared resource not present in the reviewed
  workload-root plan. This deployment does not own shared network resources.
- **Other users' data:** never delete another user, tenant, run, case,
  evidence version, recording, evaluator receipt, database row, or log. If
  data is mixed with this demo or ownership cannot be established, stop and
  escalate instead of broadening the target.

These exclusions remain in force even when a destroy command fails, a resource
is empty, or a cost alert is active. The owner of an excluded resource must
approve and execute any separate cleanup.

## Post-teardown verification and residual-cost record

After the destroy command returns, complete both control-plane and billing
verification. Do not close the teardown from the Terraform exit code alone.

1. Confirm the reviewed destroy plan has no remaining workload-root objects
   and that `rg-airlinedemo-swc-demo` has no remaining disposable resources.
   Record the resource listing, Terraform state result, and any provider
   deletion that remains pending.
2. Confirm the protected remote-state resources still exist and remain
   reachable to the platform owner. Confirm no subscription, tenant, shared
   network, policy resource, or other user's data was changed.
3. Query the subscription's Cost Management actuals and forecast after the
   deletion has propagated. Record the query time, billing period, scope,
   resource and meter breakdown, currency, amount, and operator. A successful
   destroy command is not proof that billing has stopped.
4. Complete the residual-cost table in the teardown receipt and assign an
   owner and next action for every non-zero or pending item:

| Residual category | Required verification and record |
| --- | --- |
| Retained evidence storage, blob versions, and permitted synthetic fixtures | Remaining bytes/versions, retention or deletion date, actual meter amount, and the approved evidence owner |
| Azure SQL database backups and retained database storage | Backup/data status, retention date, actual meter amount, and the SQL owner; do not infer removal from server deletion alone |
| Application Insights and Log Analytics monitoring retention or delayed ingestion | Workspace/log retention status, ingestion/retention amount, query time, and the monitoring owner |
| Protected remote Terraform state and lock/blob storage | State resource identifiers, retained versions/locks, actual meter amount, and the platform owner; this category must remain outside teardown |

Also record any retained registry image, delayed network egress, deletion
lag, tax/credit adjustment, or other charge not covered by the table and
escalate an unexpected charge. Update the cost record using the same ownership
and residual categories as `cost-model.json`; do not claim zero cost when a
meter is delayed or an excluded resource remains billable.

Teardown is closed only when the target-resource verification, exclusion
checks, post-teardown billing query, residual-cost table, and all required
evaluation and recording receipt references are complete and approved.
