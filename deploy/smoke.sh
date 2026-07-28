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
# Secret hygiene: the only values this script ever reads out of .env are the two
# Cloudflare Access service-token keys it needs to authenticate its own public-domain
# requests (CF_ACCESS_CLIENT_ID / CF_ACCESS_CLIENT_SECRET, see below). .env is never
# sourced as a whole, no other key is parsed, and no secret value is ever printed,
# logged, put on a command line, or included in a check message.
#
# Cloudflare Access modes:
#   The public hostnames sit behind Cloudflare Access (DEPLOY-PRODUCTION.md section k),
#   so an anonymous request to https://dorent.am/ is turned away at Cloudflare's edge
#   and never reaches the tunnel. Access lets non-interactive clients through with a
#   *service token* — two headers matched by a `Service Auth` policy on the Access
#   application. The PRESENCE of both keys is this script's mode switch:
#
#     both set   -> ENFORCED mode. Public-domain checks send the service token and must
#                   get the app; additionally, an ANONYMOUS probe of both hostnames must
#                   NOT get the app (that check is the proof the gate is actually closed).
#     both unset -> PUBLIC mode. Public-domain checks run anonymously, as before Access
#                   existed. If the edge answers like Access is on anyway, the check
#                   FAILs naming the missing service token — it never fails silently.
#     one set    -> hard FAIL: a half-configured token is a misconfiguration, not a mode.
#
#   Values are taken from the environment if already exported, otherwise from .env.
#   That makes an ad-hoc run possible without touching .env:
#     CF_ACCESS_CLIENT_ID=... CF_ACCESS_CLIENT_SECRET=... ./deploy/smoke.sh
#
# Check tiers:
#   MANDATORY — any failure makes the overall verdict FAIL and the exit code 1.
#   WARNING   — reported as WARN, but the exit code stays 0 if all mandatory pass.
#
# Most checks here ask "does this component answer at all". geo-map-pins is the exception
# and the reason is worth stating up front: it asserts a property of the DATA, because the
# geo surface can fail while every liveness check stays green (see the check for the full
# reasoning). Liveness is not health.
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
#
# API_BASE is overridable from the environment (same pattern as BACKUPS_DIR below) purely
# so the data-shape checks can be rehearsed against a mock that returns a known-bad body —
# proving a check actually goes red is not otherwise possible without breaking production
# data, which is never an acceptable way to test a check. Nothing else is redirectable: the
# public hostnames in DOMAINS are the stand's identity and stay pinned. An overridden base
# is visible in every message the affected checks print, so a run pointed somewhere else
# can never be mistaken for a clean production run.
API_BASE="${API_BASE:-http://127.0.0.1:8080}"
UI_BASE="http://127.0.0.1:4200"
DOMAINS=("https://dorent.am" "https://www.dorent.am")
# Single hostname used for the per-route public checks (SPA fallback, /api/, /uploads/,
# /hubs/). Both hostnames are proven to serve the shell by check_domains; the routing
# behind them is the same nginx, so exercising it once through the edge is enough.
PUBLIC_BASE="https://dorent.am"

CURL_TIMEOUT_LOCAL=10
CURL_TIMEOUT_PUBLIC=25

# --- Cloudflare Access service token -------------------------------------------------
# See the "Cloudflare Access modes" block in the file header for what this switches.

# read_env_key <KEY> → prints the raw value of exactly that key from .env, or nothing.
# Deliberately NOT `source`: sourcing .env would pull the SA password, the JWT key and
# the tunnel token into this process's environment and would execute any command
# substitution a value happened to contain. This only ever matches the one key asked
# for, takes the last assignment, and strips CR (a .env edited on Windows), optional
# surrounding quotes and trailing blanks.
read_env_key() {
    local key="$1"
    [[ -r "${ENV_FILE}" ]] || return 0
    sed -n -E "s/^[[:space:]]*(export[[:space:]]+)?${key}[[:space:]]*=[[:space:]]*//p" "${ENV_FILE}" 2>/dev/null \
        | tail -n 1 \
        | sed -E "s/\r$//; s/^\"(.*)\"$/\1/; s/^'(.*)'$/\1/; s/[[:space:]]+$//" \
        || true
}

