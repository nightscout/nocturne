# Requires build context = repo root (not src/Web/)
# Requires pre-generated API client: run `aspire run` or `dotnet build -t:GenerateClient`
# before building this image.
#
# Build: docker build -t nocturne-web:local -f src/Web/Dockerfile .

FROM node:24-alpine AS build
RUN corepack enable && corepack prepare pnpm@9 --activate
WORKDIR /app

# Install dependencies (layer cached when lockfile unchanged)
COPY src/Web/pnpm-workspace.yaml src/Web/package.json src/Web/pnpm-lock.yaml ./
COPY src/Web/packages/app/package.json packages/app/
COPY src/Web/packages/bridge/package.json packages/bridge/
COPY src/Web/packages/bot/package.json packages/bot/
COPY src/Web/packages/portal/package.json packages/portal/
RUN pnpm install --frozen-lockfile

# Copy source — then reinstall to fix any symlinks overwritten by COPY
COPY src/Web/ .
RUN pnpm install --frozen-lockfile

# Copy pre-generated API client (NSwag output from dotnet build)
COPY src/Web/packages/app/src/lib/api/generated/ packages/app/src/lib/api/generated/

RUN pnpm --filter @nocturne/bridge build 2>/dev/null || true
RUN pnpm --filter @nocturne/bot build 2>/dev/null || true
RUN NODE_OPTIONS="--max-old-space-size=7168" pnpm --filter @nocturne/app run build:sveltekit

# -- Production --
FROM node:24-alpine AS runtime
RUN corepack enable && corepack prepare pnpm@9 --activate
WORKDIR /app
ENV NODE_ENV=production

# Copy workspace structure — server.js must stay in packages/app for pnpm resolution
COPY --from=build /app/pnpm-workspace.yaml ./
COPY --from=build /app/package.json ./
COPY --from=build /app/pnpm-lock.yaml ./
COPY --from=build /app/packages/app/build packages/app/build
COPY --from=build /app/packages/app/server.js packages/app/
COPY --from=build /app/packages/app/package.json packages/app/
COPY --from=build /app/packages/bridge packages/bridge
COPY --from=build /app/packages/bot/package.json packages/bot/
COPY --from=build /app/packages/portal/package.json packages/portal/
RUN pnpm install --frozen-lockfile --prod

EXPOSE 3000
ENV PORT=3000
CMD ["node", "packages/app/server.js"]
