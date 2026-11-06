# Copilot instructions: Aircraft Return Readiness demonstrator

A public-safe demo of aircraft return-readiness investigation. An ASP.NET Core (.NET 10) API owns workflow state, policy, and deterministic decisions. A React 19 + TypeScript (Vite, Fluent UI, react-three-fiber) reviewer UI is built into the same container image. Everything (aircraft, companies, records, decisions) is synthetic. Never add live records, real identifiers, secrets, or private-network addresses to tracked files.

## Build, test, lint

Run from the repository root unless noted.

```sh
# Run locally (frontend dev server proxies /api to :5080, see frontend/vite.config.ts)
ASPNETCORE_URLS=http://localhost:5080 dotnet run --project backend/AirlineDemo.Api
npm ci --prefix frontend
npm --prefix frontend run dev

# Demo walkthrough data: seed the three case-pack aircraft at different gates
Demo__SeedCases=true Demo__SeedJourney=true Demo__Passcode=<passcode> dotnet run --project backend/AirlineDemo.Api

# Full local suite (matches the README "Run locally" block)
npm --prefix frontend run build
npm --prefix frontend test -- --run
dotnet test AirlineDemo.sln --no-restore -m:1
python3 scripts/tests/test_deployment_scripts.py
python3 scripts/tests/test_deploy_script.py
```

Single tests:

```sh
dotnet test AirlineDemo.sln --filter "FullyQualifiedName~SqlCaseStateStoreTests"           # one class
dotnet test AirlineDemo.sln --filter "FullyQualifiedName~StaticClientTests.<MethodName>"    # one method
npm --prefix frontend test -- --run tests/JourneyModel.test.ts                              # one file
npm --prefix frontend test -- --run tests/TopBar.test.tsx -t "<test name>"                  # one case
python3 -m unittest scripts.tests.test_deploy_script.<ClassName>.<test_name>                # one Python case
```

Terraform (CI runs these backend-free with no Azure credentials, Terraform >= 1.17):

```sh
cd infra/terraform && terraform fmt -check -recursive && terraform init -backend=false && terraform validate && terraform test
terraform test -filter=tests/names.tftest.hcl   # one test file
```

There is no separate linter. `npm run build` is the frontend type check (`tsc -b`) and also runs `frontend/scripts/check-hangar-budgets.mjs`, which fails the build when the hangar JS (gzip) or asset size budgets are exceeded. CI ([.github/workflows/build.yml](workflows/build.yml)) additionally runs `node backend/ci/check-sources.mjs` (only nuget.org / registry.npmjs.org package sources; any `.npmrc` registry override fails), gitleaks over tracked files, SBOM generation, and a high/critical vulnerability gate whose only escape hatch is a dated entry in `docs/security/dependency-audit-exceptions.json`.

## Architecture

- **Single deployable.** `backend/AirlineDemo.Api` serves both `/api/*` and the built React client. `StaticClient.cs` serves `wwwroot` with SPA fallback for non-`/api`, extension-less paths. `dotnet publish` runs `npm run build` and stages `frontend/dist` into `wwwroot` unless `-p:SkipClientBuild=true` (the Dockerfile builds the client in a node stage and passes that flag).
- **`Program.cs` is the composition root.** Services are registered through `Add*` extension methods and endpoints through `Map*Endpoints()` extension methods per feature folder (`Auth`, `Copilot`, `Decisions`, `Evidence`, `Finance`, `SourceSystems`, ...). Startup also validates and loads embedded fixtures, publishes the aircraft case pack, and runs the demo seeders before mapping routes. `Program.cs` doubles as two CLIs: `--evaluate-v2` (four-arm evaluation, JSON to stdout, logs to stderr) and the isolated PDF worker switch (`Evidence/PdfWorker`), which re-executes the API binary as a child process.
- **Persistence.** `ICaseStateStore` is selected by `Persistence:CaseStateProvider`: `File` (default, JSON at `CaseState:Path`) or `Sql` (EF Core `CaseStateDbContext` on Azure SQL). Tests use SQLite for the SQL path. Evidence blobs use `IEvidenceBlobStore` (in-memory or private Azure Blob).
- **Source systems.** Five synthetic systems (`technical`, `finance`, `maintenance`, `records`, `contracts`) implement `ISourceSystem<TPayload>` and read embedded JSON from `Fixtures/sources/`. Reads are governed (`GovernedSourceReader`, read budgets/windows). Contract: `docs/source-systems-contract.md`.
- **Model access.** All model calls go through `IChatClient` built in `Program.cs` against a private API Management gateway (`ModelGateway:*` config). `ModelGatewayTokenomics.RequirePrivateGatewayEndpoint` rejects non-private endpoints. The model is refused unless an approved manifest (`config/model-manifest.json`, via `Investigation:ManifestPath`) matches configured deployment, model, and version. `DefaultAzureCredential` is constructed in exactly one place, lazily on first token request; constructing clients must make no network call.
- **Frontend.** `src/journey/` is the default journey board and per-case step-by-step journey (API clients in `api.ts`, `arrivalApi.ts`, `copilotApi.ts`). `src/hangar/` is the lazily loaded 3D hangar for the same aircraft, with a non-3D data-room fallback and reduced-motion support. `src/theme/` holds the "Altivane" Fluent themes. Design tokens live in `src/styles/tokens.css`.
- **Infra.** `infra/bootstrap` is the protected Terraform state backend and private executor, managed separately from `infra/terraform` (the application platform: Container Apps, SQL, Key Vault, private Blob, Foundry/OpenAI behind internal APIM, private build runner). `scripts/deploy.sh` is the end-to-end entry point. See `docs/deployment.md`.
- **Other projects.** `backend/DemoCaseGenerator` (evaluation case generation and cost calculation) and `backend/AirlineDemo.Deployment` (managed Foundry agent setup and cleanup) are referenced by the test project.

