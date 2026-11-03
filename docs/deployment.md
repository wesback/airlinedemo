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
