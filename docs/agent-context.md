# Repository-specific agent context

Use this file for concise, durable facts that help future contributors and
coding agents work in this repository. Keep facts specific, verifiable from
the code, tests, or an explicit maintainer decision. Do not copy run logs,
task summaries, or speculative explanations here.

## Domain invariants

- Case and resource identifiers are opaque strings scoped to a seeded fixture and run and do not encode expected scenario outcomes (`docs/prds/demo-prd.md`).
- A run identifier isolates a demo execution but does not grant access (`docs/prds/demo-prd.md`).
- Mutations require the server-controlled case revision as their optimistic-concurrency precondition (`docs/prds/demo-prd.md`).
- Assessment, policy decision, review disposition, and case acceptance are separate states (`docs/prds/demo-prd.md`).
- Reprocessing an event with the same run and event identifiers is idempotent, while changed canonical content conflicts (`docs/prds/demo-prd.md`).
- Later relevant evidence starts reassessment and can invalidate current acceptance without deleting prior review history (`docs/prds/demo-prd.md`).

## Architecture and integration points

- The solution keeps the ASP.NET Core API, fixture generator, and xUnit tests in separate projects (`AirlineDemo.slnx`).
- The revised deployment target is an ASP.NET Core workload on Azure Container Apps, not an Azure Functions host (`docs/prds/terraform-deployment-brief.md`).
- The API currently persists workflow state as JSON in workflow-state.json under its configured state directory (`src/AirlineDemo.Api/JsonStateStore.cs`).
- The API reads its state directory from AIRLINEDEMO_STATE_DIRECTORY and otherwise uses a state subdirectory beside the application (`src/AirlineDemo.Api/Program.cs`).
- The fixture generator accepts a seed, fixture version, scenario date, run identifier, and output directory (`src/AirlineDemo.Generator/Program.cs`).
- The investigation contract sets limits for model calls, context size, pages, retries, and timeout (`src/AirlineDemo.Api/Contracts.cs`).

## Verification and tooling

- The exact test command is dotnet test, run from the repository root (`.github/workflows/tests.yml`).
- The test project targets net10.0 and uses xUnit (`tests/AirlineDemo.Tests/AirlineDemo.Tests.csproj`).
- CI runs on Ubuntu and installs the .NET 10 SDK before running the test command (`.github/workflows/tests.yml`).
- The solution includes the API, generator, and test project (`AirlineDemo.slnx`).

## Operational workflows

- The application deployment procedure requires five affirmative operator checkpoints before infrastructure apply, image push, or revision deployment (`deployment/application-deployment-procedure.md`).
- Application deployment, versioned SQL migration, and fixture loading are separate steps (`deployment/application-deployment-procedure.md`).
- SQL migrations use a separately authorized Entra-authenticated operator path, not the Container Apps runtime identity (`deployment/sql-migration-procedure.md`).
- The bootstrap script supports a dry run that prints the demo flow without Azure or Docker calls (`scripts/bootstrap-demo.sh`).
- The teardown procedure removes only the Terraform-owned disposable demo after an approved run has ended (`deployment/container-apps-teardown-procedure.md`).