CF_ACCESS_CLIENT_ID="${CF_ACCESS_CLIENT_ID:-$(read_env_key CF_ACCESS_CLIENT_ID)}"
CF_ACCESS_CLIENT_SECRET="${CF_ACCESS_CLIENT_SECRET:-$(read_env_key CF_ACCESS_CLIENT_SECRET)}"

# ACCESS_MODE: enforced | public | partial (see header). ACCESS_CURL_ARGS is what turns
# an anonymous probe into an authenticated one; it stays empty in every other mode.
ACCESS_MODE="public"
ACCESS_CURL_ARGS=()
ACCESS_PARTIAL_DETAIL=""
CF_CURL_CONFIG=""

# The two header values must never appear on a command line: /proc/<pid>/cmdline is
# world-readable, so `curl -H "CF-Access-Client-Secret: ..."` would expose the secret to
# every local reader for the life of the request. curl's own config file (-K) keeps them
# off the argv, and the file is created 600 and removed on exit.
cleanup_access_config() {
    if [[ -n "${CF_CURL_CONFIG}" && -f "${CF_CURL_CONFIG}" ]]; then
        rm -f "${CF_CURL_CONFIG}" || true
    fi
    return 0
}
trap cleanup_access_config EXIT

if [[ -n "${CF_ACCESS_CLIENT_ID}" && -n "${CF_ACCESS_CLIENT_SECRET}" ]]; then
    ACCESS_MODE="enforced"
    CF_CURL_CONFIG="$(mktemp "${TMPDIR:-/tmp}/smoke-cf-access.XXXXXX")"
    chmod 600 "${CF_CURL_CONFIG}"
    printf 'header = "CF-Access-Client-Id: %s"\nheader = "CF-Access-Client-Secret: %s"\n' \
        "${CF_ACCESS_CLIENT_ID}" "${CF_ACCESS_CLIENT_SECRET}" > "${CF_CURL_CONFIG}"
    ACCESS_CURL_ARGS=(--config "${CF_CURL_CONFIG}")
elif [[ -n "${CF_ACCESS_CLIENT_ID}${CF_ACCESS_CLIENT_SECRET}" ]]; then
    ACCESS_MODE="partial"
    if [[ -n "${CF_ACCESS_CLIENT_ID}" ]]; then
        ACCESS_PARTIAL_DETAIL="CF_ACCESS_CLIENT_ID is set, CF_ACCESS_CLIENT_SECRET is missing"
    else
        ACCESS_PARTIAL_DETAIL="CF_ACCESS_CLIENT_SECRET is set, CF_ACCESS_CLIENT_ID is missing"
    fi
fi

# Nothing below needs the raw values any more — drop them so a stray `set`/`env` in a
# future edit cannot surface them.
unset CF_ACCESS_CLIENT_ID CF_ACCESS_CLIENT_SECRET

# --- Reporting ---------------------------------------------------------------------
# One grep-friendly line per check: "PASS|WARN|FAIL  <check-id>: <reason>".

N_PASS=0
N_WARN=0
N_FAIL=0

report_pass() { printf 'PASS  %s: %s\n' "$1" "$2"; N_PASS=$((N_PASS + 1)); }
report_warn() { printf 'WARN  %s: %s\n' "$1" "$2"; N_WARN=$((N_WARN + 1)); }
report_fail() { printf 'FAIL  %s: %s\n' "$1" "$2"; N_FAIL=$((N_FAIL + 1)); }

# --- HTTP helpers ------------------------------------------------------------------

# http_probe <timeout> [curl args...] <url> → prints "<code> <redirect_url>".
# redirect_url is empty unless the response was a redirect (curl fills it from Location
# without following it — we deliberately never pass -L, because *where* an unauthenticated
# request is sent is the diagnosis). 000 means nothing answered: refused, timeout, DNS.
# Never fails the script (set -e safe).
http_probe() {
    local timeout="$1"
    shift
    local out
    out="$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' --max-time "${timeout}" "$@" 2>/dev/null || true)"
    # curl prints the write-out template even for a failed transfer, so `out` is normally
    # already "000 ". Empty only if curl itself could not run at all.
    [[ -n "${out}" ]] || out="000 "
    printf '%s' "${out}"
}

