# syntax=docker/dockerfile:1
# Build context is the repo root (see docker-compose.yml).

# 1) Build the frontend
FROM node:24-alpine AS frontend
WORKDIR /fe
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# 2) Build the backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY backend/*.csproj ./
RUN dotnet restore
COPY backend/ ./
RUN dotnet publish -c Release -o /app/publish

# 3) Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Pinned sockseek (formerly sldl) release — bundled so the repo is self-contained.
ARG SOCKSEEK_VERSION=3.0.4
# Bundled tools: yt-dlp (playlist import), ffmpeg/ffprobe (duration), fpcalc (Chromaprint), sockseek (downloads).
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl ffmpeg libchromaprint-tools \
 && curl -fL https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux -o /usr/local/bin/yt-dlp \
 && curl -fL "https://github.com/fiso64/sockseek/releases/download/v${SOCKSEEK_VERSION}/sockseek_${SOCKSEEK_VERSION}_linux-x64.tar.gz" -o /tmp/sockseek.tgz \
 && tar -xzf /tmp/sockseek.tgz -C /usr/local/bin sockseek \
 && mv /usr/local/bin/sockseek /usr/local/bin/sldl \
 && chmod +x /usr/local/bin/yt-dlp /usr/local/bin/sldl \
 && rm /tmp/sockseek.tgz \
 && apt-get purge -y curl && apt-get autoremove -y && apt-get clean && rm -rf /var/lib/apt/lists/*
COPY --from=backend /app/publish ./
COPY --from=frontend /fe/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
ENV DbPath=/data/crate.db
ENV SldlPath=/usr/local/bin/sldl
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Crate.Api.dll"]
