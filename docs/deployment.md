# Private Azure deployment manual

## Scope and prerequisites

This procedure deploys this repository to Azure Container Apps with private SQL,
storage, Key Vault, registry, model access and telemetry. The frontend and API
share one signed image. Only committed synthetic demo records are used.

The required Azure arguments are tenant ID and subscription ID. The default
prefix is `demo1` and region is `swedencentral`; override these only when needed.
Resource names, identity IDs, network ranges, the approved model, backend files
and signing configuration are discovered or generated. The operator's current
public IPv4 address and the private executor's NAT address are added as exact
`/32` ingress rules.

Before starting:

- Install Azure CLI, Terraform 1.17 or later, Python 3, Git, GitHub CLI and
  `ssh-keygen` on a macOS/Linux operator machine. Bash runs the entry point.
  Windows operators can use an approved Linux environment.
- Clone `wesback/airlinedemo`, check out the reviewed revision, and authenticate
  `gh`. The existing GitHub OIDC identities are specific to this repository;
  deploying a fork requires a separately reviewed federation change.
  Commit and publish deployable changes before remote execution. The script
  refuses tracked source edits, verifies the published commit and checks the
  executor's exact committed source archive digest. Untracked local source is
  not deployed; operator settings travel separately through protected transport.
- Obtain subscription provisioning and role-assignment rights, permission to
  register Entra applications, and permission to read the signed-in user.
  Activate any time-limited privileges for the complete deployment window.
  SQL initialization uses the approved operator, not a SQL password.
- Confirm regional model availability, quota, network ranges and organizational
  policies. Defaults use separate application and executor networks
  (`10.20.0.0/16` and `10.21.0.0/16`). Review the generated variable file before
  confirming plans. A budget alert is not a spending cap.
- Be a repository administrator, or have an administrator complete the GitHub
  prerequisites below. Azure and GitHub interactive sign-in, plan confirmation,
  and personal release approval cannot be replaced by tenant/subscription IDs.

The deployment adds a private executor VM, disk, NAT gateway, public **egress**
address, private endpoint and DNS in the protected backend resource group.
The VM has no public NIC address, SSH exposure or password login. It uses
Azure managed Run Command; no inbound management access is required. A separate
private GitHub runner is deployed with the application. Both VMs and the other
Azure services incur costs. Organizational VM shutdown controls remain enabled;
the deployment starts its VMs again when resuming.

## GitHub prerequisites

Preserve the repository's existing protection; the script verifies it rather than
weakening or silently recreating it.

| Setting | Required value |
| --- | --- |
| Repository variable `PRODUCT_OWNER_LOGIN` | The GitHub login of the person approving releases |
| Environment `production-release` | Exactly that user as its sole required reviewer; retain approved branch restrictions |
| Repository secret `RELEASE_GOVERNANCE_TOKEN` | A GitHub token authorized to read this repository's environment protection rules |
| Repository OIDC customization | Immutable subjects matching `infra/terraform/release-identity.tf` |

Use the GitHub repository's **Settings > Secrets and variables > Actions** for
the governance secret, or `gh secret set RELEASE_GOVERNANCE_TOKEN`. Never place
the token value in an issue, a command argument, Terraform inputs or Git.
The release workflow verifies the token's actual permissions; the deployment
preflight can check only that the secret exists. It is not an Azure credential.

The runner registration token is a **different** credential: a repository-scoped
fine-grained PAT with Administration read/write for this repository. Store it
only in Key Vault secret `github-runner-token`. The deployment requests it
through a hidden prompt only if the retained secret is absent or `unset`.
The runner exchanges it for a short-lived registration token. Build and release
Azure access use separate OIDC identities, not this PAT.

Similarly, the synthetic-user demo passcode lives in Key Vault secret
`demo-passcode`. A hidden prompt appears only when it is absent. Neither secret
value is written to the operator's Terraform variable files or printed by the
deployment transport.

