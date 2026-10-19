#!/usr/bin/env bash
#
# Production infrastructure smoke check for dorent.am (docker-compose.production.yml).
#
#   smoke.sh                    run all checks, print one PASS/WARN/FAIL line per check + summary
#   smoke.sh --record-baseline  run NO checks; measure the approved catalogue's totalCount and
#                               its number of distinct map-pin coordinate pairs and write them
#                               to the geo baseline file (GEO_BASELINE_FILE below). Run it in the
#                               deploy procedure immediately BEFORE `up -d`; the post-deploy run
#                               compares against it (check geo-pin-diversity, ADR-010 amendment
#                               2026-10-18).
#
# READ-ONLY by design: a normal run never mutates persistent production state (no DB or
# file writes, no restarts, no docker exec into service workloads). Two stated exceptions:
#   - check google-nonce asks the api to issue ONE Google sign-in nonce — an in-memory entry
#     that expires on its own after 5 minutes (see the check). Ephemeral by construction.
#   - `--record-baseline`, and only that flag, writes ONE small file outside the database
#     (the geo baseline: two numbers plus when/where they were measured). A run without the
#     flag only ever READS that file. Deliberate, accepted in the ADR-010 amendment.
# Safe to run at any time, as often as needed (well under the 30/min nonce rate limit), from
# any cwd — it resolves the compose project directory from its own location (script at
# <repo>/deploy/, compose file at <repo>/), same as backup-production.sh.
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
# Most checks here ask "does this component answer at all". geo-map-pins and
# geo-pin-diversity are the exception and the reason is worth stating up front: they assert a
# property of the DATA, because the
# geo surface can fail while every liveness check stays green (see the check for the full
# reasoning). Liveness is not health.
#
# Exit codes: 0 = all mandatory checks passed (warnings possible) / baseline recorded
#             1 = at least one mandatory check failed / baseline could not be recorded
#             2 = unknown argument
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

# Geo baseline for check geo-pin-diversity (written only by --record-baseline). Overridable
# for the same reason API_BASE is — so a rehearsal against a mock never reads or overwrites
# the real file; the path is printed in every message that uses it. Not a secret: two counts,
# a timestamp and the API base they were measured against.
GEO_BASELINE_FILE="${SMOKE_GEO_BASELINE:-/opt/dorent/deployment-notes/smoke-geo-baseline}"
# The baseline describes the catalogue "just before this deploy". Older than this, it no
# longer describes anything the current run can be held to (owners move, listings leave),
# so the comparison is reported as NOT assessed instead of producing a false FAIL. This is a
# property of the deploy procedure (record -> up -d -> smoke within minutes), not of the
# catalogue — no product fact is encoded in it.
GEO_BASELINE_MAX_AGE_HOURS=24

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
GEO_BASELINE_TMP=""   # --record-baseline's half-written temp file, removed on any exit