# http_code <timeout> [curl args...] <url>  → prints the status code, or 000 on
# transport failure (refused, timeout, DNS). Never fails the script (set -e safe).
http_code() {
    local probe
    probe="$(http_probe "$@")"
    printf '%s' "${probe%% *}"
}

# http_body <timeout> [curl args...] <url> → prints the response body (empty on failure).
http_body() {
    local timeout="$1"
    shift
    curl -s --max-time "${timeout}" "$@" || true
}

# looks_like_access_gate <code> <redirect_url> → 0 if this response is Cloudflare Access
# turning the request away rather than our own stack answering.
#
# Two signatures, and only two, because everything else is ambiguous:
#   - a redirect whose target is the Access login endpoint (`<team>.cloudflareaccess.com`
#     or a `/cdn-cgi/access/` path) — what an unauthenticated browser-ish request gets;
#   - 403, which is how Access refuses a request it will not even offer a login for.
# 401 is deliberately NOT treated as an Access signature: our own SignalR hub answers
# 401 to an anonymous negotiate, and that is a healthy result (see check_public_routes).
looks_like_access_gate() {
    local code="$1" redirect="${2:-}"
    case "${code}" in
        30[12378])
            [[ "${redirect}" == *cloudflareaccess.com* || "${redirect}" == */cdn-cgi/access/* ]]
            ;;
        403) return 0 ;;
        *) return 1 ;;
    esac
}

# access_hint → the one-line remedy appended to a public-domain failure that looks like
# Access intercepted the request. Different in each mode, because the fix is different.
access_hint() {
    case "${ACCESS_MODE}" in
        enforced)
            printf 'the service token was REJECTED — token revoked/expired, or the Service Auth policy is missing or ordered below the email policy on the Access application (DEPLOY-PRODUCTION.md section k)'
            ;;
        *)
            printf 'the hostname is behind Cloudflare Access but this run has no service token — set CF_ACCESS_CLIENT_ID and CF_ACCESS_CLIENT_SECRET in %s (DEPLOY-PRODUCTION.md section k)' "${ENV_FILE}"
            ;;
    esac
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

# Geo surface: approved listings must actually reach the map.
#
# The failure this exists for is SILENT, which is the whole reason it is a check and not a
# manual step. Public map coordinates (PublicLatitude/PublicLongitude) are a DERIVED cache,
# filled at API startup by ListingLocationBackfillRunner immediately after
# ApplyMigrationsAsync, both before app.Run(). If that step does not run, every listing keeps
# NULL public coordinates and drops out of /api/listings/map-pins and out of the radius
# filter — while the listings themselves stay completely healthy. Every other check in this
# file stays green in that state, /api/listings included: the catalogue works, the map is
# just empty. A green smoke run would have certified a half-broken stand, so this check is
# MANDATORY. A warning would have repeated the very gap it was added to close.
#
# Both requests go to the API DIRECTLY rather than through the ui container's nginx, on
# purpose: proxy routing already has checks of its own (proxy-api-db, public-api), so keeping
# this one off the proxy path means a FAIL here points at the DATA and nothing else.
#
# Counting. /api/listings reports totalCount for the approved catalogue; map-pins runs the
# SAME approved-listing filter chain plus a non-null public-coordinate requirement
# (ListingsQueryService.GetMapPinsAsync). Pins are therefore always a SUBSET of the
# catalogue, which makes exactly two relations worth asserting:
#
#   totalCount == 0        -> PASS, inert. An empty catalogue plots no pins, and that is a
#                             legitimate state (fresh restore, stand before seeding). An
#                             empty response is only evidence of failure when there is
#                             something that should have been in it — so the catalogue count
#                             is what tells the two apart. Failing here would fire on a
#                             healthy system, and a check that cries wolf gets ignored.
#   totalCount > 0, 0 pins -> FAIL. Precisely the backfill failure described above.
#   pins > totalCount      -> WARN, not FAIL. A subset cannot outnumber its superset, but the
#                             two numbers come from two separate requests, so an approval
#                             landing in between produces this legitimately. Worth surfacing;
#                             never worth failing a deploy on a timing artifact.
#
# What is deliberately NOT asserted is pins == totalCount, or any ratio floor. Listing
# latitude/longitude are optional (decimal?), so an approved listing with no location at all
# is a perfectly normal listing. The share of coordinate-less listings is a product fact that
# moves as the catalogue moves, and a threshold pinned to it would turn an ordinary listing
# edit into a red deploy — fragile in exactly the way this check must not be.
check_geo_map_pins() {
    local listings_url="${API_BASE}/api/listings?page=1&pageSize=1"
    local pins_url="${API_BASE}/api/listings/map-pins"
    local code body total pins truncated=""

    # Denominator first: it decides whether the pin count means anything at all.
    code="$(http_code "${CURL_TIMEOUT_LOCAL}" "${listings_url}")"
    if [[ "${code}" != "200" ]]; then
        report_fail "geo-map-pins" "GET ${listings_url} -> ${code} (expected 200; cannot establish the approved-listing count this check is measured against)"
        return
    fi
    body="$(http_body "${CURL_TIMEOUT_LOCAL}" "${listings_url}")"
    total="$(printf '%s' "${body}" | sed -n -E 's/.*"totalCount"[[:space:]]*:[[:space:]]*([0-9]+).*/\1/p')"
    if [[ ! "${total}" =~ ^[0-9]+$ ]]; then
        report_fail "geo-map-pins" "GET ${listings_url} -> 200 but no numeric \"totalCount\" in the body (unexpected response shape — the paged-result envelope changed?)"
        return
    fi

    code="$(http_code "${CURL_TIMEOUT_LOCAL}" "${pins_url}")"
    if [[ "${code}" != "200" ]]; then
        report_fail "geo-map-pins" "GET ${pins_url} -> ${code} (expected 200; the map has no data source at all)"
        return
    fi
    body="$(http_body "${CURL_TIMEOUT_LOCAL}" "${pins_url}")"
    if [[ "${body}" != *'"items"'* ]]; then
        report_fail "geo-map-pins" "GET ${pins_url} -> 200 but the body carries no \"items\" envelope (unexpected response shape)"
        return
    fi

    # Exactly one "latitude": per pin, and the envelope itself has none. A listing title
    # containing that literal text is JSON-escaped to \"latitude\": and cannot match.
    # grep exits 1 on no match and `set -o pipefail` is on, hence the `|| true`; wc still
    # prints 0, which is the answer we want.
    pins="$(printf '%s' "${body}" | grep -o '"latitude"[[:space:]]*:' | wc -l | tr -d '[:space:]' || true)"
    [[ "${body}" == *'"isTruncated"'*'true'* ]] && truncated=" (capped at the pin limit: isTruncated=true)"

    if (( total == 0 )); then
        if (( pins == 0 )); then
            report_pass "geo-map-pins" "approved catalogue is empty, so no pins are expected — geo surface not exercised (this check becomes meaningful as soon as one listing is approved)"
        else
            report_fail "geo-map-pins" "map-pins returned ${pins} pin(s) while the approved catalogue is empty — map-pins is publishing listings the public catalogue does not (the two filter chains diverged)"
        fi
        return
    fi

    if (( pins == 0 )); then
        report_fail "geo-map-pins" "${total} approved listing(s) but map-pins returned 0 pins — public coordinates are NULL: the startup ListingLocationBackfillRunner did not run or filled nothing. Listings stay healthy everywhere else while the map and the radius filter are empty (DEPLOY-PRODUCTION.md section g, 'Бэкфилл координат объявлений')"
        return
    fi

    if (( pins > total )); then
        report_warn "geo-map-pins" "${pins} pins vs ${total} approved listing(s): pins are a subset of the catalogue and cannot outnumber it — most likely the catalogue changed between the two requests; if it persists, the map-pins filter chain diverged from the catalogue one"
        return
    fi

    report_pass "geo-map-pins" "${pins} of ${total} approved listing(s) carry public coordinates and reach the map${truncated}"
}

# Service-token sanity: half a token is never a mode, it is a typo in .env that would
# otherwise present itself as "Access rejected us" further down. Fail it by name.
check_access_token_config() {
    if [[ "${ACCESS_MODE}" == "partial" ]]; then
        report_fail "access-token" "${ACCESS_PARTIAL_DETAIL} — Cloudflare Access needs BOTH halves of the service token; fix ${ENV_FILE} (DEPLOY-PRODUCTION.md section k)"
    fi
}

# Public domain over the Cloudflare Tunnel: both hostnames must serve the app.
#
# In ENFORCED mode the request carries the Access service token, so a 200 here proves the
# whole public path at once: Cloudflare edge -> Access Service Auth policy -> tunnel ->
# nginx -> Angular bundle. The app-shell marker matters as much as the status code: an
# Access login page is also HTTP 200 on some flows, and "200" alone would grade it as a
# healthy site.
check_domains() {
    local url probe code redirect body id
    for url in "${DOMAINS[@]}"; do
        id="domain-${url#https://}"
        probe="$(http_probe "${CURL_TIMEOUT_PUBLIC}" ${ACCESS_CURL_ARGS[@]+"${ACCESS_CURL_ARGS[@]}"} "${url}/")"
        read -r code redirect <<<"${probe}"
        body="$(http_body "${CURL_TIMEOUT_PUBLIC}" ${ACCESS_CURL_ARGS[@]+"${ACCESS_CURL_ARGS[@]}"} "${url}/")"
        if [[ "${code}" == "200" && "${body}" == *'<app-root'* ]]; then
            report_pass "${id}" "GET ${url}/ -> 200 with Angular app shell through the tunnel (access mode: ${ACCESS_MODE})"
        elif looks_like_access_gate "${code}" "${redirect:-}"; then
            report_fail "${id}" "GET ${url}/ -> ${code}, intercepted by Cloudflare Access: $(access_hint)"
        else
            report_fail "${id}" "GET ${url}/ -> ${code}, app shell marker <app-root> $([[ "${body}" == *'<app-root'* ]] && echo present || echo MISSING) (expected 200 + shell; check cloudflared logs / Cloudflare tunnel status)"
        fi
    done
}

# The routes that only the EDGE path can break, exercised through the public hostname.
# The loopback equivalents of these three (uploads-routing, ws-negotiate, proxy-api-db)
# stop at the ui container and therefore cannot see anything Cloudflare does in front of
# it — Access included. Same expected codes as the loopback checks, same M-008 reasoning:
#   SPA fallback  200 + <app-root>  (a direct link / page refresh on a client-side route)
#   /api/         200               (anonymous, DB-free catalogue endpoint)
#   /uploads/     404 from the API  (200 would mean the SPA fallback swallowed the path)
#   /hubs/        401 from SignalR  (405 would mean the SPA fallback answered the POST)
# In ENFORCED mode these run WITH the service token, so each one also proves that Access
# lets that path through rather than answering it with a login redirect — the concrete
# risk being that /api/ and /hubs/ are XHR/WebSocket paths a browser cannot follow a
# redirect on.
check_public_routes() {
    local probe code redirect body

    probe="$(http_probe "${CURL_TIMEOUT_PUBLIC}" ${ACCESS_CURL_ARGS[@]+"${ACCESS_CURL_ARGS[@]}"} "${PUBLIC_BASE}/smoke-check-nonexistent-spa-route")"
    read -r code redirect <<<"${probe}"
    body="$(http_body "${CURL_TIMEOUT_PUBLIC}" ${ACCESS_CURL_ARGS[@]+"${ACCESS_CURL_ARGS[@]}"} "${PUBLIC_BASE}/smoke-check-nonexistent-spa-route")"
    if [[ "${code}" == "200" && "${body}" == *'<app-root'* ]]; then
        report_pass "public-spa-fallback" "GET ${PUBLIC_BASE}/<unknown route> -> 200 with app shell (deep links and page refresh work)"
    elif looks_like_access_gate "${code}" "${redirect:-}"; then
        report_fail "public-spa-fallback" "GET ${PUBLIC_BASE}/<unknown route> -> ${code}, intercepted by Cloudflare Access: $(access_hint)"
    else
        report_fail "public-spa-fallback" "GET ${PUBLIC_BASE}/<unknown route> -> ${code} (expected 200 + <app-root>; 404 = nginx lost try_files, deep links break)"
    fi

    probe="$(http_probe "${CURL_TIMEOUT_PUBLIC}" ${ACCESS_CURL_ARGS[@]+"${ACCESS_CURL_ARGS[@]}"} "${PUBLIC_BASE}/api/categories")"
    read -r code redirect <<<"${probe}"
    if [[ "${code}" == "200" ]]; then
        report_pass "public-api" "GET ${PUBLIC_BASE}/api/categories -> 200 (edge -> tunnel -> nginx -> api)"
    elif looks_like_access_gate "${code}" "${redirect:-}"; then
        report_fail "public-api" "GET ${PUBLIC_BASE}/api/categories -> ${code}, intercepted by Cloudflare Access: $(access_hint)"
    else
        report_fail "public-api" "GET ${PUBLIC_BASE}/api/categories -> ${code} (expected 200)"
    fi

    probe="$(http_probe "${CURL_TIMEOUT_PUBLIC}" ${ACCESS_CURL_ARGS[@]+"${ACCESS_CURL_ARGS[@]}"} "${PUBLIC_BASE}/uploads/listings/smoke-check-does-not-exist.jpg")"
    read -r code redirect <<<"${probe}"
    if [[ "${code}" == "404" ]]; then
        report_pass "public-uploads" "GET ${PUBLIC_BASE}/uploads/<nonexistent> -> 404 from API (listing images reach the browser through the edge)"
    elif looks_like_access_gate "${code}" "${redirect:-}"; then
        report_fail "public-uploads" "GET ${PUBLIC_BASE}/uploads/<nonexistent> -> ${code}, intercepted by Cloudflare Access: $(access_hint)"
    else
        report_fail "public-uploads" "GET ${PUBLIC_BASE}/uploads/<nonexistent> -> ${code} (expected 404; 200 = SPA fallback swallowed /uploads/)"
    fi

    probe="$(http_probe "${CURL_TIMEOUT_PUBLIC}" ${ACCESS_CURL_ARGS[@]+"${ACCESS_CURL_ARGS[@]}"} -X POST "${PUBLIC_BASE}/hubs/chat/negotiate?negotiateVersion=1")"
    read -r code redirect <<<"${probe}"
    if [[ "${code}" == "401" ]]; then
        report_pass "public-ws-negotiate" "POST ${PUBLIC_BASE}/hubs/chat/negotiate -> 401 (auth required — SignalR reachable through the edge, chat can connect)"
    elif looks_like_access_gate "${code}" "${redirect:-}"; then
        report_fail "public-ws-negotiate" "POST ${PUBLIC_BASE}/hubs/chat/negotiate -> ${code}, intercepted by Cloudflare Access: $(access_hint)"
    else
        report_fail "public-ws-negotiate" "POST ${PUBLIC_BASE}/hubs/chat/negotiate -> ${code} (expected 401; 405 = SPA fallback ate /hubs/, 404 = route lost, 5xx = hub broken)"
    fi
}

# The gate itself, and the only check here that is about security rather than liveness:
# with NO credentials of any kind, both hostnames must refuse to hand over the app.
#
# The assertion is deliberately "the app shell was not served" rather than a specific
# status code. Access answers unauthenticated requests differently depending on what the
# client looks like (302 to the team login domain, or 403), and pinning one of those
# spellings would make this check lie the first time Cloudflare picks the other. What
# must never happen is a stranger receiving the Angular app.
#
# Runs in ENFORCED mode only: in PUBLIC mode an anonymous 200 is the correct answer, and
# a check that fires on the intended state is a check people learn to ignore (M-015).
check_access_gate() {
    [[ "${ACCESS_MODE}" == "enforced" ]] || return 0
    local url probe code redirect body id where
    for url in "${DOMAINS[@]}"; do
        id="access-gate-${url#https://}"
        probe="$(http_probe "${CURL_TIMEOUT_PUBLIC}" "${url}/")"
        read -r code redirect <<<"${probe}"
        body="$(http_body "${CURL_TIMEOUT_PUBLIC}" "${url}/")"
        if [[ "${code}" == "000" ]]; then
            report_fail "${id}" "anonymous GET ${url}/ -> no response at all (cannot conclude the gate is closed; check the tunnel)"
        elif [[ "${body}" == *'<app-root'* ]]; then
            report_fail "${id}" "anonymous GET ${url}/ -> ${code} AND served the Angular app shell — the stand is PUBLIC to anyone: the Cloudflare Access application is missing, disabled, or does not cover this hostname (DEPLOY-PRODUCTION.md section k)"
        else
            where=""
            [[ -n "${redirect:-}" ]] && where=" -> ${redirect%%\?*}"
            report_pass "${id}" "anonymous GET ${url}/ -> ${code}${where}, no app shell (Cloudflare Access is turning strangers away)"
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

    # backup.log carries TWO interleaved streams:
    #   1. canonical entries written by backup-production.sh's log():
    #        "<ts> OK db=...(...) uploads=...(...)"      daily backup succeeded
    #        "<ts> VERIFY PASS file=... users=N"         --verify restore succeeded
    #        "<ts> FAIL ..."                             any failure (incl. 'FAIL verify: ...')
    #   2. raw stdout/stderr of the cron job, because the crontab entry appends
    #      the script's own console output to the same file ('>> backup.log 2>&1'):
    #      sqlcmd chatter ("Processed 592 pages...") and the human-readable
    #      summary ("Backup OK: <file> (612K), ...").
    # So the LAST PHYSICAL LINE is whatever happened to print last — normally the
    # human-readable summary — not the outcome. Checking it with tail -n 1 made
    # this check WARN on every successful backup (a permanently-firing warning
    # teaches everyone to ignore warnings). Evaluate the last CANONICAL entry.
    local canonical_re='^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}[+-][0-9]{4} (OK|VERIFY PASS|VERIFY FAIL|FAIL) '
    if [[ -s "${BACKUP_LOG}" ]]; then
        local last_entry level
        last_entry="$(grep -E "${canonical_re}" "${BACKUP_LOG}" | tail -n 1 || true)"
        if [[ -z "${last_entry}" ]]; then
            report_warn "backup-log" "${BACKUP_LOG} has no canonical backup-production.sh entries yet (no backup has logged an outcome)"
        elif [[ "${last_entry}" =~ ${canonical_re} ]]; then
            level="${BASH_REMATCH[1]}"
            if [[ "${level}" == "OK" || "${level}" == "VERIFY PASS" ]]; then
                report_pass "backup-log" "last backup-production.sh entry is a success (${level}): $(printf '%s' "${last_entry}" | cut -c1-100)"
            else
                report_warn "backup-log" "last backup-production.sh entry is a failure (${level}): $(printf '%s' "${last_entry}" | cut -c1-120)"
            fi
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
    case "${ACCESS_MODE}" in
        enforced) printf 'access mode: ENFORCED — public checks authenticate with the Cloudflare Access service token\n' ;;
        partial)  printf 'access mode: MISCONFIGURED — only one half of the Access service token is set\n' ;;
        *)        printf 'access mode: PUBLIC — no Access service token configured, public checks run anonymously\n' ;;
    esac
    printf -- '---- checks ----\n'

    # MANDATORY
    check_stack_state
    check_api_health
    check_ui_shell
    check_reverse_proxy_db
    check_geo_map_pins
    check_access_token_config
    check_domains
    check_public_routes
    check_access_gate
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
