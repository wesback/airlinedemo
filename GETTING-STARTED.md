# AirlineDemo getting started

This procedure deploys only the synthetic, demo/mock-only AirlineDemo. It is
not a production deployment, does not contact real partners, and must never
used with customer or regulatory data. Terraform apply, application
deployment, SQL migration, fixture loading, and teardown are separate
operations; the protected Terraform state backend is owned and destroyed
separately.

The sample configuration targets Sweden Central. Allow about 30 minutes for
an initial deployment; Azure capacity, provider registration, and model
deployment can extend that time. The checked-in planning estimate is USD
30.72/month for its stated usage assumptions, or USD 80.72/month including
contingency. It is a planning estimate from the 2026-09-11 price snapshot,
not authorization to spend. Refresh prices and obtain the required approval
before applying. Charges can continue for retained resources and data after
the app stops; see Azure Cost Management after teardown.

## 1. Install prerequisites

The copy-paste install block is for Ubuntu 24.04. On another operating
system, use its official installers for **Git, Azure CLI (`az`), Terraform,
Docker with a running daemon, .NET SDK 10 (`dotnet`), Node.js and npm, `jq`,
`curl`, and `sqlcmd`**. Install `shellcheck` as well if you will run the
demo-script lint check. Terraform 1.9.8 is used below and satisfies
`terraform/versions.tf` (`required_version = ">= 1.9.8"`); keep it at 1.9.8
or newer. Docker must be usable by the current shell without an interactive
password prompt. On Ubuntu, the `docker` group is one way to grant that
access; membership grants root-equivalent control of the machine. The
bootstrap invokes `docker` directly, so a `sudo docker` habit alone is not
sufficient unless the current account has equivalent non-interactive access.

```bash
sudo apt-get update
sudo apt-get install -y curl git jq shellcheck unzip docker.io nodejs npm
sudo systemctl enable --now docker
sudo usermod -aG docker "$USER"
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
az version
terraform version
docker --version
dotnet --version
node --version
npm --version
jq --version
curl --version
sqlcmd --version
shellcheck --version
```

After adding yourself to `docker`, start a new login session before running
the bootstrap (or use `newgrp docker` in an interactive terminal), then
confirm `docker info` can contact the daemon. `docker --version` alone does
not verify daemon access.

If you do not use Ubuntu, install those same named tools using their
official OS-specific instructions; `shellcheck` is needed only for
`npm run lint:demo-scripts`.

Terraform uses these Azure resource providers. Registration requires
subscription permission; confirm all report `Registered` before provisioning:
`Microsoft.App`, `Microsoft.Authorization`, `Microsoft.CognitiveServices`,
`Microsoft.ContainerRegistry`, `Microsoft.Insights`,
`Microsoft.ManagedIdentity`, `Microsoft.OperationalInsights`,
`Microsoft.Resources`, `Microsoft.Sql`, and `Microsoft.Storage`.

## 2. Authenticate and choose the subscription

Obtain the tenant and subscription IDs from the subscription owner. They are
not repository values. Keep this shell open through deployment and teardown;
if you open a new shell, export these values again.

```bash
export AIRLINEDEMO_TENANT_ID="tenant ID supplied by the subscription owner"
export AIRLINEDEMO_SUBSCRIPTION_ID="subscription ID approved for the demo"
az login --tenant "$AIRLINEDEMO_TENANT_ID"
az account set --subscription "$AIRLINEDEMO_SUBSCRIPTION_ID"
az account show --query '{subscriptionId:id,tenantId:tenantId,user:user}' --output json
for provider in Microsoft.App Microsoft.Authorization Microsoft.CognitiveServices \
  Microsoft.ContainerRegistry Microsoft.Insights Microsoft.ManagedIdentity \
  Microsoft.OperationalInsights Microsoft.Resources Microsoft.Sql Microsoft.Storage; do
  az provider register --namespace "$provider"
done
```

Wait until each provider is registered, for example:

```bash
az provider show --namespace Microsoft.App --query registrationState --output tsv
```

## 3. Prepare private Terraform values and remote state

The workload backend in `terraform/backend.tf` uses state resource group
`rg-airlinedemo-state`, storage account `stairlinedemostate`, and container
`tfstate`, with Entra authentication. Before proceeding, the platform/state
owner must complete the separate procedure in
[`terraform/bootstrap/README.md`](terraform/bootstrap/README.md). The
authenticated deployment identity needs the **Storage Blob Data Contributor**
role on the state container, the account firewall must permit the operator's
network, and the signed-in identity must be able to use that backend. A
readable container confirms the state resources exist; verify the role
assignment with the state owner if access is uncertain:

```bash
az storage container show \
  --account-name stairlinedemostate \
  --name tfstate \
  --auth-mode login \
  --query name --output tsv
```

The result must be `tfstate`. The workload script does not create or repair
the protected backend.

