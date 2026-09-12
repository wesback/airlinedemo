# Protected Terraform state bootstrap

This is a standalone, owner-approved Terraform configuration for the
`airlinedemo` workload state backend. It provisions only the independently
named state resource group, StorageV2 account, private `tfstate` container,
and the one container-scoped `Storage Blob Data Contributor` assignment for
the declared deployment principal. It is not a module of the disposable
demo root and it must not receive the demo resource group name.

## Independent approval and initialization sequence

1. The platform owner approves the state resource names, owner, cost
   allocation, retention period, deployment principal object ID, and the
   explicit operator IP CIDR ranges. Confirm the destination subscription and
   tenant separately; neither is committed in this repository.
2. From this directory, authenticate with an Entra identity authorized to
   create the bootstrap resources, copy
   `examples/bootstrap.tfvars.example` to an operator-only file, replace the
   placeholder values, and run `terraform init`.
3. Run `terraform validate` and `terraform plan -var-file=<operator-file>`.
   Review that the plan contains only this state resource group, state
   StorageV2 account, private container, and the exact container-scoped role
   assignment. Apply only after the independent approval is recorded.
4. From `terraform/`, run `terraform init` after the bootstrap apply. The
   committed `azurerm` backend uses Entra authentication and Azure Blob
   native lease locking. Authenticate the deployment principal; do not
   provide an access key, SAS token, or backend secret.
5. Run the workload root's normal validate, plan, and separately approved
   apply sequence. The workload root never creates or destroys the state
   resources.

## State backend ownership, cost, retention, and cleanup

- **Owner:** `airlinedemo-platform` owns the state account, access review,
  recovery, and approval for any change to its firewall or role assignment.
- **Cost allocation:** charge the state resource group to the
  `airlinedemo-platform` cost center, separately from the disposable
  `airlinedemo-demo` workload cost center.
- **Retention:** retain the state container and its blob versions for the
  lifetime of the workload and for at least 90 days after the final approved
  demo run, subject to the platform owner's recovery policy. State is
  sensitive and must not be copied into the repository.
- **Separate cleanup path:** after the workload has been destroyed and its
  final state backup/retention decision is approved, the platform owner may
  return to this directory and run a reviewed `terraform plan` followed by
  `terraform destroy`. This cleanup is independent and must never be part of
  the disposable demo teardown.

The demo teardown is explicitly prohibited from deleting, importing, or
reusing this state resource group, storage account, container, or role
assignment. Keep the bootstrap state and any operator variable file outside
the repository.
