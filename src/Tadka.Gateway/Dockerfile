# YARP gateway image (ADR-035). Same shape as src/Tadka.Api/Dockerfile, with one difference:
# the gateway serves static files from wwwroot/ (the live-tracker demo, ADR-020). wwwroot/demo is
# gitignored and generated at build by the CopyLiveTrackerDemo MSBuild target from
# samples/live-tracker, so that folder must be in the build stage too. The runtime stage also makes
# sure /app/wwwroot exists: the gateway must never boot without its web root.
# Built by CI and pulled from GHCR by the live cloud deploy (ADR-064). Build context = repo root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY nuget.config ./
COPY src/Tadka.Telemetry/Tadka.Telemetry.csproj src/Tadka.Telemetry/
COPY src/Tadka.Gateway/Tadka.Gateway.csproj src/Tadka.Gateway/
RUN dotnet restore src/Tadka.Gateway/Tadka.Gateway.csproj

COPY src/Tadka.Telemetry/ src/Tadka.Telemetry/
COPY samples/live-tracker/ samples/live-tracker/
COPY src/Tadka.Gateway/ src/Tadka.Gateway/
RUN dotnet publish src/Tadka.Gateway/Tadka.Gateway.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/wwwroot

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Tadka.Gateway.dll"]
