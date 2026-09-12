# Controlled recovery and redeployment runbook

Status: documentation and evidence capture only; no deployment, rollback,
restart, restore, or recovery execution is claimed by this runbook  
Version: 1.0  
Workload: the merged AirlineDemo application

This runbook applies to an operator working on the already-merged application.
It records how an approved image change, application rollback, or worker
restart would be performed without changing authoritative workflow or business
results. It is not an execution receipt. The operator must not fill in a
result before the corresponding observation has actually been made.

## Required version-and-receipt record

Create one receipt for each approved redeploy, rollback, or worker restart.
Keep the receipt with the change record outside application state, and make
each value immutable after the operation is observed. The receipt must contain
all of these fields:

| Field | Required value |
| --- | --- |
| Terraform revision or plan reference | The source revision and/or reviewed Terraform plan reference used for the target environment |
| Deployed image or Container Apps revision | The immutable image reference and observed Container Apps revision, if a revision was changed |
| Fixture manifest hash | The SHA-256 hash of the fixture manifest associated with the run; record `not-applicable` only when no fixture run is involved |
| Run identifier | The authoritative run ID whose state was checked or whose smoke test exercised the application |
| Migration version | The highest migration version observed in the target database before and after the operation |
| Smoke-test result | The exact pass/fail result, test name, and observation time; do not pre-fill `pass` |
| Timestamp | UTC ISO 8601 time at which the receipt was completed |

Record the receipt only after collecting the Terraform output or plan
reference, image/revision observation, fixture manifest hash, run ID, migration
version, and smoke-test result. A missing field blocks closure. A receipt
documents evidence; it does not turn an unexecuted command into a successful
deployment.

## Preconditions and evidence capture

1. Obtain an operator approval tied to the change and select an immutable
   image reference or previously observed Container Apps revision. Do not use
   `latest` or a mutable environment tag.
2. Identify the target using the approved Terraform outputs and the same
   authoritative SQL database and case-scoped evidence storage. Do not create a
   replacement database or storage account for a rollback.
3. Before changing the application, capture a read-only state snapshot for the
   affected run IDs: case revision, workflow status, finding and request IDs,
   business-result identifiers, and the latest migration version. Record the
   snapshot reference in the receipt.
4. Confirm that the fixture manifest and run identifier are from the approved
   run. Evaluator-only truth and staged responses are not deployment evidence
   and must not be copied into application state.

If the authoritative state cannot be read or the before-change snapshot cannot
be recorded, stop. Do not infer state from a UI, Durable execution history
alone, logs alone, or a newly generated fixture.

## Application rollback or redeploy

Use this path for an application image rollback or a forward redeploy. It
preserves existing workflow and business results by changing only the
Container Apps revision; SQL, evidence versions, workflow records, findings,
requests, responses, review dispositions, and case decisions remain in place.

1. Freeze new deployment changes and obtain approval for the exact immutable
   image reference or revision. This is an application operation, not a
   database reset.
2. Re-check the before-change state snapshot and verify that the target
   database and evidence store are unchanged.
3. Push the selected immutable image if it is not already in the approved
   registry, then update the existing Container App using the Terraform-output
   resource group and application identity. Keep the same SQL and
   case-scoped evidence configuration.
4. Wait for the new revision to become observable. If it cannot start, stop
   traffic changes and retain the prior known-good revision; do not delete
   persisted results to make the revision appear healthy.
5. Run the approved read-only smoke test against the same authoritative
   database. Verify that the previously captured run IDs, case revisions,
   workflow statuses, finding/request IDs, and business-result identifiers are
   still present and unchanged. A smoke-test pass is not case acceptance.
6. Capture the after-change state and complete every receipt field. If the
   before and after state differs unexpectedly, stop and escalate as an
   integrity incident; do not silently discard, rewrite, or fabricate a
   business result.

Do not use `terraform destroy`, drop or recreate the database, truncate or
delete workflow/business tables, delete evidence versions, reset a run or case,
fabricate a smoke-test result, or silently discard business results as a
rollback or redeploy technique. A failed deployment is recovered by selecting
the prior immutable application revision and rechecking persisted state, not
by resetting authoritative data.

## State-safe worker restart

Use this path when a worker process or Container Apps revision must be
restarted without changing application code or authoritative data.

1. Select the affected worker/revision and run IDs. Record the current
   revision, case revision, workflow status, finding/request IDs, and
   business-result identifiers from the authoritative SQL state in the
   before-change snapshot.
2. Confirm that no migration, fixture load, or approved business mutation is
   in progress. A restart does not authorize a new run, a state reset, or a
   replay with a changed payload.
3. Restart only the existing worker or Container Apps revision through the
   approved platform operation. Do not recreate its database, evidence store,
   workflow records, or business-result records.
4. After the worker is ready, read the same persisted identifiers again.
   Verify that the workflow state and business results are present, that
   `caseRevision` did not move unless an approved mutation occurred, and that
   any replay uses the existing idempotency key and canonical payload.
5. Run the approved read-only smoke test, record its exact result, and complete
   the receipt. An inability to verify the after-state is a failed restart
   operation and must be escalated; it is not permission to fabricate or
   discard evidence.

## Backup, restore, and disaster-recovery boundary

Backup and restore procedures, cross-region replication, and production
disaster-recovery objectives are future design work outside this demonstrator
and are not supplied by this runbook. The single-region demo has no claimed
backup/restore or production disaster-recovery capability. Do not describe an
unwritten restore rehearsal, recovery point objective, recovery time objective,
or regional failover as completed or supported.

If authoritative state is unavailable, stop and escalate to the owner of the
future backup/restore design. Do not substitute a new database, fixture data,
logs, Durable history, or evaluator truth for a restore.