## Conventions

- **Roles.** Demo users and roles come from the embedded `Auth/Fixtures/demo-users-v1.json`: `presenter`, `technical-reviewer`, `finance-reviewer`, `legal-reviewer`, `executive`. Authorization is enforced server-side per endpoint. The UI never gates data alone.
- **Endpoint tests go through the real host.** Use `new WebApplicationFactory<Program>().WithWebHostBuilder(...)` so `Program.cs` registrations are exercised, and override configuration or services there (for example `TimeProvider`, `HttpMessageHandler`, store providers). `InternalsVisibleTo` exposes API internals to `AirlineDemo.Api.Tests`. Tests use xUnit v3 with a global `using Xunit`.
- **EF migrations are hand-written** in `Persistence/Migrations/` with `[DbContext]` and `[Migration("yyyyMMddHHmmss_Name")]` and no model snapshot. They branch on `migrationBuilder.ActiveProvider` for SQLite vs SQL Server column types. `SqlCaseStateStoreTests` asserts the latest migration ID is applied, so update it when you add a migration.
- **Fixtures are part of the build.** Source JSON, demo users, and synthetic investigation text are `EmbeddedResource`s with explicit `LogicalName`s. Case-pack and source folders, the model manifests, and the evaluation v2 suite (which lives under the test project's `Fixtures/Evaluation`) are copied to output via the `.csproj`. A new fixture file needs a `.csproj` entry.
- **Startup reads `docs/telemetry/source-system-proposed-additions.md`** relative to the working directory (`Diagnostics/DiagnosticsSetup.cs`). The Dockerfile copies it into the image, so do not move or rename it without updating both.
- **The README is under test.** `frontend/tests/ReadmeContract.test.mjs` asserts README section names and order plus specific commands and links. Run the frontend tests after any README edit.
- **Frontend tests** live in `frontend/tests/` (not next to sources), run under jsdom, and match `*.test.ts`, `*.test.tsx`, and `*.test.mjs`. Some tests read source files or CSS directly as contracts (tokens, build-budget wiring, dependency pins). Search `frontend/tests` for an old token or selector before changing it.
- **Frontend dependencies are pinned exactly** (no `^`/`~` on runtime packages) and the lockfile must resolve only from `registry.npmjs.org`.
- **Python deployment-script tests** use `unittest` with fake `az`/`terraform` shell shims on `PATH`. They never call Azure.
- **Azure policy constraints.** Public network access stays disabled on Storage and Key Vault. Deployer access uses an enforced Network Security Perimeter, not IP rules, and an NSP IP rule does not make a disabled public endpoint reachable. Never introduce public data-service access, shared-key storage, public ingress for private services, or unsigned images.
- **Deployment state stays out of Git.** Per-prefix tfvars, backend config, and plans live in `~/.local/state/airlinedemo/<prefix>/`. Tracked config contains names and secret URIs only.
- **Docs-first features.** Large features start as a PRD in `docs/prd/` (template `docs/templates/prd-template.md`) with stable `FR-`/`NFR-`/`AC-` IDs. Contracts are in `docs/contracts/` and `docs/api/`, and threat models in `docs/security/`.
- **Commits** use Conventional Commits with a directory-derived scope, for example `docs(docs): ...` or `chore(settings): ...`.
