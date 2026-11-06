# syntax=docker/dockerfile:1
# Build context: nexuscore/client
# Vite bakes VITE_* values into the JS bundle at BUILD time, so the production API URL is a build argument.

# ---- Stage 1: build ----
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
ARG VITE_API_URL
ARG VITE_HUB_URL
ARG VITE_PUBLIC_API_URL
ARG VITE_PUBLIC_WEB_URL
RUN test -n "$VITE_API_URL" || (echo "VITE_API_URL build arg is required (e.g. https://api.example.com/api)" && exit 1)
RUN npm run build

# ---- Stage 2: static hosting ----
FROM nginx:1.27-alpine AS final
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