## First deployment

Sign in and select the intended Azure context. A context mismatch is rejected
before resource operations:

```sh
az login --tenant <tenant-id>
az account set --subscription <subscription-id>
gh auth status
scripts/deploy.sh --tenant-id <tenant-id> --subscription-id <subscription-id>
```

For the existing `demoair` environment, add `--prefix demoair`. The same prefix
must be used for every resume or teardown command. `--location` overrides the
default region.

The default `all` stage performs the following sequence:

1. Verify GitHub release protection and immutable OIDC configuration.
2. Plan and, after `yes`, provision the independent private executor/backend.
   Existing protected backend resources are adopted through Azure management
   APIs, without storage keys or a public data-plane exception.
3. Display private-executor Azure device sign-in instructions if authentication
   is needed. Complete sign-in yourself, then rerun the same deployment command.
   The executor uses its own protected Azure CLI cache; it does not copy your
   laptop's refresh tokens.
4. Generate the application variables/backend file. Apply the reviewed private
   network foundation before the full platform plan, each with explicit `yes`.
   Data services keep public network access disabled throughout.
5. Initialize SQL contained users using the managed identities' **client-ID**
   SIDs, execute the repository's complete EF migration chain as the migration
   identity, and verify exact migration history and effective application
   permissions. A database lock excludes concurrent bootstrap sessions.
   Temporary migration INSERT permission is revoked even when migration fails;
   the migration identity is detached from the executor in cleanup.
6. Configure GitHub build/release variables, populate only missing secrets, and
   wait for the application's private build runner to register. Existing
   environment protection and GitHub secret values are not changed.
7. Select a successful `push` build of current `main`. If the freshly recreated
   registry lacks that image, preserve the old run artifacts locally and rerun
   the build. CI must support replacement of its named artifacts. Failed CI,
   missing provenance and a missing exact registry digest stop deployment.
8. Dispatch the protected release for that build ID and digest. On first
   deployment it signs/verifies with `bootstrap_only=true`; after the app exists,
   releases also update the running app. The script prints the run URL and
   stops while your personal approval/completion is pending.
9. After you approve the release in GitHub and it succeeds, rerun the same
   command. The recorded release is reused rather than dispatched again. Confirm
   the application plan to create the app from the signed immutable digest.
10. Verify the running digest, API/client, unauthenticated denials, all five
    synthetic roles, three case-pack aircraft, Copilot projections, anti-forgery
    rejection and one real read-only model question. Restart the revision and
    compare per-role case-state digests. Only then promote the fresh deployment
    inputs to the saved operator settings and write `verified.json`.

The script exits nonzero while sign-in, release approval or another prerequisite
is pending. That is a deliberate pause, not permission to skip the gate.
An existing app blocks a platform-only configuration with no image: the normal
deployment command never silently destroys a working app.

## Foundry migration preflight

The Foundry migration is not yet a complete deployment route. The new
`foundry-plan`, `foundry-preflight` and `foundry-apply` stages enforce its evidence
gates; the existing `all`/`platform` route has not yet been migrated. These stages
require an existing authenticated private executor, backend/settings, and the
reviewed Foundry Terraform topology. They refuse a plan without the exact
`azurerm_resource_group_template_deployment.foundry[0]` account/project wrapper.
Passing unit tests does not establish private service compatibility or policy
compliance.
When fresh rebuild settings exist, Foundry planning uses that variable/backend
pair rather than the previous deployment's saved settings. Missing members of
the fresh pair stop planning instead of mixing old and new inputs.

For a retained build runner, set the optional
`build_runner_os_disk_storage_account_type` to its actual OS disk SKU in the
selected settings before planning; the default remains `Premium_LRS`. Supported
values are `Standard_LRS`, `StandardSSD_LRS` and `Premium_LRS`. A mismatched SKU
would replace the VM and is rejected by the Foundry no-deletion guard. Azure
retains ownership of the NAT IP's `FirstPartyUsage` allocation tag; other IP
settings remain Terraform-managed. SQL express vulnerability assessment and
signing-certificate auto-renewal at 80 percent of lifetime are explicitly retained.

