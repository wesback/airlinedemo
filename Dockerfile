FROM node:22.12.0-bookworm-slim AS frontend
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
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["dotnet", "AirlineDemo.Api.dll"]
