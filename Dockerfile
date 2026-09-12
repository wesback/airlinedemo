FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/AirlineDemo.Api/AirlineDemo.Api.csproj src/AirlineDemo.Api/
RUN dotnet restore src/AirlineDemo.Api/AirlineDemo.Api.csproj --locked-mode

COPY src/AirlineDemo.Api/ src/AirlineDemo.Api/
RUN dotnet publish src/AirlineDemo.Api/AirlineDemo.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV AIRLINEDEMO_URLS=http://0.0.0.0:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AirlineDemo.Api.dll", "--serve"]