From the repository root, copy the example to a private file and fill its
required Azure OpenAI model/version and SQL Entra administrator login/object
ID from the approved service and identity owners. Keep the file out of git.
Get your public IPv4 address with `curl -4fsS https://api.ipify.org`. In
`container_allowed_source_ranges`, replace the documentation-only
`203.0.113.10/32` with that operator public IP as a `/32`; otherwise Container
Apps ingress blocks the bootstrap smoke request and fixture load. Set
`cognitive_allowed_ip_ranges` to the approved public ranges that must call
the Cognitive Services endpoints; do not leave it empty if public access is
required. Restrict both lists to the necessary operator/application egress
addresses.

```bash
cd "$(git rev-parse --show-toplevel)"
cp terraform/examples/demo.tfvars "$HOME/airlinedemo-demo.tfvars"
$EDITOR "$HOME/airlinedemo-demo.tfvars"
export AIRLINEDEMO_OPERATOR_VARS_FILE="$HOME/airlinedemo-demo.tfvars"
```

`AIRLINEDEMO_OPERATOR_VARS_FILE` is the private values file path set above;
the model, model version, `sql_aad_admin_login`, and
`sql_aad_admin_object_id` values come from their respective approved owners.
The `sqlcmd -G` migration authenticates with the signed-in Entra identity:
that identity must be the configured SQL Entra administrator, and its
machine's public IP must be permitted by the Azure SQL firewall. The
Terraform SQL rule only enables Azure services; it does not add a rule for a
workstation. If your network has no approved way to allow that SQL connection,
follow the migration recovery instructions below after the SQL server exists.

Azure OpenAI deployment requires an available approved model/version and
**30,000 tokens/minute** of quota in the selected region. Confirm quota and
Document Intelligence availability before applying; a Terraform value alone
does not reserve Azure capacity.

## 4. Generate fixtures and the scoped caller header

Use one run ID for fixture generation and the deployed image tag. This
example uses `RUN-DEMO-001`; if you change it, use the same value for both
exports and the generator `--run-id`. The bootstrap also reads
`configuration.runId` from `generator-contract.json` when
`AIRLINEDEMO_RUN_ID` is unset.

For the generated baseline package, prefix this scope with `Bearer ` when
setting `AIRLINEDEMO_AUTH_HEADER`:

```text
run=RUN-DEMO-001;airline=AIRLINE-0001;aircraft=MOCK-AC-001;lease=LEASE-0001
```

```bash
export AIRLINEDEMO_RUN_ID="RUN-DEMO-001"
dotnet run --project src/AirlineDemo.Generator -- \
  --output "$PWD/.airlinedemo-fixtures" \
  --run-id "$AIRLINEDEMO_RUN_ID" \
  --profile baseline
export AIRLINEDEMO_FIXTURE_ROOT="$PWD/.airlinedemo-fixtures"
MANIFEST="$AIRLINEDEMO_FIXTURE_ROOT/application-inputs/package-001/manifest.json"
export AIRLINEDEMO_AUTH_HEADER="Bearer run=$(jq -er '.runId' "$MANIFEST");airline=$(jq -er '.airlineId' "$MANIFEST");aircraft=$(jq -er '.aircraftId' "$MANIFEST");lease=$(jq -er '.leaseId' "$MANIFEST")"
```

`AIRLINEDEMO_AUTH_HEADER` is a demo caller scope, **not an Entra token**.
Its format is `Bearer run=<id>;airline=<id>;aircraft=<id>;lease=<id>` with
the optional `;subject=<id>`. The command above reads all four required IDs
from the generated package manifest so the header's `run=`, `airline=`,
`aircraft=`, and `lease=` match the fixture. The generator's
`generator-contract.json`, staged responses, replay events, and evaluator
truth are not uploaded; the loader submits only the declared application
package.

## 5. Bootstrap

Before provisioning, run the available local checks from the repository
root. These gate the bootstrap; they do not deploy or contact Azure:

```bash
npm run lint:demo-scripts
npm run validate:terraform
dotnet test AirlineDemo.slnx
```

The bootstrap first creates the registry, builds and pushes the image, then
applies the full Terraform configuration. After apply it gets the SQL server
FQDN and database name from Terraform outputs (`sql_database_id`'s final
resource-name segment); it does not need pre-exported SQL values.
`AIRLINEDEMO_SQL_SERVER` comes from
`sql_server_fully_qualified_domain_name`, and `AIRLINEDEMO_SQL_DATABASE`
comes from the final name in `sql_database_id`. Both are optional overrides
for unusual environments, not values to copy from Terraform placeholders.
The same exported `AIRLINEDEMO_RUN_ID` used by the generator
sets the `demo-RUN-DEMO-001` image tag in this example.

```bash
export AIRLINEDEMO_RUN_ID="RUN-DEMO-001"
./scripts/bootstrap-demo.sh
```

The bootstrap runs migration, fixture load, then an unauthenticated
`curl` GET of `/` as its HTTP smoke check. `DEMO_BOOTSTRAP_SUCCESS` is printed
only after those steps pass; it includes `resource_group`, `image`,
`app_endpoint`, and `fixture_root`. The app's ingress allow-list means that
the smoke request and later browser/API requests must originate from an
allowed public IP. The fixture loader's successful completion is marked
`DEMO_FIXTURES_SUCCESS`.

