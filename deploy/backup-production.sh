#!/usr/bin/env bash
#
# Production backup for dorent.am (docker-compose.production.yml).
#
#   backup-production.sh            daily backup: DB dump + uploads archive, 7-day retention
#   backup-production.sh --verify   restore-test the newest local .bak into a throwaway
#                                    database inside the SAME db container, then drop it
#
# Designed to run from any cwd (e.g. cron): resolves the compose project directory from
# this script's own location (script at <repo>/deploy/, compose file at <repo>/).
#
# See DEPLOY-PRODUCTION.md for cron setup, retention, and off-site-copy notes.
#
# Server constraints this script is written around: 2 GB RAM + 4 GB swap, SQL Server
# capped at 1 GB (MSSQL_MEMORY_LIMIT_MB) — a restore test therefore runs AS A SECOND
# DATABASE inside the existing db container, never as a second SQL Server container.

set -euo pipefail

# cron's PATH is minimal and may not include the directory docker/docker-compose live in.
export PATH="/usr/local/bin:/usr/bin:/bin:${PATH:-}"

# --- Paths -------------------------------------------------------------------------
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd -P)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." &>/dev/null && pwd -P)"
COMPOSE_FILE="${REPO_ROOT}/docker-compose.production.yml"
ENV_FILE="${REPO_ROOT}/.env"

COMPOSE_PROJECT_NAME="rental-api"
UPLOADS_VOLUME="rental-api_uploads"
CHAT_UPLOADS_VOLUME="rental-api_chat-uploads"

BACKUPS_DIR="${BACKUPS_DIR:-/opt/dorent/backups}"
BACKUP_LOG="${BACKUPS_DIR}/backup.log"

SQLCMD="/opt/mssql-tools18/bin/sqlcmd"
DB_NAME="RentalPlatformDb"
VERIFY_DB_NAME="RentalPlatformDb_verify"
CONTAINER_BACKUP_DIR="/var/opt/mssql/backup"
CONTAINER_DATA_DIR="/var/opt/mssql/data"

mkdir -p "${BACKUPS_DIR}"

COMPOSE=(docker compose -p "${COMPOSE_PROJECT_NAME}" -f "${COMPOSE_FILE}" --project-directory "${REPO_ROOT}")

# --- Logging / error handling -------------------------------------------------------

log() {
    printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S%z')" "$1" >>"${BACKUP_LOG}"
}

on_error() {
    local exit_code=$?
    local line=$1
    local cmd="${BASH_COMMAND:-unknown}"
    # Defensive: never let a raw secret reach the log, even if the failing command
    # line happened to contain it (e.g. the docker compose exec invocation itself).
    if [[ -n "${MSSQL_SA_PASSWORD:-}" ]]; then
        cmd="${cmd//${MSSQL_SA_PASSWORD}/***REDACTED***}"
    fi
    log "FAIL line=${line} exit=${exit_code} cmd='${cmd}'"
    exit "${exit_code}"
}
trap 'on_error ${LINENO}' ERR

print_usage() {
    cat <<'USAGE'
Usage: backup-production.sh [--verify]

  (no args)   Daily backup: DB dump (BACKUP DATABASE, compressed) + tar of the
              upload volumes, copied to /opt/dorent/backups, 7-day retention.
  --verify    Restore-test the newest local .bak as RentalPlatformDb_verify
              inside the existing db container, run a sanity query, drop it.
USAGE
}

# --- Secrets --------------------------------------------------------------------
# Parsed line-by-line on purpose (never `source`d) so the .env file's contents are
# never evaluated as shell code.
load_sa_password() {
    if [[ ! -f "${ENV_FILE}" ]]; then
        log "FAIL .env not found at ${ENV_FILE}"
        echo "ERROR: .env not found at ${ENV_FILE}" >&2
        exit 1
    fi

    MSSQL_SA_PASSWORD="$(grep -E '^MSSQL_SA_PASSWORD=' "${ENV_FILE}" | tail -n1 | cut -d= -f2-)"
    # Strip optional surrounding quotes, in case the value was quoted by hand.
    MSSQL_SA_PASSWORD="${MSSQL_SA_PASSWORD%\"}"
    MSSQL_SA_PASSWORD="${MSSQL_SA_PASSWORD#\"}"
    MSSQL_SA_PASSWORD="${MSSQL_SA_PASSWORD%\'}"
    MSSQL_SA_PASSWORD="${MSSQL_SA_PASSWORD#\'}"

    if [[ -z "${MSSQL_SA_PASSWORD}" ]]; then
        log "FAIL MSSQL_SA_PASSWORD not set in ${ENV_FILE}"
        echo "ERROR: MSSQL_SA_PASSWORD not set in ${ENV_FILE}" >&2
        exit 1
    fi
    readonly MSSQL_SA_PASSWORD
}

# --- sqlcmd helpers ---------------------------------------------------------------
# Password always travels via the SQLCMDPASSWORD env var on the exec'd process, never
# as a -P argument, so it never shows up in a process listing (e.g. `ps aux` run
# inside the container) — only -U sa is passed on the command line.

