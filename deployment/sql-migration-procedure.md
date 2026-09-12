# Versioned SQL migration procedure

This procedure is documentation only. Terraform does not execute migrations,
deploy application code, or grant the application runtime schema-changing
permissions.

## Identity boundary

1. The SQL server is configured with the separately supplied Entra
   administrator login and object ID. Those private inputs are supplied to
   Terraform at deployment time and are not committed, emitted as outputs, or
   copied into application settings.
2. A migration operator authenticates to Azure and SQL with Entra
   authentication through the separately supplied SQL Entra administrator path.
   The migration operator applies only the checked-in, ordered,
   versioned migration scripts under `database/migrations/`.
3. The Container Apps system-assigned runtime identity is not a migration
   principal and must never be used to run this procedure or receive schema
   administration permissions.
4. The Terraform-managed user-assigned migration identity is a distinct
   non-secret principal and client identifier. If the approved operating
   model later uses it for migration execution, the SQL Entra administrator
   must provision its database-scoped migration permission separately; that
   assignment is not created by the workload Terraform and is not a runtime
   permission.

No app-registration client secret, storage key, SQL password, connection string,
or secret-store integration is part of this procedure. Entra
authentication and the exact resource/database scope are the only permitted
credential and authorization path.

## Execution boundary

After infrastructure apply and before application startup:

1. Confirm the target server and database from the non-secret Terraform
   outputs and authenticate through the supplied SQL Entra administrator
   path. Do not substitute the Container Apps runtime principal or deployment
   principal.
2. Review the ordered files under `database/migrations/` and
   record the migration version to be applied. A migration is immutable once
   applied; create a new version for a correction.
3. Run the repository's approved Entra-authenticated SQL migration command
   against that exact database. The command must fail if the caller is not
   the supplied SQL Entra administrator path (or the separately authorized
   migration identity) and must not accept a SQL password or connection
   string.
4. Record the applied version and receipt outside Terraform state. Application
   deployment and fixture loading remain separate, explicitly approved steps.

Rollback is a separately reviewed versioned migration. Never use Terraform
destroy, the Container Apps runtime identity, or a deployment credential as a
schema rollback mechanism.
