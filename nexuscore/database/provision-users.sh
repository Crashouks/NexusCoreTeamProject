#!/usr/bin/env bash
# One-time setup, run by a MySQL administrator (root via auth_socket on Kali is fine):
#   sudo NEXUSCORE_APP_PASSWORD=... NEXUSCORE_MIGRATOR_PASSWORD=... ./provision-users.sh
#
# Creates the database and two least-privilege accounts:
#   nexuscore_migrator  schema changes (CREATE/ALTER/DROP/INDEX) on nexuscore.* only
#   nexuscore_app       SELECT/INSERT/UPDATE/DELETE on nexuscore.* only (what the API uses)
# Neither has global privileges, GRANT OPTION, FILE, SUPER or access to other schemas.
set -euo pipefail

DB_NAME="${DB_NAME:-nexuscore}"
APP_USER="${APP_USER:-nexuscore_app}"
MIGRATOR_USER="${MIGRATOR_USER:-nexuscore_migrator}"
# Host the API connects from. 'localhost' when API and MySQL share a machine.
# For a remote API server use its IP or subnet, e.g. '10.0.0.%'. Never '%'.
ACCOUNT_HOST="${ACCOUNT_HOST:-localhost}"
MYSQL_ADMIN_USER="${MYSQL_ADMIN_USER:-root}"
MYSQL_ARGS=(--user="$MYSQL_ADMIN_USER")
[[ -n "${MYSQL_HOST:-}" ]] && MYSQL_ARGS+=(--host="$MYSQL_HOST" --port="${MYSQL_PORT:-3306}")

die() { echo "ERROR: $*" >&2; exit 1; }

check_name() { [[ "$2" =~ ^[A-Za-z0-9_]{1,32}$ ]] || die "$1 must match [A-Za-z0-9_]{1,32}"; }
check_host() { [[ "$ACCOUNT_HOST" =~ ^[A-Za-z0-9_.:%-]+$ ]] || die "ACCOUNT_HOST has invalid characters"; [[ "$ACCOUNT_HOST" != "%" ]] || die "ACCOUNT_HOST='%' (any host) is not allowed"; }
# Passwords are interpolated into CREATE USER, so restrict the alphabet instead of escaping.
check_pw() {
  [[ -n "${!1:-}" ]] || die "$1 is required"
  [[ "${!1}" =~ ^[A-Za-z0-9_@%+=.,:-]{16,128}$ ]] || die "$1 must be 16-128 chars from A-Za-z0-9_@%+=.,:-"
}
check_name DB_NAME "$DB_NAME"; check_name APP_USER "$APP_USER"; check_name MIGRATOR_USER "$MIGRATOR_USER"
check_host; check_pw NEXUSCORE_APP_PASSWORD; check_pw NEXUSCORE_MIGRATOR_PASSWORD
[[ "$APP_USER" != "root" && "$MIGRATOR_USER" != "root" ]] || die "refusing to use root as an application account"
[[ "$APP_USER" != "$MIGRATOR_USER" ]] || die "app and migrator must be different accounts"

# Optional: force TLS for remote accounts (REQUIRE_SSL=1).
SSL_CLAUSE=""; [[ "${REQUIRE_SSL:-0}" == "1" ]] && SSL_CLAUSE="REQUIRE SSL"

mysql "${MYSQL_ARGS[@]}" <<SQL
CREATE DATABASE IF NOT EXISTS \`${DB_NAME}\` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE USER IF NOT EXISTS '${APP_USER}'@'${ACCOUNT_HOST}' IDENTIFIED BY '${NEXUSCORE_APP_PASSWORD}';
ALTER USER '${APP_USER}'@'${ACCOUNT_HOST}' IDENTIFIED BY '${NEXUSCORE_APP_PASSWORD}' ${SSL_CLAUSE} WITH MAX_USER_CONNECTIONS 50;

CREATE USER IF NOT EXISTS '${MIGRATOR_USER}'@'${ACCOUNT_HOST}' IDENTIFIED BY '${NEXUSCORE_MIGRATOR_PASSWORD}';
ALTER USER '${MIGRATOR_USER}'@'${ACCOUNT_HOST}' IDENTIFIED BY '${NEXUSCORE_MIGRATOR_PASSWORD}' ${SSL_CLAUSE} WITH MAX_USER_CONNECTIONS 5;

-- Start from a clean slate so re-running tightens, never accumulates, privileges.
REVOKE ALL PRIVILEGES, GRANT OPTION FROM '${APP_USER}'@'${ACCOUNT_HOST}';
REVOKE ALL PRIVILEGES, GRANT OPTION FROM '${MIGRATOR_USER}'@'${ACCOUNT_HOST}';

GRANT SELECT, INSERT, UPDATE, DELETE ON \`${DB_NAME}\`.* TO '${APP_USER}'@'${ACCOUNT_HOST}';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES
  ON \`${DB_NAME}\`.* TO '${MIGRATOR_USER}'@'${ACCOUNT_HOST}';
FLUSH PRIVILEGES;
SQL

echo "Provisioned '${APP_USER}' and '${MIGRATOR_USER}' @ '${ACCOUNT_HOST}' on database '${DB_NAME}'."
