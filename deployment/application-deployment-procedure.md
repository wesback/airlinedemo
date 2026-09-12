# Container Apps application build and deployment procedure

Status: execution-ready procedure, not an execution receipt  
Version: 1.0  
Workload: `airlinedemo-swc-demo` in `swedencentral`

This procedure describes the separate application lifecycle for the merged
ASP.NET Core workload. It does not claim that infrastructure, an image, a
revision, authenticated operation, migrations, fixture loading, or a rehearsal
has succeeded. Every command below is operator-executed and its output must be
recorded in the operator's deployment receipt; no receipt is committed here.

## Authority and inputs

Use only these checked-in, non-secret sources and Terraform outputs:

- `deployment/preflight.json` for the destination, application port, approved
  base image, registry name, and Container Apps limits.
- `deployment/cost-model.json` and
  `deployment/budget-response-procedure.md` for the current planning estimate,
  residual charges, and cost actions.
- `deployment/identity-mapping.json` for identity ownership and prohibited
  reuse.
- `terraform/examples/demo.tfvars` for the named demo configuration.
- `terraform output` from the approved workload state for resource IDs,
  endpoints, the Container Apps target, and runtime/migration principal IDs.

The operator separately supplies the Azure subscription and tenant, the
approved deployment-principal object ID, and the private Terraform variable
file required by the SQL administrator prerequisite. Those values are not
written in this procedure, source control, image labels, or application
settings. Authenticate with the separately supplied deployment principal
through Azure CLI or its approved non-interactive Entra mechanism; do not
place a credential value in a command, variable file committed to this
repository, or image.

The deployment principal is the only principal used here for Terraform,
registry push, and Container Apps revision update. It is not the Container Apps
system-assigned runtime identity and it is not the Terraform-managed
user-assigned migration identity. The runtime identity is for its exact
resource-scoped data-plane assignments. The migration identity is for the
separately authorized versioned SQL migration path in
[`sql-migration-procedure.md`](sql-migration-procedure.md). Neither identity
may be substituted for deployment credentials.

## Required checkpoints

These are stop points, not descriptive suggestions. Do not run an
infrastructure apply, image push, or revision deployment until all five
checkpoints have an affirmative operator record.

### 1. Preflight checkpoint

- Confirm the checked-in preflight record is still `planning-only` and every
  readiness check has been explicitly confirmed in the operator's approval
  record.
- Confirm the exact model and version, Standard deployment type, quota,
  Container Apps Consumption plan, scale-to-zero limits, registry SKU, SQL
  authentication boundary, and approved ingress ranges.
- Confirm the protected state bootstrap has already been completed by its
  owner. Initialize the workload root only after that owner-approved backend
  exists.
- Confirm the source revision, image tag, target port, and operator-only
  Terraform variable file are recorded before planning.

An unconfirmed check blocks the next checkpoint. Do not silently replace the
region, model, image base, or service tier.

### 2. Cost checkpoint

- Refresh the dated regional meters in the cost model immediately before
  planning.
- Confirm the current estimate plus contingency remains below the USD 500
  planning ceiling and record the residual charges that remain when compute is
  stopped.
- Confirm the operator understands that the estimate and notifications are not
  a spend cap. A changed meter, scope, or usage assumption blocks apply until
  the estimate and approval are updated.

### 3. Destination checkpoint

- Authenticate the separately supplied deployment principal and select the
  privately supplied subscription and tenant.
- Confirm `swedencentral`, `rg-airlinedemo-swc-demo`, and
  `airlinedemo-swc-demo` against `deployment/preflight.json`.
- Confirm the Terraform backend is the protected state container described by
  `terraform/bootstrap/README.md`, not a local or disposable demo state.
- Record the deployment-principal object ID and the selected destination in the
  private receipt. Never use the runtime or migration principal ID as that
  value.

Use the approved Azure CLI authentication mechanism for the separately supplied
deployment principal. These identifiers are destination inputs, not committed
configuration:

```bash
AZURE_SUBSCRIPTION_ID="${AIRLINEDEMO_SUBSCRIPTION_ID:?Set the privately supplied Azure subscription ID}"
AZURE_TENANT_ID="${AIRLINEDEMO_TENANT_ID:?Set the privately supplied Azure tenant ID}"

az login --tenant "$AZURE_TENANT_ID"
az account set --subscription "$AZURE_SUBSCRIPTION_ID"
az account show --query "{subscriptionId:id,tenantId:tenantId,user:user}" -o json
```

### 4. Terraform-plan checkpoint

Run validation and create a reviewable plan. Keep the plan and state outside
the repository because Terraform plans can contain sensitive provider data.
The image reference is supplied as a variable so the planned Container App
image is the exact immutable source tag that will be pushed later.

