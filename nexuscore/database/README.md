# NexusCore database (MySQL 8)

| File | Run by | Purpose |
|---|---|---|
| `provision-users.sh` | MySQL admin, once | Creates the DB and two accounts: `nexuscore_migrator` (DDL) and `nexuscore_app` (SELECT/INSERT/UPDATE/DELETE only) |
| `migrate.sh` | migrator account | Applies `migrations/V*.sql` once each, tracked with checksums in `schema_migrations` |
| `migrations/V001` | | Baseline schema (users, games, libraries, cloud_sessions, ...) |
| `migrations/V002` | | Foreign keys, CHECK constraints, indexes |
| `migrations/V003` | | Seed: 8-game catalog + cloud plans (no user accounts) |

## Fresh server (e.g. Kali)

    sudo apt install mysql-server && sudo systemctl enable --now mysql
    export NEXUSCORE_APP_PASSWORD='<16+ chars, A-Za-z0-9_@%+=.,:->'
    export NEXUSCORE_MIGRATOR_PASSWORD='<different 16+ chars>'
    sudo -E ./provision-users.sh     # root over the local socket
    ./migrate.sh

If the API runs on another machine: `ACCOUNT_HOST=<api-ip> REQUIRE_SSL=1 sudo -E ./provision-users.sh`
and run `migrate.sh` with `DB_HOST=<mysql-host>`; also allow port 3306 only from the API host.

## Point the API at it (`nexuscore/.env`)

    DB_HOST=<host>
    DB_USER=nexuscore_app
    DB_PASSWORD=<NEXUSCORE_APP_PASSWORD>
    DB_AUTO_MIGRATE=false

The API refuses to start as `root` or with an empty password, and at startup only checks
that `schema_migrations` is at `V003` or newer. Check `GET /api/health/db`.

## First admin

Register in the app, then once as an administrator:
`UPDATE users SET role='admin' WHERE email='you@example.com';`

## Changing the schema

Add `migrations/V004__what_changed.sql` and run `migrate.sh`. Never edit an applied file.
Bump `RequiredSchemaVersion` in `MigrationService.cs` when the API needs the new version.

## Note on the old scripts

`seed.js`/`schema.sql` still work for throwaway local setups, but `seed.js` connects as root and creates
`admin@nexuscore.com / admin123`. Do not use it on a shared or public server.
