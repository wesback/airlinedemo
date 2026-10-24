# Pre-provision cost model

This is an unapproved planning model, not a quote, estimate, budget, or spending limit. Prices remain `STUB` until approved pricing is recorded for the target environment. The lifecycle states mean active operation, idle-but-provisioned operation, and residual operation during removal or retention after teardown is requested. Every source below is the pricing source to check for the configured service; regional availability, discounts, taxes, network charges, and actual usage can change the result.

| Component | Lifecycle | Price | Pricing source | Unit | Configuration assumptions | Contingency | Price status |
|---|---|---|---|---|---|---|---|
| Azure compute | active | STUB | Azure Container Apps pricing | vCPU-time, memory-time, and requests | One Container Apps app replica using the configured half-vCPU and one-GiB container; operation duration and plan are unpriced | Include scale, execution duration, logging, and network egress variance | unapproved |
| Azure compute | idle | STUB | Azure Container Apps pricing | vCPU-time and memory-time | The configured minimum replica remains available while idle; idle duration and any scale-to-zero change are unpriced | Include minimum-replica charges and idle duration variance | unapproved |
| Azure compute | residual | STUB | Azure Container Apps pricing | vCPU-time and memory-time | Charges may continue until the app and environment are confirmed deleted | Include deletion delay and any remaining billable replica time | unapproved |
| Azure SQL | active | STUB | Azure SQL Database pricing | database compute-time and storage | Terraform-managed database; selected service tier, capacity, storage, and workload are unpriced | Include backup storage, compute variation, and data transfer | unapproved |
| Azure SQL | idle | STUB | Azure SQL Database pricing | database compute-time and storage | Database remains provisioned while the demo is idle; no pause or auto-pause saving is assumed | Include minimum provisioned capacity and idle duration variance | unapproved |
| Azure SQL | residual | STUB | Azure SQL Database pricing | database compute-time and retained storage | Database charges may continue until deletion completes; retained backups may remain billable | Include backup retention and delayed deletion | unapproved |
| Azure Blob Storage | active | STUB | Azure Blob Storage pricing | stored data, operations, and data transfer | Standard, locally redundant storage; evidence volume, access tier, operations, and transfer are unpriced | Include transactions, retrieval, growth, and transfer variance | unapproved |
| Azure Blob Storage | idle | STUB | Azure Blob Storage pricing | stored data, operations, and data transfer | Evidence remains stored while idle; lifecycle tiering and access volume are unpriced | Include retained data, versioning, soft-delete, and transaction variance | unapproved |
| Azure Blob Storage | residual | STUB | Azure Blob Storage pricing | retained data, operations, and data transfer | Evidence versions and soft-deleted data may remain during configured retention after teardown | Include retained copies, recovery operations, and final deletion delay | unapproved |
| Azure Container Registry | active | STUB | Azure Container Registry pricing | registry tier-time, stored image data, and transfer | Premium registry; image count, image size, retention, and pulls are unpriced | Include image growth, retention, and transfer variance | unapproved |
| Azure Container Registry | idle | STUB | Azure Container Registry pricing | registry tier-time and stored image data | Premium registry and images remain available while idle; cleanup policy is unpriced | Include minimum tier charge and retained image growth | unapproved |
| Azure Container Registry | residual | STUB | Azure Container Registry pricing | registry tier-time, stored image data, and transfer | Registry and images may remain billable until deletion is confirmed | Include deletion delay and retained image data | unapproved |
| Azure API Management | active | STUB | Azure API Management pricing | tier-time and calls | The gateway runs at the configured tier (default Developer); tier, unit count and call volume are unpriced | Include tier change, call volume and network variance | unapproved |
| Azure API Management | idle | STUB | Azure API Management pricing | tier-time and calls | The gateway stays provisioned while the demo is idle; no pause saving is assumed | Include minimum tier charge and idle duration variance | unapproved |
| Azure API Management | residual | STUB | Azure API Management pricing | tier-time and calls | Gateway charges may continue until deletion completes; a deleted instance may remain recoverable and billable until purged | Include deletion delay and any retained instance | unapproved |
| Azure Monitor (Log Analytics and Application Insights) | active | STUB | Azure Monitor pricing | ingested data, retention and alert rule evaluations | Workspace on pay-per-GB ingestion with 30-day retention plus Application Insights and scheduled alert rules; ingestion volume is unpriced | Include log growth, retries and alert evaluation variance | unapproved |
| Azure Monitor (Log Analytics and Application Insights) | idle | STUB | Azure Monitor pricing | ingested data, retention and alert rule evaluations | Workspace and alert rules remain provisioned while idle; background telemetry volume is unmeasured | Include minimum ingestion and alert rule charges | unapproved |
| Azure Monitor (Log Analytics and Application Insights) | residual | STUB | Azure Monitor pricing | ingested data, retention and alert rule evaluations | Retained logs stay billable until the retention period ends or deletion completes | Include retention period and deletion delay | unapproved |
| Model input tokens | active | STUB | Azure OpenAI model pricing for the configured deployment | input tokens | Global Standard deployment; model, region, input volume, and per-transition token use require approved configuration and measurement | Include retries, prompt growth, and model or regional price changes | unapproved |
| Model input tokens | idle | STUB | Azure OpenAI model pricing for the configured deployment | input tokens | No idle requests are assumed; any health checks or background calls require measurement | Include unexpected calls and retries | unapproved |
| Model input tokens | residual | STUB | Azure OpenAI model pricing for the configured deployment | input tokens | No calls are intended after service removal; any calls before access is revoked are unpriced | Include removal delay and in-flight requests | unapproved |
| Model output tokens | active | STUB | Azure OpenAI model pricing for the configured deployment | output tokens | Global Standard deployment; model, region, output volume, and per-transition token use require approved configuration and measurement | Include retries, response growth, and model or regional price changes | unapproved |
| Model output tokens | idle | STUB | Azure OpenAI model pricing for the configured deployment | output tokens | No idle requests are assumed; any health checks or background calls require measurement | Include unexpected calls and retries | unapproved |
| Model output tokens | residual | STUB | Azure OpenAI model pricing for the configured deployment | output tokens | No calls are intended after service removal; any calls before access is revoked are unpriced | Include removal delay and in-flight requests | unapproved |

Spending ceiling: not set

## Approval record

Cost approval and execution approval are separate decisions. Both the cost scope and the execution scope require explicit approval by the owner of the target environment. This blank template records no approval and authorizes neither provisioning nor expenditure.

```yaml
approverName: ""
approvalDate: ""
authorizedCostScope: ""
authorizedExecutionScope: ""
authorizationStatus: "not authorized"
```

Review this model before any separately authorized provisioning or expenditure. Review, completion of this model, or any unapproved model does not authorize provisioning or expenditure. Until pricing is approved and both cost and execution scopes are explicitly authorized, dependent costs remain `STUB` and provisioning and spend are not authorized.

## Value and break-even

This section supports PRD DD-043 and AC-025. No value below is validated customer savings.

The case-study input is EUR250,000 per idle aircraft-month (case-study stipulation).

Divide by 30.4 days per month (case-study stipulation) to obtain approximately EUR8,200 per recovered idle-aircraft-day (derived).

Platform cost per transition prepared is derived from the rows above. For each component/lifecycle row, multiply its approved unit price by measured units for that row; sum the resulting lifecycle costs and divide by the number of transitions prepared in the same measured period. It remains `STUB` while any component price is unapproved or usage and transition counts are unmeasured.

Break-even days equal platform cost per transition prepared divided by recovered idle-aircraft-day value. While platform cost per transition prepared is `STUB`, break-even days are exactly not calculated.
