# Delivery service image (ADR-033). Same shape as src/Tadka.Api/Dockerfile: multi-stage SDK build →
# aspnet runtime. Built by CI and pulled from GHCR by the live cloud deploy (ADR-064). Local dev still
# runs this service with `dotnet run`. Build context = repo root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY nuget.config ./
COPY src/Tadka.Telemetry/Tadka.Telemetry.csproj src/Tadka.Telemetry/
COPY src/Tadka.Delivery.Api/Tadka.Delivery.Api.csproj src/Tadka.Delivery.Api/
RUN dotnet restore src/Tadka.Delivery.Api/Tadka.Delivery.Api.csproj

COPY src/Tadka.Telemetry/ src/Tadka.Telemetry/
COPY src/Tadka.Delivery.Api/ src/Tadka.Delivery.Api/
RUN dotnet publish src/Tadka.Delivery.Api/Tadka.Delivery.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Tadka.Delivery.Api.dll"]