For a local command-flow check only (no Azure, Docker, SQL, or network
calls), run `AIRLINEDEMO_DRY_RUN=1 ./scripts/bootstrap-demo.sh`; it prints
`DEMO_BOOTSTRAP_SUCCESS` and the selected image tag but does not prove that a
deployment works.

## 6. Validate

### Verify the deployment

Use the exact `app_endpoint` printed by the successful bootstrap, from a
machine whose public IP is in `container_allowed_source_ranges`. It should
serve the app with HTTP 200. The bootstrap output must also include both
`DEMO_BOOTSTRAP_SUCCESS` and `DEMO_FIXTURES_SUCCESS`.

```bash
APP_ENDPOINT="https://$(az containerapp show \
  --name ca-airlinedemo-swc-demo \
  --resource-group rg-airlinedemo-swc-demo \
  --query properties.configuration.ingress.fqdn \
  --output tsv)"
export AIRLINEDEMO_API_BASE_URL="$APP_ENDPOINT"
curl --fail --silent --show-error --output /dev/null \
  --write-out 'HTTP %{http_code}\n' "$APP_ENDPOINT/"
```

If the fixture load or app's authenticated APIs are exercised manually,
send the same `AIRLINEDEMO_AUTH_HEADER` built from the fixture manifest; a
valid header only scopes a synthetic caller and does not grant access to
other fixture identities.

### Troubleshooting and re-running

- **Terraform apply failed:** fix the reported Azure permission, provider,
  quota, or configuration issue, then rerun `./scripts/bootstrap-demo.sh`.
  Terraform apply is repeatable for the same config and run ID; the script
  reruns its image and deployment steps.
- **Migration failed:** check that the active Entra identity is the configured
  SQL Entra administrator and that its public IP is allowed by the SQL
  firewall. If the SQL server was just created and an operator IP rule was
  not pre-arranged, add the approved rule now. For example, for this example
  deployment:

  ```bash
  OPERATOR_PUBLIC_IP="$(curl -4fsS https://api.ipify.org)"
  az sql server firewall-rule create \
    --resource-group rg-airlinedemo-swc-demo \
    --server sql-airlinedemo-swc-demo \
    --name airlinedemo-operator \
    --start-ip-address "$OPERATOR_PUBLIC_IP" \
    --end-ip-address "$OPERATOR_PUBLIC_IP"
  ./scripts/bootstrap-demo.sh
  ```

  The rerun repeats Terraform apply and then runs migrations again. The
  checked-in versioned migration records version 1 and applies it once, so
  retrying that migration is idempotent. Do not edit migration history;
  resolve any SQL error before retrying.
- **Fixture load failed:** verify `AIRLINEDEMO_AUTH_HEADER` still comes from
  the generated manifest and that its run ID matches the contract and image
  run ID. If bootstrap did not finish and print `app_endpoint`, set it from
  the deployed app, then rerun `./scripts/load-demo-fixtures.sh` from the
  same exported shell:

  ```bash
  export AIRLINEDEMO_API_BASE_URL="https://$(az containerapp show \
    --name ca-airlinedemo-swc-demo \
    --resource-group rg-airlinedemo-swc-demo \
    --query properties.configuration.ingress.fqdn \
    --output tsv)"
  ./scripts/load-demo-fixtures.sh
  ```

  Re-running the same package/run is idempotent; do not reuse a run ID for
  changed fixture content.
- **Ingress or cognitive calls are blocked:** replace the sample
  `203.0.113.10/32` in `container_allowed_source_ranges` with the operator's
  public IP, set the approved `cognitive_allowed_ip_ranges`, and apply the
  reviewed updated values before retrying the relevant step.

## 7. Tear down

Run teardown from the same shell, or re-export the exact
`AIRLINEDEMO_OPERATOR_VARS_FILE`, `AIRLINEDEMO_SUBSCRIPTION_ID`, and
`AIRLINEDEMO_TENANT_ID` values used above. `AIRLINEDEMO_TENANT_ID` is
retained here so a new shell can authenticate to the same tenant.
`destroy-demo.sh` creates a reviewed Terraform destroy plan and requires
explicit confirmation; it does not delete protected state or shared
resources.

```bash
az login --tenant "$AIRLINEDEMO_TENANT_ID"
az account set --subscription "$AIRLINEDEMO_SUBSCRIPTION_ID"
./scripts/destroy-demo.sh
# type: DELETE AIRLINEDEMO DEMO
```

For an explicitly confirmed non-interactive teardown, use
`./scripts/destroy-demo.sh --yes`. Expect `DEMO_DESTROY_SUCCESS`; inspect
Azure Cost Management for retained registry images, monitoring data,
database backups, and the separately owned Terraform state. The state
backend has its own owner-approved cleanup procedure.
