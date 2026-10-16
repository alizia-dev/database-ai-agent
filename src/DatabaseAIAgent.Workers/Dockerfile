# Build context: repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props DatabaseAIAgent.slnx ./
COPY src/ src/
RUN dotnet restore src/DatabaseAIAgent.Workers/DatabaseAIAgent.Workers.csproj
RUN dotnet publish src/DatabaseAIAgent.Workers/DatabaseAIAgent.Workers.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_RUNNING_IN_CONTAINER=true
USER $APP_UID
ENTRYPOINT ["dotnet", "DatabaseAIAgent.Workers.dll"]
