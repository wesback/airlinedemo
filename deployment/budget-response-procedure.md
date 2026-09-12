# Budget-response procedure

Status: planning-only budget response record  
Version: 1.0  
Date: 2026-09-11  
Region: `swedencentral`  
Related estimate: [`cost-model.json`](cost-model.json)  
Configuration source: [`preflight.json`](preflight.json)

This procedure is the operator response for the bounded synthetic rehearsal
configuration. It does not enable alerts, authorize provisioning or spending,
or claim that a deployment has been applied. An Azure subscription owner must
configure any notifications separately after the configuration and budget have
been approved.

## Planning thresholds and actions

The planning ceiling is USD 500 per calendar month. Thresholds apply to the
subscription's month-to-date actual cost and the provider's current-month
forecast, whichever reaches a threshold first. Alerts are notifications only:
they do not stop resources, cap spend, revoke tokens or guarantee that the
ceiling cannot be exceeded.

| Threshold | Named operator action |
| --- | --- |
| USD 250 (50%) | The demo operator records the notification, checks the Azure Cost Management breakdown by resource and meter, confirms that the spend belongs to the demo scope, and pauses any optional rehearsal not already in progress. |
| USD 400 (80%) | The subscription owner and demo operator are notified. The demo operator stops new rehearsals, disables optional fallback/diagnostic workloads, confirms that Container Apps has zero active replicas while idle, verifies SQL auto-pause, checks the container registry image count and inventories retained evidence, versions, logs and state. No additional run starts until the owner records a decision. |
| USD 450 (90%) | The subscription owner records an explicit go/no-go decision. Unless the owner approves a revised estimate, the operator stops the Container Apps workload, prevents new package submissions, removes only disposable unneeded container images and demo evidence/fixtures, and escalates any unexpected shared-resource charge. |
| USD 500 (100%) | Treat the ceiling as approached or exceeded, not as an automatic cap. The operator stops the disposable demo workload, preserves audit and required rehearsal receipts, opens a cost investigation with the subscription owner, and obtains a revised budget record before resuming. The operator must not delete the externally owned remote state backend or shared resources. |

The operator records the alert time, month-to-date actual, forecast, resource and
meter breakdown, action taken, person responsible and restart decision. A
notification received after a rehearsal has started does not imply that the
rehearsal succeeded or that it may continue.

## Pre-ceiling checks

Before the 80% threshold is approached, the named demo operator must:

1. Stop scheduling new rehearsals and allow any active run to reach a safe
   boundary or be cancelled using the application workflow.
2. Inspect Container Apps vCPU/memory consumption and replica activity, container
   registry storage and pulls, Document Intelligence pages, Azure OpenAI
   input/output tokens and retry counts.
3. Confirm the application caps remain 12 runs/month, 48 pages/package, one
   document retry, two model attempts per document, 25,000 tokens/minute and
   90-day retention.
4. Check that Container Apps remains configured for scale-to-zero, the container
   registry has only the approved image retention, SQL pauses as configured,
   database backup growth is expected, and log ingestion, network egress and
   model deployments remain bounded.
5. Remove only disposable synthetic evidence and fixture data that the
   retention policy permits. Preserve the audit record and any receipt needed
   for evaluation.
6. Ask the subscription owner to confirm whether the charge is within this
   estimate or requires a revised estimate and approval.

## Residual and ownership rules

Stopping compute is not a zero-cost teardown. The following can continue to
produce charges:

- Retained evidence blobs and document versions for up to 90 days.
- Azure SQL data files and backup storage while serverless compute is paused.
- Application Insights and Log Analytics ingestion and 90-day retained logs.
- Container Apps environment state and the container registry's retained images.
- The protected remote Terraform state account and lock data.

The disposable demo scope is the resource group
`rg-airlinedemo-swc-demo`, including its Container Apps environment and app,
application container registry, demo storage, demo SQL database, Document
Intelligence resource, demo Azure OpenAI deployment and monitoring resources.
The remote Terraform state backend, subscription, tenant and any shared
organizational network are externally owned backend resources. Demo teardown
must not delete or mutate them; their owner must handle residual charges and
cleanup separately.

## Notification boundary

This procedure defines proposed notification thresholds and human actions only.
It does not configure Azure Cost Management budgets or alerts. Even when an
owner later enables them, provider notifications are not spend caps and do not
replace the named operator actions above.