```bash
REPOSITORY_ROOT="$(git rev-parse --show-toplevel)"
TERRAFORM_ROOT="$REPOSITORY_ROOT/terraform"
CONFIG_FILE="$TERRAFORM_ROOT/examples/demo.tfvars"
OPERATOR_VARS_FILE="${AIRLINEDEMO_OPERATOR_VARS_FILE:?Set this to the approved operator-only Terraform vars file}"
SOURCE_REVISION="$(git -C "$REPOSITORY_ROOT" rev-parse --verify HEAD)"
IMAGE_TAG="sha-$(printf '%s' "$SOURCE_REVISION" | cut -c1-12)"
REGISTRY_NAME="$(jq -r '.application.containerRegistry.name' \
  "$REPOSITORY_ROOT/deployment/preflight.json")"
IMAGE_REPOSITORY="airlinedemo"
IMAGE_REF="${REGISTRY_NAME}.azurecr.io/${IMAGE_REPOSITORY}:${IMAGE_TAG}"
PLAN_FILE="${AIRLINEDEMO_PLAN_FILE:?Set this to an operator-only path outside the repository}"

terraform -chdir="$TERRAFORM_ROOT" init
terraform -chdir="$TERRAFORM_ROOT" validate
terraform -chdir="$TERRAFORM_ROOT" plan \
  -var-file="$CONFIG_FILE" \
  -var-file="$OPERATOR_VARS_FILE" \
  -var="container_image=$IMAGE_REF" \
  -out="$PLAN_FILE"
```

Review the complete plan for the exact resource group, registry, Container App,
environment, identity assignments, model selection, ingress ranges, and
protected-state boundary. Reject any plan that adds credentials, a connection
string, broad role assignment, runtime-host storage, a second workload, or an
unapproved destination. Record the plan path and review result; a plan is not
an applied deployment.

### 5. Operator approval checkpoint

An authorized operator must record all of the following before continuing:

1. Preflight readiness is confirmed.
2. Cost estimate, ceiling, residual charges, and destination are accepted.
3. The complete Terraform plan was reviewed and its plan file hash is recorded.
4. The deployment principal is separately supplied and is distinct from the
   runtime and migration principal IDs.
5. The exact source revision and image tag are approved.

If any item is missing, stop. An approval record authorizes the next command
only; it is not evidence that the command has run or succeeded.

## Build and tag the application image

Build from the repository root with the checked-in `Dockerfile`. The source
revision is the immutable tag input; do not use `latest`, a mutable environment
name, a secret, or an evaluator-only label.

```bash
docker build \
  --pull \
  --file "$REPOSITORY_ROOT/Dockerfile" \
  --tag "$IMAGE_REF" \
  "$REPOSITORY_ROOT"

docker image inspect "$IMAGE_REF" --format '{{.Id}}'
```

Record the source revision, image tag, Dockerfile path, and resulting local
image identifier. A local build is not a registry push and is not evidence that
the application is running.

## Apply infrastructure, push the image, and deploy a revision

After the approval checkpoint, apply exactly the reviewed plan using the
separately supplied deployment principal:

```bash
terraform -chdir="$TERRAFORM_ROOT" apply "$PLAN_FILE"
```

Read target identifiers from Terraform outputs. Do not reconstruct resource
IDs, discover a different resource group, or copy any credential from state:

```bash
CONTAINER_APP_ID="$(terraform -chdir="$TERRAFORM_ROOT" output -raw container_app_id)"
RESOURCE_GROUP_NAME="$(terraform -chdir="$TERRAFORM_ROOT" output -raw resource_group_name)"
REGISTRY_ID="$(terraform -chdir="$TERRAFORM_ROOT" output -raw container_app_registry_id)"
RUNTIME_PRINCIPAL_ID="$(terraform -chdir="$TERRAFORM_ROOT" output -raw container_app_runtime_principal_id)"
MIGRATION_PRINCIPAL_ID="$(terraform -chdir="$TERRAFORM_ROOT" output -raw migration_identity_principal_id)"
DEPLOYMENT_PRINCIPAL_OBJECT_ID="${AIRLINEDEMO_DEPLOYMENT_PRINCIPAL_OBJECT_ID:?Set the separately supplied deployment-principal object ID}"

test "$DEPLOYMENT_PRINCIPAL_OBJECT_ID" != "$RUNTIME_PRINCIPAL_ID"
test "$DEPLOYMENT_PRINCIPAL_OBJECT_ID" != "$MIGRATION_PRINCIPAL_ID"
test "$RUNTIME_PRINCIPAL_ID" != "$MIGRATION_PRINCIPAL_ID"

CONTAINER_APP_NAME="$(az resource show --ids "$CONTAINER_APP_ID" --query name -o tsv)"
az resource show --ids "$REGISTRY_ID" --query "{name:name,resourceGroup:resourceGroup}" -o json
az acr login --name "$REGISTRY_NAME"
docker push "$IMAGE_REF"
az containerapp update \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RESOURCE_GROUP_NAME" \
  --image "$IMAGE_REF" \
  --revision-suffix "$IMAGE_TAG"
```

The three identity comparisons are a required separation check. A failed
comparison stops the procedure. The deployment principal must have only the
approved Terraform, registry-push, and Container Apps deployment permissions;
the runtime and migration identities must not be granted those permissions.
The registry's admin authentication remains disabled.

The final `az containerapp update` creates the revision from the pushed,
immutable image tag. Record the command output and the observed revision name
as deployment evidence. Do not write "deployed", "healthy", or "authenticated
operation succeeded" in advance; those are outcomes that can be recorded only
after a separately approved observation.

## Post-deployment boundary and rollback

Application deployment ends after the revision update and its operator receipt.
SQL schema migration remains the separately approved procedure in
`sql-migration-procedure.md`; fixture generation/loading, mock-partner response
delivery, reviewer authentication, and evaluator-only truth are separate
operator actions and are not part of Terraform or this image.

For a rollback, obtain a new operator approval, select a previously recorded
immutable image tag, and repeat only the image push/revision update commands
against the Terraform-output Container App ID. Do not use Terraform destroy,
the runtime identity, the migration identity, a mutable tag, or a credential
embedded in an image as a rollback mechanism.
