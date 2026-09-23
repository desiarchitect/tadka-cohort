# Day 6 scale-out beat: containerizes the monolith so docker-compose can run N replicas behind
# an nginx load balancer. Every earlier day still runs via `dotnet run` — this Dockerfile is
# additive, not a replacement for the local dev workflow. Also the image CI pushes to GHCR for the
# live cloud deploy (ADR-064, .github/workflows/images.yml).
#
# Build context is the repo root (docker-compose and CI build with `context: .`). Since Day 13 the
# monolith references src/Tadka.Telemetry, so that project is copied too (without it, restore fails).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY nuget.config ./
COPY src/Tadka.Telemetry/Tadka.Telemetry.csproj src/Tadka.Telemetry/
COPY src/Tadka.Api/Tadka.Api.csproj src/Tadka.Api/
RUN dotnet restore src/Tadka.Api/Tadka.Api.csproj

COPY src/Tadka.Telemetry/ src/Tadka.Telemetry/
COPY src/Tadka.Api/ src/Tadka.Api/
RUN dotnet publish src/Tadka.Api/Tadka.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Tadka.Api.dll"]
