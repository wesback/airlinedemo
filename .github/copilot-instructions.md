# Copilot instructions

## Repository state and source of truth

- This repository currently contains the .NET test harness and approved design documents; most application projects described below have not been implemented yet. Do not assume planned components already exist.
- `docs/prds/demo-prd.md` owns shared contracts, authority boundaries, state semantics, and acceptance-criterion IDs. The focused briefs in `docs/prds/` inherit from it; resolve discrepancies in favor of the parent PRD or surface them instead of silently choosing.
- `AGENTS.md` contains the repository's planning-pipeline rules. Follow it when creating PRDs, stories, or bug reports. `CLAUDE.md` imports those same rules.

## Build and test commands

The solution and tests target .NET 10.

```bash
# Restore and build the solution
dotnet build AirlineDemo.slnx

# Run the complete test suite (the same effective command used by CI)
dotnet test AirlineDemo.slnx

# Run one test by fully qualified name
dotnet test tests/AirlineDemo.Tests/AirlineDemo.Tests.csproj \
  --filter 'FullyQualifiedName=AirlineDemo.Tests.HarnessSmokeTests.TestHarnessRunsAndAsserts'

# Run a test class
dotnet test tests/AirlineDemo.Tests/AirlineDemo.Tests.csproj \
  --filter 'FullyQualifiedName~AirlineDemo.Tests.HarnessSmokeTests'
```

CI runs `dotnet test` on Ubuntu with the .NET 10 SDK. There is currently no separate repository lint command or lint configuration.

## High-level architecture

The target system is a bounded aircraft-return-readiness demonstrator:

- A .NET 10 isolated Azure Functions application, using Durable Functions, owns ingestion and the case lifecycle.
- Azure SQL is authoritative for workflow, business, and audit state. Durable history tracks execution progress and should contain IDs/references, not document bodies or credentials.
- Blob Storage stores versioned evidence. Evidence access is scoped by case and exact document version; unrestricted blob URLs are not part of the API contract.
- A governed Azure OpenAI deployment performs bounded extraction/classification. Deterministic server-side policy—not model output—decides whether to create a mock evidence request or an internal review task.
- A small web client consumes the API and displays authoritative backend state. It must not calculate permissions, acceptance, or policy outcomes locally.
- A deterministic local generator creates synthetic fixtures, controlled defects, replay events, and evaluator-only truth. Application inputs, staged responses, and evaluator data must remain separated.
- Terraform provisions the selected Azure resources, but infrastructure apply, application deployment, SQL migration, and fixture seeding remain explicit separate steps.

The key flow is: authenticate and validate package -> process every declared file -> create an immutable evidence basis -> produce cited findings -> apply deterministic policy -> persist state/audit/outbox atomically -> dispatch only permitted mock requests -> reassess responses against a new basis.

## Codebase-specific conventions

- Use .NET 10, nullable reference types, and implicit usings. Tests use xUnit and live under `tests/AirlineDemo.Tests`.
- Preserve the PRD contract vocabulary and closed enums. IDs are opaque; actual timestamps are UTC ISO 8601; `caseRevision` is server-controlled and mutations use optimistic concurrency.
- Keep assessment, policy, request delivery, review disposition, and case acceptance as separate states. A `satisfied` finding, completed task, or received response is not automatic acceptance.
- Treat model and document content as untrusted input. Models may read case-scoped evidence but may not choose scope, mutate policy/business state, approve evidence, or initiate arbitrary external actions.
- Make package/event handling idempotent. Identical `(runId, eventId)` replays reuse prior results; reuse with changed canonical payload is a conflict. Deduplicate evidence requests by the stable business `requestKey`, not by model run or evidence basis.
- Preserve immutable evidence bases, reviews, and audit history. Later evidence triggers reassessment and can invalidate current acceptance without deleting prior decisions.
- Enforce authorization from authenticated identity plus resource ownership. `runId`, caller-supplied scope IDs, and UI role selectors never grant access.
- Demo integrations remain explicitly simulated: use synthetic data and a mock partner inbox; never add real partner communication, regulatory approval, invoice, or formal-notice behavior without an approved scope change.
- For PRDs and issues, write concrete, independently verifiable acceptance criteria. Do not use comparative baselines or require build/lint/typecheck evidence unless the deterministic story test command actually runs those checks. Never remove `needs-review` as an automated approval shortcut.