# The two header values must never appear on a command line: /proc/<pid>/cmdline is
# world-readable, so `curl -H "CF-Access-Client-Secret: ..."` would expose the secret to
# every local reader for the life of the request. curl's own config file (-K) keeps them
# off the argv, and the file is created 600 and removed on exit.
cleanup_access_config() {
    if [[ -n "${CF_CURL_CONFIG}" && -f "${CF_CURL_CONFIG}" ]]; then
        rm -f "${CF_CURL_CONFIG}" || true
    fi
    if [[ -n "${GEO_BASELINE_TMP}" && -f "${GEO_BASELINE_TMP}" ]]; then
        rm -f "${GEO_BASELINE_TMP}" || true
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
# --- Geo measurement, shared by geo-map-pins, geo-pin-diversity and --record-baseline ---
# One measurement per run: the two data checks look at the SAME pair of responses, and
# --record-baseline records exactly what a later run will compare against.
GEO_MEASURED=0      # 1 once geo_fetch succeeded
GEO_TOTAL=""        # approved-catalogue totalCount
GEO_PINS=""         # number of pins map-pins returned
GEO_DISTINCT=""     # number of DISTINCT coordinate pairs among them
GEO_TRUNCATED=0     # 1 if map-pins reported isTruncated=true
GEO_ERR=""          # why geo_fetch failed (a complete check message)

# geo_distinct_pairs <map-pins body> → prints the number of distinct (latitude, longitude)
# pairs, or prints nothing and returns 1 if latitudes and longitudes do not pair up.
#
# Distinct is EXACT string equality of the serialized pair, which is the right key and not a
# proximity guess: public coordinates are geohash-7 centroids stored as decimal(9,6), so two
# listings in one cell publish byte-identical values — the same key the catalogue map
# clusters on (ADR-012 §3). Each pin carries exactly one "latitude": and one "longitude":, in
# the same order; a title containing that text is JSON-escaped (\"latitude\":) and cannot
# match. grep exits 1 on no match under pipefail, hence the `|| true`s.
geo_distinct_pairs() {
    local body="$1" lats lngs n_lat n_lng
    lats="$(printf '%s' "${body}" | grep -oE '"latitude"[[:space:]]*:[[:space:]]*-?[0-9][0-9.eE+-]*' | sed -E 's/^.*:[[:space:]]*//' || true)"
    lngs="$(printf '%s' "${body}" | grep -oE '"longitude"[[:space:]]*:[[:space:]]*-?[0-9][0-9.eE+-]*' | sed -E 's/^.*:[[:space:]]*//' || true)"
    n_lat="$(printf '%s' "${lats}" | grep -c . || true)"
    n_lng="$(printf '%s' "${lngs}" | grep -c . || true)"
    [[ "${n_lat}" == "${n_lng}" ]] || return 1
    if [[ "${n_lat}" == "0" ]]; then
        printf '0'
        return 0
    fi
    paste -d ',' <(printf '%s\n' "${lats}") <(printf '%s\n' "${lngs}") | sort -u | grep -c . || true
}

# geo_fetch → fills the GEO_* globals; returns 1 with GEO_ERR set on any failure.
# Both requests go to the API DIRECTLY (see check_geo_map_pins for why).
geo_fetch() {
    local listings_url="${API_BASE}/api/listings?page=1&pageSize=1"
    local pins_url="${API_BASE}/api/listings/map-pins"
    local code body

    # Denominator first: it decides whether the pin count means anything at all.
    code="$(http_code "${CURL_TIMEOUT_LOCAL}" "${listings_url}")"
    if [[ "${code}" != "200" ]]; then
        GEO_ERR="GET ${listings_url} -> ${code} (expected 200; cannot establish the approved-listing count this check is measured against)"
        return 1
    fi
    body="$(http_body "${CURL_TIMEOUT_LOCAL}" "${listings_url}")"
    GEO_TOTAL="$(printf '%s' "${body}" | sed -n -E 's/.*"totalCount"[[:space:]]*:[[:space:]]*([0-9]+).*/\1/p')"
    if [[ ! "${GEO_TOTAL}" =~ ^[0-9]+$ ]]; then
        GEO_ERR="GET ${listings_url} -> 200 but no numeric \"totalCount\" in the body (unexpected response shape — the paged-result envelope changed?)"
        return 1
    fi

    code="$(http_code "${CURL_TIMEOUT_LOCAL}" "${pins_url}")"
    if [[ "${code}" != "200" ]]; then
        GEO_ERR="GET ${pins_url} -> ${code} (expected 200; the map has no data source at all)"
        return 1
    fi
    body="$(http_body "${CURL_TIMEOUT_LOCAL}" "${pins_url}")"
    if [[ "${body}" != *'"items"'* ]]; then
        GEO_ERR="GET ${pins_url} -> 200 but the body carries no \"items\" envelope (unexpected response shape)"
        return 1
    fi

    # Exactly one "latitude": per pin, and the envelope itself has none (see geo_distinct_pairs).
    GEO_PINS="$(printf '%s' "${body}" | grep -o '"latitude"[[:space:]]*:' | wc -l | tr -d '[:space:]' || true)"
    if ! GEO_DISTINCT="$(geo_distinct_pairs "${body}")" || [[ ! "${GEO_DISTINCT}" =~ ^[0-9]+$ ]]; then
        GEO_ERR="GET ${pins_url} -> 200 but latitude/longitude values do not pair up one-to-one (unexpected response shape — ListingMapPinResponse changed?)"
        return 1
    fi
    GEO_TRUNCATED=0
    [[ "${body}" == *'"isTruncated"'*'true'* ]] && GEO_TRUNCATED=1
    GEO_MEASURED=1
    return 0
}

check_geo_map_pins() {
    local total pins truncated=""

    if ! geo_fetch; then
        report_fail "geo-map-pins" "${GEO_ERR}"
        return
    fi
    total="${GEO_TOTAL}"
    pins="${GEO_PINS}"
    (( GEO_TRUNCATED == 1 )) && truncated=" (capped at the pin limit: isTruncated=true)"

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

# Geo surface, second half: the pins must not have COLLAPSED onto fewer points.
#
# The failure this exists for (ADR-024 home-point release): the AddUserHomePoint migration
# moves every listing of an owner onto that owner's ONE home point, and only
# DemoContentBootstrapRunner (BOOTSTRAP_DEMO_CONTENT_ENABLED=true in .env) spreads the
# showcase back across per-district owners. If the runner does not run, 56 listings sit on 1
# pin, and geo-map-pins honestly reports "56 of 56 carry public coordinates" — the COUNT is
# intact, only the DIVERSITY is gone.
#
# Why this compares against a baseline instead of asserting a number. Under ADR-024 all
# listings of one owner LEGITIMATELY share one pin, so the public edge shows distinct pins ==
# distinct owners (measured live 2026-10-18: 57 pins, 14 distinct pairs, 14 distinct owners).
# A catalogue collapsed by a failed runner and a catalogue that genuinely belongs to one
# owner are therefore byte-identical from outside: same owners, same pins, same districts.
# Nothing in the public responses separates them, and any floor ("at least N pins") is a
# threshold pinned to a product fact — rejected by ADR-010. What DOES separate them is the
# catalogue's own state a few minutes earlier: `--record-baseline` right before `up -d`
# captures it, and the baseline plays the role totalCount plays in geo-map-pins — the
# "there should have been something" (ADR-010 amendment 2026-10-18).
#
# The rule:
#   no measurement           -> WARN  (geo-map-pins already FAILed and says why)
#   totalCount == 0          -> PASS, inert: an empty catalogue has no pins to diversify
#   no / unreadable / stale baseline, baseline from another API_BASE, or either side capped
#   by isTruncated           -> WARN, "NOT assessed", with the reason — never a silent PASS
#   distinct pins fell AND totalCount did not fall
#                            -> FAIL. Listings stayed, points vanished: a collapse.
#   distinct pins fell AND totalCount fell
#                            -> WARN. Listings leaving the catalogue legitimately take their
#                               owner's pin with them; this check cannot tell that from a
#                               collapse that coincided with it.
#   otherwise                -> PASS
# Accepted false positive: an owner moving their home point into another owner's geohash
# cell between --record-baseline and this run (minutes, during a deploy) is a real drop with
# an unchanged count. Rare, visible, and resolved by looking at the two pins.
check_geo_pin_diversity() {
    local id="geo-pin-diversity" src="(api ${API_BASE}, baseline ${GEO_BASELINE_FILE})"
    local b_total b_distinct b_api b_epoch b_at b_trunc now age_h

    if (( GEO_MEASURED != 1 )); then
        report_warn "${id}" "NOT assessed: geo-map-pins could not read the catalogue (see its FAIL above) ${src}"
        return
    fi
    if (( GEO_TOTAL == 0 )); then
        report_pass "${id}" "approved catalogue is empty, so there are no pins to compare — not exercised ${src}"
        return
    fi
    if [[ ! -r "${GEO_BASELINE_FILE}" ]]; then
        report_warn "${id}" "NOT assessed: no baseline file — pin diversity is unverified (now ${GEO_DISTINCT} distinct pin(s) for ${GEO_TOTAL} listing(s)). Record one right before the next 'up -d': deploy/smoke.sh --record-baseline (DEPLOY-PRODUCTION.md, 'Обновление стенда' step 5) ${src}"
        return
    fi

    b_total="$(read_kv_key "${GEO_BASELINE_FILE}" total)"
    b_distinct="$(read_kv_key "${GEO_BASELINE_FILE}" distinct_pins)"
    b_trunc="$(read_kv_key "${GEO_BASELINE_FILE}" truncated)"
    b_api="$(read_kv_key "${GEO_BASELINE_FILE}" api_base)"
    b_epoch="$(read_kv_key "${GEO_BASELINE_FILE}" recorded_epoch)"
    b_at="$(read_kv_key "${GEO_BASELINE_FILE}" recorded_at)"
    if [[ ! "${b_total}" =~ ^[0-9]+$ || ! "${b_distinct}" =~ ^[0-9]+$ || ! "${b_epoch}" =~ ^[0-9]+$ ]]; then
        report_warn "${id}" "NOT assessed: baseline file is unreadable (missing total/distinct_pins/recorded_epoch) — re-record it with --record-baseline ${src}"
        return
    fi
    if [[ "${b_api}" != "${API_BASE}" ]]; then
        report_warn "${id}" "NOT assessed: baseline was measured against ${b_api:-<unknown>}, this run against ${API_BASE} — not comparable ${src}"
        return
    fi
    now="$(date +%s)"
    age_h=$(( (now - b_epoch) / 3600 ))
    if (( now - b_epoch > GEO_BASELINE_MAX_AGE_HOURS * 3600 )); then
        report_warn "${id}" "NOT assessed: baseline is ${age_h}h old (recorded ${b_at}), older than ${GEO_BASELINE_MAX_AGE_HOURS}h — it describes a pre-deploy moment, not now; now ${GEO_DISTINCT} distinct pin(s) for ${GEO_TOTAL} listing(s) ${src}"
        return
    fi
    if (( GEO_TRUNCATED == 1 )) || [[ "${b_trunc}" == "1" ]]; then
        report_warn "${id}" "NOT assessed: map-pins hit its row cap (isTruncated) on one side, so distinct-pin counts are of a partial set ${src}"
        return
    fi

    if (( GEO_DISTINCT < b_distinct )); then
        if (( GEO_TOTAL >= b_total )); then
            report_fail "${id}" "map pins COLLAPSED: ${b_distinct} -> ${GEO_DISTINCT} distinct pin(s) while approved listings went ${b_total} -> ${GEO_TOTAL} (baseline recorded ${b_at}). Listings stayed, their points merged — the home-point signature (ADR-024): most likely DemoContentBootstrapRunner did not redistribute the showcase (check BOOTSTRAP_DEMO_CONTENT_ENABLED=true in .env and the 'Demo content bootstrap completed' line in the api startup log). Pre-migration locations survive in ListingLocationsBeforeHomePoint — the undo tape (DEPLOY-PRODUCTION.md section g, 'Первая загрузка после деплоя home-point') ${src}"
        else
            report_warn "${id}" "distinct pins fell ${b_distinct} -> ${GEO_DISTINCT} while approved listings also fell ${b_total} -> ${GEO_TOTAL} (baseline recorded ${b_at}) — consistent with listings leaving the catalogue, but a collapse at the same time would look the same; look at the map if the drop is larger than the departures explain ${src}"
        fi
        return
    fi

    report_pass "${id}" "${GEO_DISTINCT} distinct pin(s) for ${GEO_TOTAL} listing(s) vs baseline ${b_distinct} for ${b_total} (recorded ${b_at}, ${age_h}h ago) — no collapse ${src}"
}

# read_kv_key <file> <key> → value of the last `key=value` line, or nothing. Never sourced:
# the file is plain data and must not be able to execute anything.
read_kv_key() {
    sed -n -E "s/^${2}=//p" "$1" 2>/dev/null | tail -n 1 | tr -d '\r' || true
}

# --record-baseline: measure, write atomically, print what was written. Runs no checks.
# Refuses (exit 1, file untouched) if the measurement fails — a half-measured baseline would
# turn the next comparison into noise.
record_geo_baseline() {
    local dir tmp
    printf 'dorent.am smoke — recording geo baseline — %s\n' "$(date '+%Y-%m-%d %H:%M:%S%z')"
    if ! geo_fetch; then
        printf 'FAIL  record-baseline: %s — baseline NOT written (%s)\n' "${GEO_ERR}" "${GEO_BASELINE_FILE}"
        exit 1
    fi
    dir="$(dirname -- "${GEO_BASELINE_FILE}")"
    if [[ ! -d "${dir}" || ! -w "${dir}" ]]; then
        printf 'FAIL  record-baseline: directory %s is missing or not writable by %s — baseline NOT written\n' "${dir}" "$(id -un)"
        exit 1
    fi
    tmp="$(mktemp "${dir}/.smoke-geo-baseline.XXXXXX")"
    GEO_BASELINE_TMP="${tmp}"
    {
        printf '# written by deploy/smoke.sh --record-baseline; read by check geo-pin-diversity\n'
        printf 'recorded_at=%s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
        printf 'recorded_epoch=%s\n' "$(date +%s)"
        printf 'api_base=%s\n' "${API_BASE}"
        printf 'total=%s\n' "${GEO_TOTAL}"
        printf 'pins=%s\n' "${GEO_PINS}"
        printf 'distinct_pins=%s\n' "${GEO_DISTINCT}"
        printf 'truncated=%s\n' "${GEO_TRUNCATED}"
    } > "${tmp}"
    chmod 644 "${tmp}"
    mv -f -- "${tmp}" "${GEO_BASELINE_FILE}"
    printf 'recorded: total=%s pins=%s distinct_pins=%s truncated=%s (api %s) -> %s\n' \
        "${GEO_TOTAL}" "${GEO_PINS}" "${GEO_DISTINCT}" "${GEO_TRUNCATED}" "${API_BASE}" "${GEO_BASELINE_FILE}"
    if (( GEO_TOTAL > 0 && GEO_DISTINCT == 0 )); then
        printf 'note: the catalogue has listings but no pins RIGHT NOW — the stand is already in the geo-map-pins failure state before the deploy\n'
    fi
    exit 0
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

# Production email gate (ADR-028 section 9). With a missing/invalid email configuration the
# api does NOT crash and stays healthy — by design (M-016) — so every check above stays green
# while sign-up is closed: register and resend-verification answer 503
# auth.registration_unavailable. The only trace is ONE Critical line written by
# EmailConfigurationStartupCheck when the process starts. So the check reads the logs of the
# CURRENT api process (since its State.StartedAt — not `--tail N`, which a busy hour pushes
# the one-off startup line out of) and fails if that line is there. Read-only: docker inspect
# + docker logs, no exec, no request. Images older than the verification release never write
# the line, so on them this passes vacuously; that is correct (they have no gate to close).
# The pattern is the message's fixed prefix in
# src/RentalPlatform.Infrastructure/Services/EmailConfigurationStartupCheck.cs — if that text
# changes, change it here in the same commit.
# Reliable right after a deploy or restart only: on a long-running process the one-off startup
# line can rotate out of the json-file logs (10m x 3), so a PASS then is not a long-term guarantee.
EMAIL_GATE_MARKER='Email verification is NOT operational in Production'
check_email_gate() {
    local cid started lines count sample
    cid="$("${COMPOSE[@]}" ps -q api 2>/dev/null || true)"
    if [[ -z "${cid}" ]]; then
        report_fail "email-gate" "api container not found (see stack-state); cannot prove sign-up is open"
        return
    fi
    started="$(docker inspect -f '{{.State.StartedAt}}' "${cid}" 2>/dev/null || true)"
    if [[ -z "${started}" ]]; then
        report_fail "email-gate" "cannot read api State.StartedAt; cannot prove sign-up is open"
        return
    fi
    lines="$(docker logs --since "${started}" "${cid}" 2>&1 || true)"
    count="$(printf '%s\n' "${lines}" | grep -cF "${EMAIL_GATE_MARKER}" || true)"
    if [[ "${count}" == "0" ]]; then
        report_pass "email-gate" "no email-gate Critical line since api start (${started})"
    else
        # The line names WHAT is missing (key names only, never values, by construction).
        sample="$(printf '%s\n' "${lines}" | grep -F "${EMAIL_GATE_MARKER}" | tail -n 1 | sed 's/^[[:space:]]*//' | cut -c1-220)"
        report_fail "email-gate" "sign-up is CLOSED (register/resend answer 503): ${sample} — fix EMAIL_* / APP_PUBLIC_BASE_URL in .env, then 'up -d api' (DEPLOY-PRODUCTION.md, email-verification release)"
    fi
}

# Google sign-in (ADR-030 section 7). Same silent-failure class as email-gate: with no
# GOOGLE_CLIENT_ID the api does NOT crash (M-016) and every other check stays green, while
# Google sign-in answers 503 auth.external_provider_unavailable and the SPA quietly hides the
# button. Unlike email-gate this check makes a REQUEST, because the nonce endpoint is the
# cheapest exact probe of the very thing the browser does first: one anonymous
# POST /api/auth/external/nonce through the ui container's nginx (so the /api/ proxy route is
# exercised for a POST as well).
#
# This is the ONE request in this script that is not a pure read, stated honestly: it makes
# the api issue a nonce, i.e. adds one entry to the in-memory ExternalAuthNonceStore
# (capacity 50 000, expires by itself after 5 min, no DB write, nothing persisted) and spends
# one token of the external-auth-nonce rate limit (30/min per client IP; loopback requests
# carry no CF-Connecting-IP, so they share the nginx container's bucket, which no public user
# is in). The nonce value is never printed. This is a liveness/configuration check, NOT a
# data invariant — ADR-010 is not involved.
#
# What it CANNOT see: whether https://dorent.am is in the client's Authorized JavaScript
# origins in Google Cloud Console. Google enforces that in the browser popup only; the server
# never hears about it. That stays a manual post-deploy check (DEPLOY-PRODUCTION.md, Google
# sign-in release).
GOOGLE_GATE_MARKER='Google sign-in is NOT operational in Production'
check_google_nonce() {
    local url="${UI_BASE}/api/auth/external/nonce"
    local out code body cid started sample=""
    # ONE request (code and body together): a second request would issue a second nonce.
    out="$(curl -s --max-time "${CURL_TIMEOUT_LOCAL}" -X POST --data '' -w '\n%{http_code}' "${url}" 2>/dev/null || true)"
    code="${out##*$'\n'}"
    body="${out%$'\n'*}"
    [[ "${code}" =~ ^[0-9]{3}$ ]] || code="000"
    [[ "${out}" == *$'\n'* ]] || body=""

    if [[ "${code}" == "200" && "${body}" =~ \"nonce\"[[:space:]]*:[[:space:]]*\"[A-Za-z0-9_-]{20,}\" ]]; then
        report_pass "google-nonce" "POST ${url} -> 200 with a nonce (Google sign-in configured server-side; Authorized JavaScript origins are a manual check)"
        return
    fi

    case "${code}" in
        200)
            report_fail "google-nonce" "POST ${url} -> 200 but no \"nonce\" string in the body (unexpected response shape — ExternalNonceResponse changed?)"
            ;;
        503)
            # Name the cause from the CURRENT api process's own startup line, as email-gate does.
            cid="$("${COMPOSE[@]}" ps -q api 2>/dev/null || true)"
            if [[ -n "${cid}" ]]; then
                started="$(docker inspect -f '{{.State.StartedAt}}' "${cid}" 2>/dev/null || true)"
                if [[ -n "${started}" ]] && docker logs --since "${started}" "${cid}" 2>&1 | grep -qF "${GOOGLE_GATE_MARKER}"; then
                    sample=" — api startup logged '${GOOGLE_GATE_MARKER}'"
                fi
            fi
            report_fail "google-nonce" "POST ${url} -> 503 auth.external_provider_unavailable: Google sign-in is OFF${sample}. Usual cause: GOOGLE_CLIENT_ID missing/empty in ${ENV_FILE} (set it, then 'up -d api'); otherwise the nonce store is full (Critical 'nonce store is full' in the api log). Also confirm https://dorent.am is in the client's Authorized JavaScript origins (DEPLOY-PRODUCTION.md, Google sign-in release)"
            ;;
        404)
            report_fail "google-nonce" "POST ${url} -> 404: the running api image predates Google sign-in (ADR-030) or the /api/ proxy lost the route"
            ;;
        405)
            report_fail "google-nonce" "POST ${url} -> 405: the SPA fallback answered the POST — nginx lost the /api/ location (regression class M-008)"
            ;;
        429)
            report_fail "google-nonce" "POST ${url} -> 429: the external-auth-nonce rate limit (30/min) is exhausted for loopback requests — something on this host is hammering the endpoint; re-run in a minute and investigate"
            ;;
        *)
            report_fail "google-nonce" "POST ${url} -> ${code} (expected 200 + nonce; 000 = nothing answered, 5xx = api/proxy broken)"
            ;;
    esac
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
    case "${1:-}" in
        "") ;;
        --record-baseline) record_geo_baseline ;;
        -h|--help)
            printf 'usage: %s [--record-baseline]\n' "$0"
            exit 0
            ;;
        *)
            printf 'smoke.sh: unknown argument: %s (usage: smoke.sh [--record-baseline])\n' "$1" >&2
            exit 2
            ;;
    esac

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
    check_geo_pin_diversity
    check_access_token_config
    check_domains
    check_public_routes
    check_access_gate
    check_uploads_routing
    check_websocket_negotiate
    check_email_gate
    check_google_nonce

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
