# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/SmartDispenser.Core/SmartDispenser.Core.csproj src/SmartDispenser.Core/
COPY src/SmartDispenser.Infrastructure/SmartDispenser.Infrastructure.csproj src/SmartDispenser.Infrastructure/
COPY src/SmartDispenser.API/SmartDispenser.API.csproj src/SmartDispenser.API/
RUN dotnet restore src/SmartDispenser.API/SmartDispenser.API.csproj

COPY src/ src/
RUN dotnet publish src/SmartDispenser.API/SmartDispenser.API.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartDispenser.API.dll"]
