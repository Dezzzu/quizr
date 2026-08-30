# syntax=docker/dockerfile:1

# Multi-stage build: SDK image compiles and publishes, the smaller runtime image
# actually ships. Long polling means nothing needs to listen on a port (STACK.md),
# so this uses the plain runtime image, not aspnet.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first so `dotnet restore` is cached across builds that only
# change application code.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Quizr.Domain/Quizr.Domain.csproj src/Quizr.Domain/
COPY src/Quizr.App/Quizr.App.csproj src/Quizr.App/
RUN dotnet restore src/Quizr.App/Quizr.App.csproj

COPY src/ src/
RUN dotnet publish src/Quizr.App/Quizr.App.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# CLAUDE.md: tzdata must be present in the image, and the image rebuilt
# periodically — TimeZoneInfo reads the OS zone database, so a stale image
# produces wrong offsets after a country changes its DST rules, silently, with
# no error. Installed explicitly here rather than trusted to the base image, so
# a future base-image change can't drop it unnoticed.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# Runs as its own unprivileged user rather than the image's default root.
RUN useradd --create-home --shell /usr/sbin/nologin quizr
USER quizr

ENTRYPOINT ["dotnet", "Quizr.App.dll"]
