#!/usr/bin/env bash
#
# Production infrastructure smoke check for dorent.am (docker-compose.production.yml).
#
#   smoke.sh    run all checks, print one PASS/WARN/FAIL line per check + summary
#
# READ-ONLY by design: this script never mutates production state (no writes, no
# restarts, no docker exec into service workloads). Safe to run at any time, as
# often as needed, from any cwd — it resolves the compose project directory from
# its own location (script at <repo>/deploy/, compose file at <repo>/), same as
# backup-production.sh.
#
# Secret hygiene: the script checks that .env EXISTS and has mode 600, but never
# reads, prints, or exports any value from it.
#
# Check tiers:
#   MANDATORY — any failure makes the overall verdict FAIL and the exit code 1.
#   WARNING   — reported as WARN, but the exit code stays 0 if all mandatory pass.
#
# Exit codes: 0 = all mandatory checks passed (warnings possible)
#             1 = at least one mandatory check failed
#
# See DEPLOY-PRODUCTION.md (section j) for when to run this and how to read it.

set -euo pipefail

# cron's/CI's PATH is minimal and may not include the directory docker lives in.
export PATH="/usr/local/bin:/usr/bin:/bin:${PATH:-}"

# --- Paths -------------------------------------------------------------------------
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd -P)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." &>/dev/null && pwd -P)"
COMPOSE_FILE="${REPO_ROOT}/docker-compose.production.yml"
ENV_FILE="${REPO_ROOT}/.env"

COMPOSE_PROJECT_NAME="rental-api"
COMPOSE=(docker compose -p "${COMPOSE_PROJECT_NAME}" -f "${COMPOSE_FILE}" --project-directory "${REPO_ROOT}")

BACKUPS_DIR="${BACKUPS_DIR:-/opt/dorent/backups}"
BACKUP_LOG="${BACKUPS_DIR}/backup.log"

# Loopback endpoints (M-014: always 127.0.0.1, never localhost — IPv6 ::1 trap).
API_BASE="http://127.0.0.1:8080"
UI_BASE="http://127.0.0.1:4200"
DOMAINS=("https://dorent.am" "https://www.dorent.am")

CURL_TIMEOUT_LOCAL=10
CURL_TIMEOUT_PUBLIC=25

# --- Reporting ---------------------------------------------------------------------
# One grep-friendly line per check: "PASS|WARN|FAIL  <check-id>: <reason>".

N_PASS=0
N_WARN=0
N_FAIL=0

report_pass() { printf 'PASS  %s: %s\n' "$1" "$2"; N_PASS=$((N_PASS + 1)); }
report_warn() { printf 'WARN  %s: %s\n' "$1" "$2"; N_WARN=$((N_WARN + 1)); }
report_fail() { printf 'FAIL  %s: %s\n' "$1" "$2"; N_FAIL=$((N_FAIL + 1)); }

# --- HTTP helpers ------------------------------------------------------------------

# http_code <timeout> [curl args...] <url>  → prints the status code, or 000 on
# transport failure (refused, timeout, DNS). Never fails the script (set -e safe).
http_code() {
    local timeout="$1"
    shift
    curl -s -o /dev/null -w '%{http_code}' --max-time "${timeout}" "$@" || printf '000'
}

# http_body <timeout> <url> → prints the response body (empty on failure).
http_body() {
    local timeout="$1"
    shift
    curl -s --max-time "${timeout}" "$@" || true
}

# --- MANDATORY checks ----------------------------------------------------------------

