# Deploying NexusCore with Docker Compose

The production stack in `nexuscore/docker-compose.yml` runs Caddy, the React
frontend, the ASP.NET Core API, MySQL, and a one-shot database provisioning and
migration service. Only Caddy publishes ports 80 and 443; MySQL and the API stay
on the private Compose network.

## Requirements

- A Linux server with Docker Engine and the Compose plugin.
- DNS records for sibling frontend/API hostnames (for example
  `app.example.com` and `api.example.com`) pointing to the server. Forward ports
  80 and 443 to it so Caddy can provision TLS certificates.
- A GitHub Actions Docker Hub account/repository if deploying prebuilt images.

The sibling hostnames are important: the authentication cookie uses
`SameSite=Lax`, so the frontend and API need to be same-site.

## Configure and start

```sh
git clone <repository-url> NexusCoreTeamProject
cd NexusCoreTeamProject/nexuscore
cp deploy.env.example .env
nano .env        # WEB_DOMAIN, API_DOMAIN, DB_ROOT_PASSWORD, DB_PASSWORD, NEXUSCORE_MIGRATOR_PASSWORD, JWT_SECRET
chmod 600 .env
```
`.env` is git-ignored. Secrets reach the container only as environment variables (not in the Dockerfile or the image).

## 2. Start
```bash
docker compose config            # validates interpolation
docker compose up -d --build
docker compose ps                # db healthy, migrate exited 0, api/web/caddy running
docker compose logs -f api       # expect: "CORS origins: https://app.example.com"
```
The first start creates the database, then the one-shot `migrate` service provisions the restricted
application and migration accounts and applies `database/migrations/V001` through `V004`. The API
starts only after migrations complete and verifies the required schema version.

### Optional: publish images to Docker Hub (Module 4 / Container Registry)
```bash
docker login
docker compose build
docker compose push api web      # needs DOCKERHUB_USERNAME and TAG in .env
```
Then on any server: `docker compose pull && docker compose up -d`.

## 3. What was configured (maps to the task)
| Requirement | Where |
|---|---|
| Backend from a Docker image | `NexusCore.Api/Dockerfile` (multi-stage, .NET 10, non-root) |
| `ASPNETCORE_ENVIRONMENT=Production` | `docker-compose.yml` → `api.environment` (also default in the Dockerfile) |
| Database configuration | `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, and `DB_PASSWORD` are environment variables read by `Services/DbService.cs` |
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
`docker compose exec db mysql -uroot -p nexuscore -e "SELECT user_id,username FROM users;"`

For a local build instead, use `docker compose up -d --build`. The migration
service waits for MySQL, provisions separate restricted app and migrator
accounts, and applies checksummed `database/migrations/V*.sql` scripts. The API
starts only after migrations succeed. Subsequent starts skip already-applied
migrations; an edited applied migration intentionally causes the migrator to
fail.

## CI and container publishing

`.github/workflows/ci.yml` builds and tests the .NET solution and builds the
frontend on pushes and pull requests. To enable Docker Hub publishing from the
repository's default branch:

1. Add repository secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`.
2. Add repository variable `PUBLISH_DOCKER_IMAGES=true`.
3. Add repository variables `VITE_API_URL` (for example
   `https://api.example.com/api`), `VITE_HUB_URL` (for example
   `https://api.example.com/hubs/chat`), `VITE_PUBLIC_API_URL`, and
   `VITE_PUBLIC_WEB_URL`.

After checks pass, the workflow publishes `nexuscore-api` and `nexuscore-web`
with `latest` and `sha-<commit>` tags. Set the server's `TAG` to the desired
published tag before pulling and starting the services. The repositories must
be public or the server must already be logged in to Docker Hub.
Publishing is disabled until explicitly enabled with the repository variable.

Automatic deployment is also opt-in. To enable it, first configure the server
with the production Compose checkout and a working `.env`, and enable image
publishing above. Then set the repository variable `AUTO_DEPLOY_ENABLED=true`,
the absolute `DEPLOY_PATH`
(the directory containing `docker-compose.yml`), and secrets `DEPLOY_HOST`,
`DEPLOY_USER`, `DEPLOY_SSH_KEY`, and `DEPLOY_KNOWN_HOSTS`. Verify the host key
before saving `DEPLOY_KNOWN_HOSTS`. The job runs only after successful checks
and image publishing on the default branch; it pulls the commit-specific images,
restarts the Compose services, and checks the API health URL. Leave the variable
unset to keep deployment manual.

## Post-deployment verification

Check that the API is healthy and can serve the frontend:

```sh
curl -fsS https://api.example.com/api/health
docker compose logs --no-color migrate api
```

Open `https://app.example.com`, sign in, and verify in the browser's Network
panel that API and SignalR requests use `https://api.example.com` and
`wss://api.example.com`. Uploaded assets are served through the frontend
hostname by Caddy.

The workflow also has a manual `workflow_dispatch` smoke-test option. Enter the
API origin and exact frontend origin; it checks API health, that the configured
frontend origin receives CORS permission, and that a deliberately unrelated
origin does not.

Equivalent manual preflight checks:

```sh
curl -si -X OPTIONS https://api.example.com/api/games \
  -H 'Origin: https://app.example.com' \
  -H 'Access-Control-Request-Method: GET'
curl -si -X OPTIONS https://api.example.com/api/games \
  -H 'Origin: https://untrusted.example.invalid' \
  -H 'Access-Control-Request-Method: GET'
```

The first response should include
`Access-Control-Allow-Origin: https://app.example.com` and
`Access-Control-Allow-Credentials: true`; the second must not grant an
`Access-Control-Allow-Origin`. CORS is enforced by browsers, not an
authentication control.

## Operations and submission status

```sh
docker compose logs -f api web db caddy
docker compose pull api web
docker compose run --rm migrate
docker compose up -d --no-build --wait --no-deps api web caddy
docker compose down                  # named volumes are retained
```

Run the migrator before each image update so newly added migration files are
applied; it is safe to rerun because applied migrations are checksum-verified.

No live deployment URL is claimed in this repository. Add the real frontend
and API health URLs to the README only after deploying and verifying them.
