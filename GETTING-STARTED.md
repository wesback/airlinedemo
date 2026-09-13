# AirlineDemo getting started

This is the shortest supported path to run the **synthetic, demo/mock-only**
AirlineDemo in Azure. It is not a production deployment, does not connect to
real partners, and must not be used with customer or regulatory data. The
scripts create only the disposable workload described by the checked-in
Terraform configuration; protected remote Terraform state is owned and
destroyed separately.

## 1. Install prerequisites

The commands below install the tools on Ubuntu 24.04. Use the equivalent
official installers on another operating system, then confirm each command
prints a version:

```bash
sudo apt-get update
sudo apt-get install -y curl jq shellcheck unzip docker.io
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
curl -fsSL https://aka.ms/InstallAzureCLIDeb | sudo bash
curl -fsSL https://packages.microsoft.com/keys/microsoft.asc |
  sudo tee /etc/apt/trusted.gpg.d/microsoft.asc >/dev/null
curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/prod.list |
  sudo tee /etc/apt/sources.list.d/mssql-release.list >/dev/null
sudo apt-get update
sudo ACCEPT_EULA=Y apt-get install -y mssql-tools18 unixodbc-dev
sudo ln -sf /opt/mssql-tools18/bin/sqlcmd /usr/local/bin/sqlcmd
curl -fsSL https://releases.hashicorp.com/terraform/1.9.8/terraform_1.9.8_linux_amd64.zip \
  -o /tmp/terraform.zip
sudo unzip -o /tmp/terraform.zip -d /usr/local/bin
az extension add --name containerapp --upgrade
dotnet --version
terraform version
az version
docker --version
jq --version
sqlcmd --version
```

Install `sqlcmd` from Microsoft's SQL command-line tools package if
`sqlcmd --version` is not available. Start Docker before running the bootstrap.
The operator also needs an Azure subscription that permits creation of the
disposable demo resource group.

## 2. Authenticate and choose the subscription

Use the privately supplied tenant and subscription values; do not commit them
to this repository:

```bash
export AIRLINEDEMO_TENANT_ID="<private-tenant-id>"
export AIRLINEDEMO_SUBSCRIPTION_ID="<private-subscription-id>"
az login --tenant "$AIRLINEDEMO_TENANT_ID"
az account set --subscription "$AIRLINEDEMO_SUBSCRIPTION_ID"
az account show --query '{subscriptionId:id,tenantId:tenantId,user:user}' --output json
```

The protected Terraform state owner must have completed its separate state
bootstrap before the workload root is initialized. Keep the state bootstrap
operator file outside this checkout.

## 3. Prepare private Terraform values

Copy the credential-free example, then edit the copy with the exact approved
Azure OpenAI model/version, SQL Entra administrator login/object ID, and
operator IP ranges. Keep this file outside source control:

```bash
cd /path/to/airlinedemo
cp terraform/examples/demo.tfvars "$HOME/airlinedemo-demo.tfvars"
$EDITOR "$HOME/airlinedemo-demo.tfvars"
export AIRLINEDEMO_OPERATOR_VARS_FILE="$HOME/airlinedemo-demo.tfvars"
```

The file must include values for `azure_openai_model`,
`azure_openai_model_version`, `sql_aad_admin_login`, and
`sql_aad_admin_object_id`. The committed example remains intentionally
credential-free.

## 4. Generate and bootstrap the mock demo

Generate one deterministic synthetic fixture run, then run the bootstrap. The
bootstrap first creates only the registry, builds and pushes the immutable
image, and only then applies the full Terraform plan that references that
image. It runs the versioned migration, loads only the selected application
inputs, and performs an authenticated application smoke request.

```bash
dotnet run --project src/AirlineDemo.Generator -- \
  --output "$PWD/.airlinedemo-fixtures" \
  --run-id "RUN-DEMO-001" \
  --profile baseline
export AIRLINEDEMO_FIXTURE_ROOT="$PWD/.airlinedemo-fixtures"
export AIRLINEDEMO_AUTH_HEADER="Bearer <approved-demo-token>"
export AIRLINEDEMO_SQL_SERVER="<terraform-output-sql-server>"
export AIRLINEDEMO_SQL_DATABASE="<terraform-output-database>"
./scripts/bootstrap-demo.sh
```

The generator writes synthetic application inputs plus protected staged,
replay, and evaluator-only artifacts. `load-demo-fixtures.sh` submits only the
selected package manifest and never installs those protected artifacts. The
bootstrap prints a structured success block containing `resource_group`,
`image`, `app_endpoint`, and `fixture_root`. Open `app_endpoint` to view the
demo and use the authenticated API smoke paths with the approved demo
identities.

The successful bootstrap block begins with `DEMO_BOOTSTRAP_SUCCESS`; this is
the operator's searchable completion marker.

To inspect the complete command flow without Azure, Docker, SQL, or network
calls, use:

```bash
AIRLINEDEMO_DRY_RUN=1 ./scripts/bootstrap-demo.sh
```

## 5. Validate and tear down

The repository's deterministic checks are:

```bash
npm run lint:demo-scripts
npm run validate:terraform
dotnet test AirlineDemo.slnx
```

When the demo is finished, destroy only the disposable workload. The script
creates and applies a reviewed Terraform destroy plan, requires an explicit
confirmation, and leaves protected remote state, shared resources, fixture
source files, and any provider-retained billing artifacts alone:

```bash
./scripts/destroy-demo.sh
# type: DELETE AIRLINEDEMO DEMO
```

For a non-interactive operator job, `--yes` is the explicit confirmation:

```bash
./scripts/destroy-demo.sh --yes
```

The teardown prints `DEMO_DESTROY_SUCCESS`, the removed resource group and
application endpoint status, and the remaining protected boundary. A
successful destroy does not prove that billing has stopped: check Azure Cost
Management for retained storage, monitoring retention, database backups, and
the protected Terraform state container.
