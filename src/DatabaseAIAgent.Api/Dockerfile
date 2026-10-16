# Build context: repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props DatabaseAIAgent.slnx ./
COPY src/ src/
RUN dotnet restore src/DatabaseAIAgent.Api/DatabaseAIAgent.Api.csproj
RUN dotnet publish src/DatabaseAIAgent.Api/DatabaseAIAgent.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
# Run as the image's built-in non-root user. Liveness/readiness: GET /health/live and /health/ready
# (probed by the orchestrator: docker-compose, Azure App Service, ECS).
USER $APP_UID
ENTRYPOINT ["dotnet", "DatabaseAIAgent.Api.dll"]
