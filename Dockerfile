# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

# Copy project files first for layer caching
COPY src/SmartDispenser.Core/SmartDispenser.Core.csproj src/SmartDispenser.Core/
COPY src/SmartDispenser.Infrastructure/SmartDispenser.Infrastructure.csproj src/SmartDispenser.Infrastructure/
COPY src/SmartDispenser.API/SmartDispenser.API.csproj src/SmartDispenser.API/

# Restore before copying source (cache-friendly)
RUN dotnet restore src/SmartDispenser.API/SmartDispenser.API.csproj

COPY . .

RUN dotnet publish src/SmartDispenser.API/SmartDispenser.API.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Runtime stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "SmartDispenser.API.dll"]
