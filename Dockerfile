# ============================================================
# Stage 1 – JS build (frontend + admin SPAs)
# ============================================================
FROM node:24-alpine AS js-build

# Enable corepack so pnpm is available at the exact workspace version
RUN corepack enable

WORKDIR /repo

# Copy manifest files first for better layer caching
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY apps/frontend/package.json ./apps/frontend/
COPY apps/admin/package.json     ./apps/admin/

RUN pnpm install --frozen-lockfile

# Copy the rest of the source
COPY apps/frontend ./apps/frontend
COPY apps/admin    ./apps/admin
# The .env is NOT copied; Vite reads env at build time only for VITE_* vars.
# API calls use relative paths (/api/*) so no VITE_API_URL needed in production.

# Build both SPAs in production mode
RUN pnpm --filter @bosun/frontend build
RUN pnpm --filter @bosun/admin    build

# ============================================================
# Stage 2 – .NET publish (Bosun.Api)
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build

WORKDIR /repo

# Restore NuGet packages (layer-cached until .csproj files change)
COPY global.json Directory.Build.props* Directory.Packages.props* NuGet.Config* ./
COPY apps/backend ./apps/backend
COPY apps/simulator ./apps/simulator

RUN dotnet restore apps/backend/src/Bosun.Api/Bosun.Api.csproj

RUN dotnet publish apps/backend/src/Bosun.Api/Bosun.Api.csproj \
    --no-restore \
    -c Release \
    -o /app/publish

# ============================================================
# Stage 3 – Runtime image
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

# Copy published .NET binaries
COPY --from=dotnet-build /app/publish .

# Copy Vite-built frontend into wwwroot (served at /)
COPY --from=js-build /repo/apps/frontend/dist ./wwwroot

# Copy Vite-built admin SPA into wwwroot/admin (served at /admin/)
COPY --from=js-build /repo/apps/admin/dist ./wwwroot/admin

# Copy the pre-computed RAG embedding cache so the knowledge base
# can be indexed on first boot without any Gemini API calls.
COPY data/rag-embedding-cache.json ./data/rag-embedding-cache.json

# Create the data/db directory; the Render disk mount replaces it at runtime.
RUN mkdir -p /app/data/db

# Render injects environment variables; no .env file needed in the image.
# Render routes external HTTPS → container :8080
# ASPNETCORE_URLS (not ASPNETCORE_HTTP_PORTS) must be set so Program.cs skips its
# loopback-only UseUrls("http://127.0.0.1:...") override and binds to all interfaces.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "Bosun.Api.dll"]
