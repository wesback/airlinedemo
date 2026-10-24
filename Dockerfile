FROM node:22.22.2-bookworm-slim AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS build
WORKDIR /src
COPY . .
COPY --from=frontend /src/frontend/dist/ ./backend/AirlineDemo.Api/wwwroot/
RUN dotnet publish backend/AirlineDemo.Api/AirlineDemo.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./
# The telemetry approval file is read at startup from docs/telemetry under the working directory.
COPY --from=build /src/docs/telemetry/source-system-proposed-additions.md ./docs/telemetry/source-system-proposed-additions.md
ENV ASPNETCORE_URLS=http://+:8080
ENV Investigation__ManifestPath=/app/config/model-manifest.json
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["dotnet", "AirlineDemo.Api.dll"]
