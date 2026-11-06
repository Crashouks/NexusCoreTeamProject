# syntax=docker/dockerfile:1
# Build context: nexuscore/NexusCore.Api
# docker build -t YOUR_DOCKERHUB_USERNAME/nexuscore-api:v1.0.0 NexusCore.Api

# ---- Stage 1: build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY NexusCore.Api.csproj ./
RUN dotnet restore NexusCore.Api.csproj
COPY . .
RUN dotnet publish NexusCore.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- Stage 2: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Uploads live in a volume; create it owned by the unprivileged runtime user.
RUN mkdir -p /app/uploads && chown -R $APP_UID:$APP_UID /app/uploads
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

# Program.cs adds its own URL from PORT / API_BIND, so disable the image's default
# ASPNETCORE_HTTP_PORTS to avoid binding the same port twice.
ENV ASPNETCORE_HTTP_PORTS= \
    ASPNETCORE_ENVIRONMENT=Production \
    API_BIND=0.0.0.0 \
    PORT=8080
EXPOSE 8080
USER $APP_UID

# Secrets (ConnectionStrings__DefaultConnection, JWT_SECRET) are NOT baked in: pass them at run time.
ENTRYPOINT ["dotnet", "NexusCore.Api.dll"]
