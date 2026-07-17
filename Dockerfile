# ---------------------------------------------------------------------------
# Multi-stage Dockerfile.
#
# Stage 1 ("build") uses the full .NET SDK image to compile and publish the
# app. Stage 2 ("final") copies only the published output into the much
# smaller ASP.NET runtime image - the SDK never ships to production.
# ---------------------------------------------------------------------------

# ---------- Stage 1: build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project file first and restore packages. Docker caches this
# layer, so packages are only re-downloaded when the .csproj changes.
COPY GcwSheetOptimizer.csproj ./
RUN dotnet restore GcwSheetOptimizer.csproj

# Now copy the rest of the source and publish a release build.
COPY . .
RUN dotnet publish GcwSheetOptimizer.csproj -c Release -o /app/publish

# ---------- Stage 2: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# The app listens on port 8080 inside the container
# (docker-compose maps it to a port on your machine).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "GcwSheetOptimizer.dll"]
