# Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY nexuscore/NexusCore.Api/NexusCore.Api.csproj nexuscore/NexusCore.Api/
RUN dotnet restore nexuscore/NexusCore.Api/NexusCore.Api.csproj

COPY nexuscore/NexusCore.Api/ nexuscore/NexusCore.Api/
WORKDIR /src/nexuscore/NexusCore.Api
RUN dotnet publish NexusCore.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN mkdir -p /app/uploads && chown -R $APP_UID:$APP_UID /app/uploads
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

ENV ASPNETCORE_HTTP_PORTS= \
    ASPNETCORE_ENVIRONMENT=Production \
    API_BIND=0.0.0.0 \
    PORT=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "NexusCore.Api.dll"]
