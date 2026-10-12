# airlinedemo

This repository is onboarded to the engineering pipeline. Product requirements will be added as an approved PRD.

The reviewer interface uses React + TypeScript. ASP.NET Core owns workflow state, policy, and decisions.

## Run locally

Run the API with `ASPNETCORE_URLS=http://localhost:5080 dotnet run --project backend/AirlineDemo.Api`.
In another terminal, run `npm ci --prefix frontend && npm --prefix frontend run dev`.

Run both test suites from the repository root with `npm --prefix frontend run build && npm --prefix frontend test -- --run && dotnet test AirlineDemo.sln --no-restore -m:1`.

The pipeline gate image for this stack is built from the pipeline checkout with `docker build -t ep-spike-node-dotnet -f scripts/Dockerfile.spike-node-dotnet scripts`.
