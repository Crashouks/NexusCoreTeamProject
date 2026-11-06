#!/usr/bin/env bash
# Applies database/migrations/V*.sql in order, exactly once each, as the migrator account.
#   NEXUSCORE_MIGRATOR_PASSWORD=... ./migrate.sh
# Applied versions and SHA-256 checksums are stored in schema_migrations; editing an
# already-applied file aborts the run (add a new V00N file instead).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DB_NAME="${DB_NAME:-nexuscore}"
MIGRATOR_USER="${MIGRATOR_USER:-nexuscore_migrator}"
DB_HOST="${DB_HOST:-127.0.0.1}"
DB_PORT="${DB_PORT:-3306}"
: "${NEXUSCORE_MIGRATOR_PASSWORD:?NEXUSCORE_MIGRATOR_PASSWORD is required}"

# Keep the password out of the process list.
CNF="$(mktemp)"; trap 'rm -f "$CNF"' EXIT; chmod 600 "$CNF"
printf '[client]\nuser=%s\npassword=%s\nhost=%s\nport=%s\n' \
  "$MIGRATOR_USER" "$NEXUSCORE_MIGRATOR_PASSWORD" "$DB_HOST" "$DB_PORT" > "$CNF"
sql() { mysql --defaults-extra-file="$CNF" --default-character-set=utf8mb4 "$DB_NAME" "$@"; }

sql -e "CREATE TABLE IF NOT EXISTS schema_migrations (
  version     VARCHAR(20)  PRIMARY KEY,
  description VARCHAR(200) NOT NULL,
  checksum    CHAR(64)     NOT NULL,
  applied_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4"

shopt -s nullglob
applied=0
for f in "$HERE"/migrations/V*.sql; do
  base="$(basename "$f" .sql)"; version="${base%%__*}"; desc="${base#*__}"; desc="${desc//_/ }"
  sum="$(sha256sum "$f" | cut -d' ' -f1)"
  old="$(sql -N -B -e "SELECT checksum FROM schema_migrations WHERE version='${version}'")"
  if [[ -n "$old" ]]; then
    [[ "$old" == "$sum" ]] || { echo "ERROR: $base changed after it was applied" >&2; exit 1; }
    echo "skip   $base"; continue
  fi
  echo "apply  $base"
  sql < "$f"
  sql -e "INSERT INTO schema_migrations (version, description, checksum) VALUES ('${version}', '${desc}', '${sum}')"
  applied=$((applied+1))
done
echo "Done. ${applied} migration(s) applied."
