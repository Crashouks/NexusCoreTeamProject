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
chmod 600 .env
```

Edit `.env` and set both domains, `DOCKERHUB_USERNAME`, and distinct strong
values for `DB_ROOT_PASSWORD`, `DB_PASSWORD`,
`NEXUSCORE_MIGRATOR_PASSWORD`, and `JWT_SECRET`. The database passwords must
use 16–128 characters from `A-Z a-z 0-9 _@%+=.,:-`; the JWT secret must be at
least 32 characters. Generate values with `openssl rand -hex 32`. Never commit
the real `.env`.

For CI-published images, set `TAG` to `latest` or a published `sha-<commit>`
tag, then run:

```sh
docker compose config
docker compose pull api web
docker compose run --rm migrate
docker compose up -d --no-build --wait --no-deps api web caddy
docker compose ps
```

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
