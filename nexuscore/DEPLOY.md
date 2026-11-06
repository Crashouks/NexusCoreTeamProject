# Deploying NexusCore on a home Linux server

Architecture (two different origins, so CORS is really exercised):

```
Browser ──https──> Caddy :443 ──> web  (nginx, React build)          https://app.example.com
                            └───> api  (ASP.NET Core, Docker image)   https://api.example.com
                                    └──> db (MySQL 8.4, internal network only)
```

Replace `example.com` with your real domain. Both names must be **sibling subdomains of one domain**:
the login cookie is `SameSite=Lax`, which is only sent cross-origin when frontend and API are *same-site*.
(A free DuckDNS/No-IP name works too, but then use two names under the same parent.)

## 0. Prerequisites
- Docker Engine + Compose plugin on the server (`docker compose version`)
- DNS A records for `app.` and `api.` pointing to your public IP, and router port-forwarding of **80 and 443** to the server.
  Behind CGNAT / no public IP? Use a Cloudflare Tunnel instead: route both hostnames to the tunnel, and keep Caddy
  (or replace it with tunnel routes: `app.` → `web:80`, `api.` → `api:8080`, `app./uploads/*` → `api:8080`).

## 1. Get the code and configure secrets
```bash
git clone <your-repo> && cd NexusCoreTeamProject-main/nexuscore
cp deploy.env.example .env
nano .env        # WEB_DOMAIN, API_DOMAIN, DB_ROOT_PASSWORD, DB_PASSWORD, JWT_SECRET
chmod 600 .env
```
`.env` is git-ignored. Secrets reach the container only as environment variables (not in the Dockerfile or the image).

## 2. Start
```bash
docker compose config            # validates interpolation
docker compose up -d --build
docker compose ps                # db should be "healthy", others "running"
docker compose logs -f api       # expect: "CORS origins: https://app.example.com"
```
The first start creates the schema from `schema.sql` (MySQL init script) and the API applies its idempotent migrations
(with retry while MySQL warms up).

### Optional: publish images to Docker Hub (Module 4 / Container Registry)
```bash
docker login
docker compose build
docker compose push api web      # needs DOCKERHUB_USER and TAG in .env
```
Then on any server: `docker compose pull && docker compose up -d`.

## 3. What was configured (maps to the task)
| Requirement | Where |
|---|---|
| Backend from a Docker image | `NexusCore.Api/Dockerfile` (multi-stage, .NET 10, non-root) |
| `ASPNETCORE_ENVIRONMENT=Production` | `docker-compose.yml` → `api.environment` (also default in the Dockerfile) |
| `ConnectionStrings` | `ConnectionStrings__DefaultConnection` → read by `Services/DbService.cs` (falls back to the old `DB_*` vars for local dev) |
| Frontend → production API URL | `client/Dockerfile` build args `VITE_API_URL`, `VITE_HUB_URL` (Vite bakes them in at build time) |
| CORS only for the deployed frontend | `Program.cs` policy `FrontendPolicy` + `Helpers/CorsOrigins.cs`: in Production the allow-list is **only** `PUBLIC_WEB_URL` / `CORS_ALLOWED_ORIGINS`; localhost origins exist only in Development; wildcards are rejected; the API refuses to start with an empty list |
| Swagger | disabled in Production (set `ENABLE_SWAGGER=1` to expose it) |

## 4. Verification checklist (acceptance criteria)
Replace the hostnames below.

**Backend up and talking to the database**
```bash
curl -i https://api.example.com/api/health          # 200 {"status":"ok"}
curl -s https://api.example.com/api/games | head -c 300   # JSON (a DB query), not a 500
docker compose logs api | grep -i "migration failed"      # should print nothing
```
Register through the UI, then confirm the row exists:
`docker compose exec db mysql -unexus -p nexuscore -e "SELECT user_id,username FROM users;"`

**Frontend online, calling the remote API**
Open `https://app.example.com`, DevTools → Network: requests go to `https://api.example.com/api/...` (no `localhost`).
```bash
docker compose exec web sh -c "grep -rl 'localhost:5000' /usr/share/nginx/html || echo clean"   # expect: clean
```

**CORS — allowed origin passes**
```bash
curl -si -X OPTIONS https://api.example.com/api/games \
  -H "Origin: https://app.example.com" \
  -H "Access-Control-Request-Method: GET" | grep -i "^HTTP\|access-control"
# expect: 204 + Access-Control-Allow-Origin: https://app.example.com + Access-Control-Allow-Credentials: true
```
**CORS — third-party origin is not granted**
```bash
curl -si -X OPTIONS https://api.example.com/api/games \
  -H "Origin: https://evil.example.org" \
  -H "Access-Control-Request-Method: GET" | grep -i "^HTTP\|access-control"
# expect: NO Access-Control-Allow-Origin header  => the browser blocks the call
```
Browser proof: on any other site's console run
`fetch("https://api.example.com/api/games",{credentials:"include"})` → "blocked by CORS policy".
(CORS is enforced by browsers; curl just shows the headers the browser would act on. It is not an authentication mechanism.)

**Other**
- Login works and the cookie `nc_auth` is `HttpOnly; Secure` (DevTools → Application → Cookies).
- Chat/stream hubs connect (WebSocket to `wss://api.example.com/hubs/...`).
- Rate limiting still returns 429 after >10 login attempts/minute from one IP (confirms real client IPs reach the API).

## 5. Final link for submission
Put the working URL in the repository README:

```
Deployed product: https://app.example.com
API health:       https://api.example.com/api/health
```

## 6. Operations
```bash
docker compose logs -f api web db caddy
docker compose pull && docker compose up -d      # update from registry
docker compose down                               # keeps volumes (database, uploads, certificates)
docker run --rm -v nexuscore_mysql_data:/v -v $PWD:/b alpine tar czf /b/mysql-backup.tgz -C /v .   # cold backup (stop db first)
```
Make a first admin: register in the UI, then
`docker compose exec db mysql -uroot -p nexuscore -e "UPDATE users SET role='admin' WHERE username='YOU';"`
