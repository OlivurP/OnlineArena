# Fælles multi-stage Dockerfile. Vælg service med build-arg: --build-arg SERVICE=AuthService
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG SERVICE
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Shared/Shared.csproj src/Shared/
COPY src/${SERVICE}/${SERVICE}.csproj src/${SERVICE}/
RUN dotnet restore src/${SERVICE}/${SERVICE}.csproj
COPY src/Shared/ src/Shared/
COPY src/${SERVICE}/ src/${SERVICE}/
RUN dotnet publish src/${SERVICE}/${SERVICE}.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG SERVICE
ENV SERVICE_DLL=${SERVICE}.dll
WORKDIR /app
# curl bruges af Docker Compose health checks
RUN apt-get update \
 && apt-get install --yes --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