# Stack state: db/api/ui/cloudflared must all exist and be running, and every
# container that defines a healthcheck must be healthy (no restarting/exited/
# unhealthy). cloudflared has no healthcheck — running is enough for it.
check_stack_state() {
    local svc cid status health bad="" detail=""
    for svc in db api ui cloudflared; do
        cid="$("${COMPOSE[@]}" ps -q "${svc}" 2>/dev/null || true)"
        if [[ -z "${cid}" ]]; then
            bad+="${svc}=missing "
            continue
        fi
        status="$(docker inspect -f '{{.State.Status}}' "${cid}" 2>/dev/null || echo 'inspect-error')"
        health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "${cid}" 2>/dev/null || echo 'inspect-error')"
        detail+="${svc}=${status}/${health} "
        if [[ "${status}" != "running" ]]; then
            bad+="${svc}=${status} "
        elif [[ "${health}" != "none" && "${health}" != "healthy" ]]; then
            bad+="${svc}=${health} "
        fi
    done
    if [[ -n "${bad}" ]]; then
        report_fail "stack-state" "unhealthy services: ${bad}(${detail% })"
    else
        report_pass "stack-state" "${detail% }"
    fi
}

# API liveness: the same anonymous, DB-free endpoint the docker healthcheck uses.
check_api_health() {
    local code body
    code="$(http_code "${CURL_TIMEOUT_LOCAL}" "${API_BASE}/health")"
    body="$(http_body "${CURL_TIMEOUT_LOCAL}" "${API_BASE}/health")"
    if [[ "${code}" == "200" && "${body}" == *'"status"'*'"ok"'* ]]; then
        report_pass "api-health" "GET ${API_BASE}/health -> 200 with expected body"
    else
        report_fail "api-health" "GET ${API_BASE}/health -> ${code}, body '${body:0:80}' (expected 200 + {\"status\":\"ok\"})"
    fi
}

# UI shell: nginx serves the built Angular app (not a default page / error page).
check_ui_shell() {
    local code body
    code="$(http_code "${CURL_TIMEOUT_LOCAL}" "${UI_BASE}/")"
    body="$(http_body "${CURL_TIMEOUT_LOCAL}" "${UI_BASE}/")"
    if [[ "${code}" == "200" && "${body}" == *'<app-root'* ]]; then
        report_pass "ui-shell" "GET ${UI_BASE}/ -> 200 with Angular app shell (<app-root>)"
    else
        report_fail "ui-shell" "GET ${UI_BASE}/ -> ${code}, app shell marker <app-root> $([[ "${body}" == *'<app-root'* ]] && echo present || echo MISSING)"
    fi
}

# Reverse proxy + database, one check: /api/listings through the ui container's
# nginx must return 200. A 200 here proves BOTH nginx -> api proxying AND
# api -> SQL Server connectivity, because the listings endpoint is DB-backed
# (it queries approved listings) — unlike /health, which deliberately touches
# nothing. If the DB were down, this would be 5xx while /health stayed 200.
check_reverse_proxy_db() {
    local code
    code="$(http_code "${CURL_TIMEOUT_LOCAL}" "${UI_BASE}/api/listings")"
    if [[ "${code}" == "200" ]]; then
        report_pass "proxy-api-db" "GET ${UI_BASE}/api/listings -> 200 (nginx->api proxy AND api->SQL Server both alive)"
    else
        report_fail "proxy-api-db" "GET ${UI_BASE}/api/listings -> ${code} (expected 200; 5xx here with api-health OK usually means DB trouble)"
    fi
}

# Public domain over the Cloudflare Tunnel: both hostnames must serve the app.
check_domains() {
    local url code
    for url in "${DOMAINS[@]}"; do
        code="$(http_code "${CURL_TIMEOUT_PUBLIC}" "${url}/")"
        if [[ "${code}" == "200" ]]; then
            report_pass "domain-${url#https://}" "GET ${url}/ -> 200 through the tunnel"
        else
            report_fail "domain-${url#https://}" "GET ${url}/ -> ${code} (expected 200; check cloudflared logs / Cloudflare tunnel status)"
        fi
    done
}

