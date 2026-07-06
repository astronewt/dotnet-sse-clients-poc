# Edge: builds the React + Blazor static clients, serves them and reverse-proxies
# the SSE server behind nginx with TLS + HTTP/2. Build context = repo root.

# --- React build ---
FROM node:22-alpine AS react
WORKDIR /app
COPY clients/react/package.json clients/react/package-lock.json ./
RUN npm ci
COPY clients/react/ ./
RUN npm run build

# --- Blazor build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS blazor
WORKDIR /src
COPY clients/blazor/Crw.BlazorClient.csproj ./
RUN dotnet restore
COPY clients/blazor/ ./
RUN dotnet publish -c Release -o /out
# Serve under the /blazor/ subpath.
RUN sed -i 's#<base href="/" />#<base href="/blazor/" />#' /out/wwwroot/index.html

# --- nginx edge ---
FROM nginx:alpine
RUN apk add --no-cache openssl && mkdir -p /etc/nginx/certs && \
    openssl req -x509 -nodes -days 825 -newkey rsa:2048 \
      -keyout /etc/nginx/certs/self.key -out /etc/nginx/certs/self.crt \
      -subj "/CN=localhost"
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=react /app/dist/ /usr/share/nginx/html/react/
COPY --from=blazor /out/wwwroot/ /usr/share/nginx/html/blazor/
EXPOSE 80 443
