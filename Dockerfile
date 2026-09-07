# syntax=docker/dockerfile:1

# Multi-stage build: SDK image compiles and publishes, the smaller runtime image
# actually ships. The aspnet image rather than the plain runtime one, because the
# per-player calendar feed is served over HTTP (docs/CALENDAR.md) — the bot itself
# still long-polls and connects outward for everything it does.

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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# CLAUDE.md: tzdata must be present in the image, and the image rebuilt
# periodically — TimeZoneInfo reads the OS zone database, so a stale image
# produces wrong offsets after a country changes its DST rules, silently, with
# no error. Installed explicitly here rather than trusted to the base image, so
# a future base-image change can't drop it unnoticed.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*

# Set explicitly rather than inherited from the base image, so the port Coolify has to be
# told about is written down in the same place as everything else about this container.
# Above 1024, so the unprivileged user below can bind it.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

WORKDIR /app
COPY --from=build /app .

# Runs as its own unprivileged user rather than the image's default root.
RUN useradd --create-home --shell /usr/sbin/nologin quizr
USER quizr

ENTRYPOINT ["dotnet", "Quizr.App.dll"]
