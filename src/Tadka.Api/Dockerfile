# Day 6 scale-out beat: containerizes the monolith so docker-compose can run N replicas behind
# an nginx load balancer. Every earlier day still runs via `dotnet run` — this Dockerfile is
# additive, not a replacement for the local dev workflow.
#
# Build context is the repo root (docker-compose builds with `context: .`) because Tadka.Api has
# no project references today, but keeping the root context means this won't need to change if
# that ever stops being true.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Tadka.Api/Tadka.Api.csproj src/Tadka.Api/
RUN dotnet restore src/Tadka.Api/Tadka.Api.csproj

COPY src/Tadka.Api/ src/Tadka.Api/
RUN dotnet publish src/Tadka.Api/Tadka.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Tadka.Api.dll"]