The candidate adds a separate APIM API at
`/foundry/projects/airline-demo/openai/v1`. It admits only conversation
create/read/delete, item list/delete and response create/read/delete. Other
operations are explicit deny routes, not a pass-through. The existing model
comparison and Prompt Shields APIs remain separate. Only APIM receives the
project-scoped **Foundry Agent Consumer** role; the application receives no
direct project grant.

`foundry_runtime_profile` is initially null, so the new API rejects every runtime
request until both immutable agent versions have been published and their exact
configuration digests supplied. Setup must populate the two `copilot` and
`investigator` entries with `name`, numeric `version` and lowercase SHA-256
`configuration_id`; it never selects `latest`. Wrong operations, projects and
agent/configuration tuples return `gateway_operation_forbidden`,
`gateway_target_forbidden` and `gateway_agent_forbidden`, respectively. Native
APIM policy compilation and private service authorization still require the
deployment acceptance checks; local rendered-policy tests are not live proof.

The candidate backend registers a lazy gateway-only transport and a SQL-backed
conversation metadata store. The additive `managed_conversations` migration
stores owner/context, remote identifiers, revisions and fences, not a transcript.
Runtime SQL grants add only `SELECT`, `INSERT` and `UPDATE` on that table; cleanup
uses a separate bounded `managed-cleanup` command and a separate identity; its
SQL permissions and scheduler are not installed yet. The command accepts a
protected configuration file containing `projectEndpoint`, `cleanupClientId`,
`server` and `database`, and requires least-privilege SQL and direct Foundry
project permissions. Do not run it until those grants and private endpoint
behavior are accepted. It claims at most 100 bindings, runs for at most ten
minutes, performs at most 1000 deletes, and claims a binding no more than three
times per rolling 24 hours. Item and response deletes are confirmed by a fresh
complete listing before checkpointing; conversation deletion is confirmed by a
not-found read. It assumes no deletion cascade, retains confirmed cleanup
metadata for seven days, and does not claim physical erasure. Scheduling is
P04-T05 and remains uninstalled. Missing SQL configuration fails closed before
remote creation rather than falling back to process memory.
Creation/recovery and fenced completion have production-host tests with a real
SQLite adapter, including lost replies, failed SQL completion and late results.
These are not Azure SQL concurrency or private-service acceptance evidence.
The registered continuation host enforces the observable 16000-input estimate,
2000-output setting, eight submissions, 20 function selections and read-only
500/1000ms retry schedule. Writes are never automatically retried. Malformed
history, changed context and refusal/filter outcomes stop before further tools.
The shared copilot dispatcher terminates on a proposal or native release:
confirmation notes and exact stored receipt bytes are UI-only, never function
outputs sent to Foundry. A committed native release retains its exact receipt
even if later metadata retirement, the deadline or the enclosing question's
audit write fails; its native release audit was committed atomically. Pending
proposals and generated answers still require successful fenced completion.
Managed investigator answer validation reuses the native verbatim-witness and
source/personal-data verifier, adds closed JSON shapes and rechecks every cited
page against the committed synthetic registry.
The copilot and investigator endpoints now use managed turns. They remain
unavailable until the private project, exact agent versions, gateway allowlist
and SQL configuration have passed deployment acceptance; there is no direct-model
production fallback.

Purview is not required for the demo's agents, safety filters or evaluations,
and this deployment does not add a Purview resource or application integration.
Assess policies assigned in the target tenant; internal managed-environment
controls are not requirements for an external tenant. Existing collection
settings are not disabled automatically and must still satisfy the approved
content/export boundary before live interactions.