# -b (on error batch abort) on every call site below: without it, sqlcmd exits 0 even
# after a Msg-level SQL error (e.g. permission denied, RESTORE failure) and the error
# text ends up wherever the output was headed — e.g. silently "parsed" as query results
# instead of failing loudly. With -b, a SQL error fails the sqlcmd process (and, via
# set -e / the ERR trap, the whole script) immediately, with the real message on stderr.

# Runs a statement for its side effect (BACKUP / RESTORE / DROP DATABASE / mkdir).
sqlcmd_exec() {
    "${COMPOSE[@]}" exec -T -e SQLCMDPASSWORD="${MSSQL_SA_PASSWORD}" db \
        "${SQLCMD}" -S localhost -U sa -C -b -Q "$1"
}

# Runs a query for a single scalar value: no headers, no row-count banner, trimmed.
sqlcmd_query() {
    "${COMPOSE[@]}" exec -T -e SQLCMDPASSWORD="${MSSQL_SA_PASSWORD}" db \
        "${SQLCMD}" -S localhost -U sa -C -b -h -1 -W -Q "$1"
}

# Runs a query with pipe-separated columns, for parsing multi-column result sets
# (used for RESTORE FILELISTONLY).
sqlcmd_query_piped() {
    "${COMPOSE[@]}" exec -T -e SQLCMDPASSWORD="${MSSQL_SA_PASSWORD}" db \
        "${SQLCMD}" -S localhost -U sa -C -b -h -1 -W -s '|' -Q "$1"
}

db_container_id() {
    local cid
    cid="$("${COMPOSE[@]}" ps -q db)"
    if [[ -z "${cid}" ]]; then
        echo "ERROR: db container is not running (docker compose ps -q db returned nothing)" >&2
        return 1
    fi
    printf '%s' "${cid}"
}

# Safety-net cleanup for --verify: drops the throwaway database and removes the temp
# .bak copy inside the container. Idempotent (IF EXISTS / -f), so it's harmless to run
# even when the happy path already cleaned up.
#
# Takes the in-container .bak path as an explicit argument ($1) rather than closing
# over a caller's `local` variable. This function is registered against the EXIT trap
# from inside run_verify(), and EXIT fires after run_verify() has already returned (once
# the whole script is exiting) — by then any `local` of run_verify's is out of scope,
# and under `set -u` merely referencing it would itself be a fatal "unbound variable"
# error that happens during word expansion, before the command even runs, so `|| true`
# can't rescue it either. See the trap registration in run_verify for how $1 is bound.
cleanup_verify() {
    local temp_bak_in_container="$1"
    sqlcmd_exec "IF DB_ID(N'${VERIFY_DB_NAME}') IS NOT NULL DROP DATABASE [${VERIFY_DB_NAME}];" || true
    "${COMPOSE[@]}" exec -T db rm -f "${temp_bak_in_container}" || true
}

# --- Daily backup -------------------------------------------------------------------

run_backup() {
    local stamp bak_name bak_in_container bak_out uploads_tgz cid bak_size tgz_size

    stamp="$(date +%Y-%m-%d_%H%M)"
    bak_name="${DB_NAME}_${stamp}.bak"
    bak_in_container="${CONTAINER_BACKUP_DIR}/${bak_name}"
    bak_out="${BACKUPS_DIR}/${bak_name}"
    uploads_tgz="uploads_${stamp}.tgz"

    "${COMPOSE[@]}" exec -T db mkdir -p "${CONTAINER_BACKUP_DIR}"

    sqlcmd_exec "BACKUP DATABASE [${DB_NAME}] TO DISK = N'${bak_in_container}' WITH COMPRESSION, INIT;"

    cid="$(db_container_id)"
    docker cp "${cid}:${bak_in_container}" "${bak_out}"
    "${COMPOSE[@]}" exec -T db rm -f "${bak_in_container}"

    # Throwaway container, never the SQL Server image — this box has no RAM to spare
    # for a second SQL Server instance just to tar two volumes.
    docker run --rm \
        -v "${UPLOADS_VOLUME}:/data/uploads:ro" \
        -v "${CHAT_UPLOADS_VOLUME}:/data/chat-uploads:ro" \
        -v "${BACKUPS_DIR}:/backup" \
        alpine sh -c "tar czf /backup/${uploads_tgz} -C /data uploads chat-uploads"

    # Retention: keep 7 days of local backups. (Off-site copy is a separate,
    # not-yet-automated step — see DEPLOY-PRODUCTION.md.)
    find "${BACKUPS_DIR}" -maxdepth 1 -type f -name '*.bak' -mtime +7 -delete
    find "${BACKUPS_DIR}" -maxdepth 1 -type f -name '*.tgz' -mtime +7 -delete

    bak_size="$(du -h "${bak_out}" | cut -f1)"
    tgz_size="$(du -h "${BACKUPS_DIR}/${uploads_tgz}" | cut -f1)"

    log "OK db=${bak_name}(${bak_size}) uploads=${uploads_tgz}(${tgz_size})"
    echo "Backup OK: ${bak_name} (${bak_size}), ${uploads_tgz} (${tgz_size})"
}

