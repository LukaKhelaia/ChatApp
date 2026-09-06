# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY *.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# PORT is injected at RUNTIME by the host (Render, Fly, Cloud Run...), so it has
# to be expanded by a shell when the container starts - not baked in at build
# time. `ENV ASPNETCORE_URLS=http://+:${PORT}` resolved to an empty port during
# the build and the app failed to bind.
ENTRYPOINT ["/bin/sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet ChatApp.dll"]