The candidate is opt-in (`foundry_enabled=true`) and requires reviewed
`foundry_state_profile` and `foundry_model_profile` configuration. The model
profile has exactly `runtime` and `judge`, each with `name`, pinned `version`,
`sku` and positive integral `capacity`. No model version is selected implicitly
or upgraded automatically. These are configuration/evidence inputs, not new
required Azure ID command arguments; automatic derivation remains unfinished.

When the catalog omits capacity bounds, new deployments must use its explicit
default. A retained deployment may keep a different capacity only when the saved
plan proves an unchanged identity/model/SKU and live ARM readback confirms that
exact successful deployment. Every deployment sharing that profile must qualify;
a new deployment cannot borrow the exception. Catalog retirement, known bounds
and full-plan quota checks still apply. Native what-if excludes only `Ignore`
results; the two candidate resources remain mandatory and unexpected changes
are rejected.

Project runtime grants derive their principal and workspace identifier from
read-only ARM outputs. After that deployment completes, a read-only Terraform
module queries the actual Blob container IDs/ACLs and Cosmos database ID,
without provisioning or taking ownership of them. The Blob data source uses
`storage_account_id` for management-plane readback, not legacy data-plane lookup.
Blob data rights require the exact project containers with public access
`private` (the provider's normalized `None`); Cosmos data rights require the
exact `enterprise_memory` database.
Missing, malformed, foreign or public readbacks stop grants instead of using
account-wide data scope.
Separate publisher/evaluator managed identities receive project-scoped custom
roles; neither is the application identity.

The official setup sample supplies the workspace-ID technique, but the selected
preview GET schema does not guarantee that response property. Private native
acceptance must verify the returned identifier, generated store scopes,
provisioning order, AAD connections and actual role enforcement. Offline tests
are not that proof. No competing capability-host or store-container owner is
created, and no operator-supplied workspace ID is required.

Use the same tenant/subscription and retained prefix:

```sh
scripts/deploy.sh --tenant-id <tenant-id> --subscription-id <subscription-id> \
  --prefix demoair --stage foundry-plan
scripts/deploy.sh --tenant-id <tenant-id> --subscription-id <subscription-id> \
  --prefix demoair --stage foundry-apply
```

Before planning, provide private `foundry-estimate-inputs.json` in the existing
deployment state directory. Its three top-level objects are `quotaRequests`,
`usage` and `meterSkus`. `quotaRequests` maps exact regional catalog usage names
to `unit` and additional `requested` amount. `usage` maps official meter IDs to
`unit`, `productName`, positive `quantity` and explicit `usageAssumption`;
`meterSkus` maps the same IDs to exact retail SKU names. Quantities describe the
additional resources and usage window being estimated, not a spending cap.
Neither model deployment capacity-to-quota conversion nor New-runtime store
capacity is guessed. Current authorized capacity evidence must justify the
chosen quantities and quota conversion for the exact planned configuration.
Missing or uncertain inputs stop planning rather than selecting another SKU,
region or public/key route.

`foundry-plan` saves a real Terraform plan on the executor without applying it;
its output log stays private there. A closed-field inspector derives the local
candidate from that saved plan, rejects every deletion/replacement, and admits
only the reviewed Incremental, debug-disabled account/project template. Raw
Terraform plan JSON is never returned through Run Command output. Discovery
then refreshes the selected account, effective assignments, retained inventory,
providers, regional SKU/model availability, quota, official USD retail prices,
and native ARM validate/what-if for the exact template.

At each assessment prompt, inspect the private request/discovery files and use
current authenticated organizational policy sources and source-specific
New-runtime capacity evidence. Write `foundry-assessments.json` containing
`policy` and `capacity` objects. Each includes the current `runId`, UTC
`capturedAt`, `configurationSha256`, `sourceReference`, `sourceSha256` and
`status` (`approved` for policy, `verified` for capacity). Policy also includes
`assignmentSha256`; capacity includes `quotaRequestsSha256`. Use SHA-256 of
canonical sorted compact JSON for that quota mapping. Source hashes identify
the inspected source content, not the changing run/timestamp envelope.
An unavailable source or unknown outcome cannot be approved.

Apply gathers evidence, asks confirmation of the exact plan hash and estimate,
then reacquires everything with a new run ID, including both assessments.
Old assessment files are removed before each prompt. Changed evidence gives
`deployment_prerequisite_failed` and zero apply calls; regenerate the plan and
reconfirm. Missing evidence gives `foundry_preflight_refused`. The accepted
saved plan is copied, made read-only and hash-checked immediately before apply.
Private evidence files are mode `0600`; keep them, internal sources, plan logs
and tenant-linked prices out of Git and public delivery artifacts.

### Publish the managed-agent definitions

Only after the approved Foundry infrastructure plan has been applied and the
private service/API compatibility gates are satisfied, run the publication
stage explicitly:

```sh
scripts/deploy.sh --tenant-id <tenant-id> --subscription-id <subscription-id> \
  --prefix demoair --stage foundry-agents
```

The stage reads the nonsecret `foundry_agent_setup` Terraform output, previews
the exact Copilot and Investigator definition/configuration digests, and asks
for confirmation before any publisher identity is attached. It uses the
separate project-scoped publisher identity only for the publication call and
removes that identity from the executor afterward, including when publication
fails. If that identity is already attached, the stage stops without changing
it. An uncertain create is reconciled from its persisted exact-definition
reservation on a later run; it is never blindly submitted again.

The nonsecret manifest is saved at
`~/.local/state/airlinedemo/<prefix>/managed-agent-release.json` (or under the
configured `DEPLOY_STATE_DIR`). Publishing definitions does not enable the
runtime gateway profile; it remains deny-only until the separately gated
deployment/release flow supplies exact approved agent versions and digests.
The manifest records fixed tool names and exact definition/configuration
digests; those digests bind the complete schemas without sending their larger
payload through the output-limited Run Command channel.

## Saved files and resume

Operator files default to `~/.local/state/airlinedemo/<prefix>/`. Set
`DEPLOY_STATE_DIR` to change the parent directory and keep it consistent on every
invocation. Sensitive files are mode `600`; directories are created mode `700`.

| Path | Purpose |
| --- | --- |
| `bootstrap.tfstate`, `bootstrap.tfvars.json` | Independently owned executor/backend state and inputs |
| `executor_ed25519`, `.pub` | Generated executor administration key; never used to open public SSH |
| `executor.json` | Bootstrap completion marker |
| `fresh/<prefix>/deploy.tfvars.json`, `backend.hcl` | Candidate application inputs, separate from the previous deployment |
| `deploy.tfvars.json`, `backend.hcl` | Verified application inputs |
| `previous-deploy.tfvars.json`, `previous-backend.hcl` | Preserved prior inputs when verification promotes replacements |
| `managed-agent-release.json` | Exact published agent versions, definition/configuration digests and pinned tool metadata |
| `build.json`, `release.json` | Exact build/digest and protected-release identifiers for resume |
| `previous-build-evidence/`, `release-evidence/` | Retained CI and signing evidence |
| `verified.json` | Successful deployment origin and digest |

Back up this directory to an approved encrypted location. **Losing the local
bootstrap state is not repaired by deleting the Azure backend.** Recover the
backup or have its owner explicitly import the independent resources. Keep all
state, binary plans, logs and credentials out of Git.

On the executor, root-only `/var/lib/airlinedemo/` holds Azure authentication,
pinned source checkouts, full Terraform plans/state backups and verification
digests. Run Command console output is truncated by Azure; inspect the retained
full files through authorized private-executor operations before approving a
plan. The source sync includes local deployment changes during development;
release images still come only from the protected push-main CI path.

Every phase can be resumed explicitly:

```sh
scripts/deploy.sh --tenant-id <tenant-id> --subscription-id <subscription-id> \
  --prefix <prefix> --stage <stage>
```

| Stage | Purpose |
| --- | --- |
| `preflight` | Read-only GitHub protection, secret-name and OIDC checks |
| `plan-bootstrap` | Initialize/adopt independent ownership and save the executor plan; no apply |
| `bootstrap` | Provision executor/backend after plan confirmation |
| `login` | Start/check executor device-code authentication |
| `inspect` | Verify private state access and retain a state snapshot |
| `prepare` | Generate candidate files without applying application resources |
| `foundry-plan`, `foundry-preflight`, `foundry-apply` | Save, verify and explicitly apply the gated Foundry infrastructure plan |
| `foundry-agents` | Preview, confirm and publish exact immutable managed-agent definitions |
| `platform` | Plan/apply private network foundation and full platform |
| `database` | Idempotent schema initialization, migrations and permission checks |
| `configure` | GitHub variables, missing Key Vault values, private runner readiness |
| `build` | Wait for or rerun push-main CI, retain and validate its exact digest |
| `release` | Dispatch/resume personally approved signing/release |
| `application` | Require successful release, then plan/apply the application |
| `verify` | Live HTTP/model, signed-digest and restart-durability checks |
| `connect` | Temporary private bridge to the previous application's network |
| `plan-destroy` | Back up state, retain signing/runner material and save a destroy plan |
| `destroy` | Validate and apply that exact plan after explicit confirmation |
| `all` | Normal provisioning/resume sequence; never includes teardown |

`all` starts an existing executor if organizational shutdown stopped it. For
individual remote stages, start it first if necessary:

```sh
az vm start -g rg-<prefix>-tfstate -n vm-<prefix>-deploy
```

Do not run deployments concurrently or reuse a saved plan after any state,
configuration, source or workflow-owned image change. Replan instead. The
backend uses Entra authentication and Blob lease locking; binary saved plans
also bind to a state serial.

## Rebuild an existing application from scratch

This procedure **deletes application data**, including SQL cases/history, ACR
images/evidence and telemetry resources. Obtain explicit owner approval, preserve
required evidence and finish reviewing the script changes before proceeding.
It is not the synthetic run-reset action.

Retained resources are the backend resource group/account/container, independent
executor/network, operator backups, GitHub repository/protection/secrets and
unrelated runners. The application Key Vault is soft-deleted and recovered;
purge-protected signing material and the runner PAT are deliberately retained,
not destroyed or regenerated.

```sh
scripts/deploy.sh --tenant-id <tenant-id> --subscription-id <subscription-id> \
  --prefix <prefix> --stage plan-destroy
# Review the saved plan and state backup on the private executor.
scripts/deploy.sh --tenant-id <tenant-id> --subscription-id <subscription-id> \
  --prefix <prefix> --stage destroy
scripts/deploy.sh --tenant-id <tenant-id> --subscription-id <subscription-id> \
  --prefix <prefix>
```

Planning teardown first connects the independent executor to the old app
network and verifies private state/vault resolution. It saves the complete
pre-destroy state and imports the verified temporary peerings/DNS links into the
application state. Terraform dependencies retain that connectivity until
data-plane resource deletion completes, then remove the links before their DNS
zones. It then removes only the signing-certificate and runner-token
resource addresses from Terraform state so destroy does not delete their active
versions. **This changes ownership even though no resources are deleted yet.**
If teardown is abandoned, re-import those exact retained objects before an
ordinary full application plan. Use the retained state backup and versioned
Key Vault IDs; never restore a whole old state over newer state changes.

Destroy validates the saved plan's exact subscription/resource-group ID and
delete-only actions, and checks private backend access, **before** removing the
obsolete backend endpoint attached to the old app VNet. It applies the reviewed
binary plan, verifies the app resource group is absent and removes the temporary
executor bridge. It never destroys `infra/bootstrap`.

On rebuilding with the same prefix, the script recovers the purge-protected
vault with public access still disabled, recreates the operator's scoped vault
roles and imports retained certificate/token versions. There is no purge or
policy-ignore route. The new SQL database and application state start clean.

If destruction is interrupted, keep the executor and backend intact. Inspect
the current state and Azure inventory before creating another destroy plan.
Rerun `--stage plan-destroy`, review its remaining actions, then run
`--stage destroy`. If the app VNet is already gone, planning does not recreate
it: recorded surviving bridge links and the API Management DNS link are adopted
for cleanup. Conflicting or unrecognized DNS ownership stops the script.
Do not force-unlock unless the previous operation is confirmed stopped. Do not
delete the state resource group to solve an application failure.

The low-level `scripts/destroy-deployment.sh` remains available to operators
already on private networking with the explicit environment variables
documented in the operator guide. Its `--plan-only <path>`, `--check-plan <path>`
and `--yes --apply-plan <path>` support reviewing and applying the same plan.
The private entry point is preferred because it also handles preserved material
and obsolete backend connectivity.

## Access and troubleshooting

Open the origin printed after verification from the allowlisted operator
network. Retrieve/distribute the demo passcode through your approved secret
channel, not GitHub or deployment logs. Every reviewer uses the shared synthetic
demo passcode and selects their named demo persona; these are not real employee
identities.

| Symptom | Action |
| --- | --- |
| Browser receives ingress 403 | Check the actual outbound IPv4 address and exact ingress rule. Disconnect a VPN that changes routing. Do not admit a corporate cloud range or all internet callers. |
| Operator address changed | Review/update `presenter_reviewer_cidrs` in candidate settings, then run the reviewed application plan. Keep the executor NAT `/32`. |
| Device sign-in expired or privilege activation ended | Run `--stage login`, complete sign-in/elevation yourself, then resume. |
| Private endpoint request times out | Check private DNS links, peering and endpoint approval from the executor; a public-IP NSP rule does not override disabled public access. |
| SQL initialization fails | Run `--stage database` after correcting the reported private connectivity/grant/migration error. Never grant the app DDL, `db_owner` or audit mutation rights. |
| Private runner remains offline | Inspect its cloud-init/registration log, PAT expiry/Administration permission, vault RBAC/private DNS and organizational shutdown. Keep unrelated runners unchanged. |
| Build is absent or failed | Merge reviewed code to `main` and fix CI. Do not substitute a workflow-dispatch build or a local image for push-main provenance. |
| Release is waiting | Personally approve its GitHub environment deployment, wait for success, then resume. |
| Governance preflight fails | Correct the repository token/protection with its owner; never remove the required reviewer. |
| App health is 200 but browser fallback hides an API error | Use `/api/health`; `/health` is the SPA document, not a health endpoint. |
| Verification/model check fails | Keep the prior saved inputs, inspect private app/gateway logs, fix the actual dependency, and rerun verification. No success marker is written on failure. |

## Validation and evidence

Before using changed deployment code, run:

```sh
python3 -m unittest discover -s scripts/tests -p 'test*.py'
terraform -chdir=infra/bootstrap fmt -check
terraform -chdir=infra/bootstrap validate
terraform -chdir=infra/bootstrap test
terraform -chdir=infra/terraform fmt -check
terraform -chdir=infra/terraform validate
terraform -chdir=infra/terraform test
dotnet test AirlineDemo.sln -m:1
npm --prefix frontend test -- --run
npm --prefix frontend run build
```

Terraform initialization/providers and restored application dependencies are
prerequisites. Repository Terraform tests use mocked providers; do not change
them into live apply tests. Hosted CI must also pass. A workflow change requires
a person's reviewed merge before the new workflow can supply release evidence.

Retain the source revision, reviewed plans, migration head/permission results,
build/run IDs, signing evidence and live verification output in an approved
restricted location. A passing unit test or a plan is not evidence that a fresh
Azure rebuild completed. `verified.json` is written only after the actual HTTP,
model and restart checks succeed.