# Uploads routing: a request for a nonexistent file under /uploads/ must come
# back as a clean 404 FROM THE API. 404 is the healthy answer here:
#   - 200 would mean nginx lost the /uploads/ location and the SPA fallback
#     swallowed the path (regression class M-008) — files would 'load' as HTML;
#   - 405/5xx would mean the request reached something broken;
#   - 404 means nginx proxied to the API, the API's static-file pipeline ran,
#     looked for the file, and correctly said it does not exist.
check_uploads_routing() {
    local code
    code="$(http_code "${CURL_TIMEOUT_LOCAL}" "${UI_BASE}/uploads/listings/smoke-check-does-not-exist.jpg")"
    if [[ "${code}" == "404" ]]; then
        report_pass "uploads-routing" "GET ${UI_BASE}/uploads/<nonexistent> -> 404 from API (proxy route intact, no SPA fallback)"
    else
        report_fail "uploads-routing" "GET ${UI_BASE}/uploads/<nonexistent> -> ${code} (expected 404; 200 = SPA fallback swallowed /uploads/, 5xx = broken proxy/api)"
    fi
}

# WebSocket endpoint: SignalR negotiate for the chat hub requires auth, so an
# anonymous POST must get 401. 404/405 would mean nginx no longer routes /hubs/
# to the API (405 = SPA fallback answered a POST — regression class M-008);
# 5xx would mean the API's SignalR pipeline is broken.
check_websocket_negotiate() {
    local code
    code="$(http_code "${CURL_TIMEOUT_LOCAL}" -X POST "${UI_BASE}/hubs/chat/negotiate?negotiateVersion=1")"
    if [[ "${code}" == "401" ]]; then
        report_pass "ws-negotiate" "POST ${UI_BASE}/hubs/chat/negotiate -> 401 (auth required — route reaches SignalR)"
    else
        report_fail "ws-negotiate" "POST ${UI_BASE}/hubs/chat/negotiate -> ${code} (expected 401; 405 = SPA fallback ate /hubs/, 404 = route lost, 5xx = hub broken)"
    fi
}

# --- WARNING checks ------------------------------------------------------------------

# Recent critical errors in the api container's logs. Pattern targets ASP.NET
# Core's own severity prefixes ('fail:'/'crit:' at start of the log message) and
# unhandled-exception banners — NOT the words 'error'/'exception' anywhere, which
# healthy logs mention routinely (e.g. retry policies, validation messages).
check_api_logs() {
    local lines count sample
    lines="$("${COMPOSE[@]}" logs --tail 200 --no-color api 2>&1 || true)"
    count="$(printf '%s\n' "${lines}" | grep -cE '(^|\| *)(fail|crit): |Unhandled exception' || true)"
    if [[ "${count}" == "0" ]]; then
        report_pass "api-logs" "no fail:/crit:/unhandled-exception lines in last 200 log lines"
    else
        sample="$(printf '%s\n' "${lines}" | grep -E '(^|\| *)(fail|crit): |Unhandled exception' | tail -n 1 | cut -c1-140)"
        report_warn "api-logs" "${count} critical-looking line(s) in last 200 log lines; newest: ${sample}"
    fi
}

# Disk usage on the root filesystem. Two thresholds: >=90% is a MANDATORY fail
# (backups + docker builds + SQL Server on a full disk is an outage in the
# making), >=80% is a warning.
check_disk() {
    local pct
    pct="$(df -P / | awk 'NR==2 {gsub("%","",$5); print $5}')"
    if [[ ! "${pct}" =~ ^[0-9]+$ ]]; then
        report_warn "disk-usage" "could not parse df output"
    elif (( pct >= 90 )); then
        report_fail "disk-usage" "/ at ${pct}% (>=90% is treated as mandatory failure)"
    elif (( pct >= 80 )); then
        report_warn "disk-usage" "/ at ${pct}% (>=80%)"
    else
        report_pass "disk-usage" "/ at ${pct}%"
    fi
}

# Swap: this 2 GB box NEEDS its 4 GB swap file (ADR-003 amendment); no swap or
# nearly-exhausted swap means the next docker build or SQL Server spike OOMs.
check_swap() {
    local total_kb free_kb used_pct
    total_kb="$(awk '/^SwapTotal:/ {print $2}' /proc/meminfo)"
    free_kb="$(awk '/^SwapFree:/ {print $2}' /proc/meminfo)"
    if [[ -z "${total_kb}" || "${total_kb}" == "0" ]]; then
        report_warn "swap" "no swap configured (this 2 GB host requires its 4 GB swap file)"
        return
    fi
    used_pct=$(( (total_kb - free_kb) * 100 / total_kb ))
    if (( used_pct >= 90 )); then
        report_warn "swap" "swap ${used_pct}% used of $((total_kb / 1024)) MiB (nearly exhausted)"
    else
        report_pass "swap" "swap present: $((total_kb / 1024)) MiB total, ${used_pct}% used"
    fi
}