# --- Restore verification ------------------------------------------------------------

run_verify() {
    local latest_bak bak_basename verify_stamp bak_in_container cid
    local filelist data_logical log_logical verify_data_file verify_log_file count

    latest_bak="$(ls -t "${BACKUPS_DIR}"/*.bak 2>/dev/null | head -n1 || true)"
    if [[ -z "${latest_bak}" ]]; then
        log "FAIL verify: no .bak files found in ${BACKUPS_DIR}"
        echo "VERIFY FAIL: no .bak files found in ${BACKUPS_DIR}" >&2
        exit 1
    fi
    bak_basename="$(basename "${latest_bak}")"
    echo "Verifying: ${bak_basename}"

    verify_stamp="$(date +%Y%m%d_%H%M%S)"
    bak_in_container="${CONTAINER_BACKUP_DIR}/verify_${verify_stamp}.bak"

    "${COMPOSE[@]}" exec -T db mkdir -p "${CONTAINER_BACKUP_DIR}"

    cid="$(db_container_id)"
    docker cp "${latest_bak}" "${cid}:${bak_in_container}"
    # docker cp preserves host ownership (typically the deploy user, e.g. uid 1000),
    # but sqlservr inside the container runs as mssql (uid 10001) and can't read a file
    # it doesn't own — RESTORE FILELISTONLY/RESTORE DATABASE would fail with OS error 5
    # (Access is denied). The container's default exec user is mssql itself, which can't
    # chown, so this needs -u root.
    "${COMPOSE[@]}" exec -T -u root db chown mssql:mssql "${bak_in_container}"

    # Bind the current value of bak_in_container into the trap command NOW (double-quoted
    # trap string → expanded at registration time), not as a variable reference resolved
    # later — see cleanup_verify's comment for why that distinction matters here.
    trap "cleanup_verify '${bak_in_container}'" EXIT

    # Read the backup's own logical file names rather than assuming fixed ones, so
    # this keeps working even if the database was ever created/restored differently.
    filelist="$(sqlcmd_query_piped "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'${bak_in_container}';")"

    data_logical=""
    log_logical=""
    while IFS='|' read -r logical_name _physical_name file_type _rest; do
        logical_name="$(echo "${logical_name}" | xargs)"
        file_type="$(echo "${file_type}" | xargs)"
        [[ -z "${logical_name}" ]] && continue
        case "${file_type}" in
            D) data_logical="${logical_name}" ;;
            L) log_logical="${logical_name}" ;;
        esac
    done <<<"${filelist}"

    if [[ -z "${data_logical}" || -z "${log_logical}" ]]; then
        log "FAIL verify: could not parse logical file names from RESTORE FILELISTONLY for ${bak_basename}"
        echo "VERIFY FAIL: could not parse backup file list for ${bak_basename}" >&2
        exit 1
    fi

    # Unique physical file names so this never collides with the live database's
    # own .mdf/.ldf in the same data directory.
    verify_data_file="${CONTAINER_DATA_DIR}/${VERIFY_DB_NAME}_${verify_stamp}.mdf"
    verify_log_file="${CONTAINER_DATA_DIR}/${VERIFY_DB_NAME}_${verify_stamp}.ldf"

    sqlcmd_exec "RESTORE DATABASE [${VERIFY_DB_NAME}] FROM DISK = N'${bak_in_container}' WITH MOVE N'${data_logical}' TO N'${verify_data_file}', MOVE N'${log_logical}' TO N'${verify_log_file}', REPLACE;"

    # dbo.Users is AppDbContext's real table name (UserConfiguration.ToTable("Users")).
    count="$(sqlcmd_query "SET NOCOUNT ON; SELECT COUNT(*) FROM [${VERIFY_DB_NAME}].dbo.Users;")"
    count="$(echo "${count}" | tr -d '[:space:]')"

    if [[ ! "${count}" =~ ^[0-9]+$ ]]; then
        log "FAIL verify: sanity query returned non-numeric result '${count}' for ${bak_basename}"
        echo "VERIFY FAIL: unexpected sanity query result '${count}'" >&2
        exit 1
    fi

    log "VERIFY PASS file=${bak_basename} users=${count}"
    echo "VERIFY PASS: restored ${bak_basename} as ${VERIFY_DB_NAME}, dbo.Users COUNT(*) = ${count}"
    # cleanup_verify (registered above) runs on exit and drops RentalPlatformDb_verify
    # plus the temp .bak — nothing further to do here.
}

# --- Entry point --------------------------------------------------------------------

main() {
    local mode="backup"

    if [[ $# -gt 1 ]]; then
        print_usage >&2
        exit 2
    fi

    if [[ $# -eq 1 ]]; then
        case "$1" in
            --verify) mode="verify" ;;
            -h | --help)
                print_usage
                exit 0
                ;;
            *)
                echo "Unknown argument: $1" >&2
                print_usage >&2
                exit 2
                ;;
        esac
    fi

    load_sa_password

    if [[ "${mode}" == "verify" ]]; then
        run_verify
    else
        run_backup
    fi
}

main "$@"
