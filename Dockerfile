# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["NexusCore/NexusCore.csproj", "NexusCore/"]
RUN dotnet restore "NexusCore/NexusCore.csproj"

# Copy the rest of the code and build
COPY . .
WORKDIR "/src/NexusCore"
RUN dotnet build "NexusCore.csproj" -c Release -o /app/build

# Publish Stage
FROM build AS publish
RUN dotnet publish "NexusCore.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080

USER app