# Backup sanity — configuration and freshness only; never reads secret values.
check_backups() {
    # Cron entry installed for the deploy user.
    if crontab -l 2>/dev/null | grep -q 'backup-production\.sh'; then
        report_pass "backup-cron" "crontab entry for backup-production.sh present"
    else
        report_warn "backup-cron" "no crontab entry for backup-production.sh found for user $(id -un)"
    fi

    # Newest .bak younger than ~26h (03:30 daily cron + slack). NOTE: until the
    # FIRST 03:30 cron run after (re)provisioning, this may legitimately warn.
    if [[ -d "${BACKUPS_DIR}" ]]; then
        if [[ -n "$(find "${BACKUPS_DIR}" -maxdepth 1 -type f -name '*.bak' -mmin -1560 2>/dev/null | head -n 1)" ]]; then
            report_pass "backup-freshness" "a .bak newer than 26h exists in ${BACKUPS_DIR}"
        else
            report_warn "backup-freshness" "no .bak newer than 26h in ${BACKUPS_DIR} (legitimate only before the first 03:30 cron run)"
        fi
    else
        report_warn "backup-freshness" "${BACKUPS_DIR} does not exist"
    fi

    # Last backup.log line must be a success ('OK ...' or 'VERIFY PASS ...').
    if [[ -s "${BACKUP_LOG}" ]]; then
        if tail -n 1 "${BACKUP_LOG}" | grep -qE ' (OK|VERIFY PASS)( |$)'; then
            report_pass "backup-log" "last ${BACKUP_LOG} line is a success entry"
        else
            report_warn "backup-log" "last ${BACKUP_LOG} line is not OK/VERIFY PASS: $(tail -n 1 "${BACKUP_LOG}" | cut -c1-120)"
        fi
    else
        report_warn "backup-log" "${BACKUP_LOG} missing or empty (no backup has logged yet)"
    fi

    # .env present with mode 600 — existence and permissions only, values are
    # deliberately never read or printed.
    if [[ -f "${ENV_FILE}" ]]; then
        local mode
        mode="$(stat -c '%a' "${ENV_FILE}" 2>/dev/null || echo '?')"
        if [[ "${mode}" == "600" ]]; then
            report_pass "env-file" "${ENV_FILE} exists with mode 600"
        else
            report_warn "env-file" "${ENV_FILE} has mode ${mode}, expected 600 (fix: chmod 600)"
        fi
    else
        report_warn "env-file" "${ENV_FILE} not found"
    fi
}

# --- Entry point ---------------------------------------------------------------------

main() {
    printf 'dorent.am production smoke check — %s\n' "$(date '+%Y-%m-%d %H:%M:%S%z')"
    printf 'repo: %s\n' "${REPO_ROOT}"
    printf -- '---- checks ----\n'

    # MANDATORY
    check_stack_state
    check_api_health
    check_ui_shell
    check_reverse_proxy_db
    check_domains
    check_uploads_routing
    check_websocket_negotiate

    # WARNING tier (disk can escalate itself to FAIL at >=90%)
    check_api_logs
    check_disk
    check_swap
    check_backups

    printf -- '---- summary ----\n'
    printf 'checks: pass=%d warn=%d fail=%d\n' "${N_PASS}" "${N_WARN}" "${N_FAIL}"
    if (( N_FAIL > 0 )); then
        printf 'overall: FAIL (exit 1 — at least one mandatory check failed)\n'
        exit 1
    fi
    printf 'overall: PASS (exit 0 — all mandatory checks passed'
    if (( N_WARN > 0 )); then
        printf ', %d warning(s) above' "${N_WARN}"
    fi
    printf ')\n'
    exit 0
}

main "$@"
