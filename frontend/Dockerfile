# Build a partir da raiz do repositório:
#   docker build -f frontend/Dockerfile -t gestor-web .
FROM node:22-alpine AS build
WORKDIR /app
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# Caddy com o desafio DNS do DuckDNS e limite de requisições.
FROM caddy:2-builder AS caddy
RUN xcaddy build \
    --with github.com/caddy-dns/duckdns \
    --with github.com/mholt/caddy-ratelimit

# Caddy serve a SPA e faz o proxy da API (infra/caddy/Caddyfile).
FROM caddy:2-alpine
COPY --from=caddy /usr/bin/caddy /usr/bin/caddy
COPY --from=build /app/dist /srv/web
