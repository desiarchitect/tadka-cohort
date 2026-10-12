#!/usr/bin/env bash
# Run ONE scenario of the evolution workload against ONE checkpoint branch (day-03 ... day-16).
# Bash twin of run-checkpoint.ps1 (same steps, same manifest, same output); see that file's header and
# docs/runbooks/evolution-1-to-1m.md. Needs: git, docker, the .NET 10 SDK, k6, jq, curl.
#
#   evolution/run-checkpoint.sh --day day-03 --scenario history
#   evolution/run-checkpoint.sh --day day-07 --scenario orders --variant sync-slow
#   evolution/run-checkpoint.sh --day day-16 --scenario history --variant replicas3 --repeat 3
set -euo pipefail
(( BASH_VERSINFO[0] >= 4 )) || { echo "bash 4+ needed (macOS: brew install bash, then run with that bash)" >&2; exit 1; }

DAY='' SCENARIO='' VARIANT='' REPEAT=1 RATES='' STEP_SECONDS=0 QUICK=0
WORKTREE_ROOT='' KEEP_INFRA=0 REUSE_INFRA=0 SKIP_BUILD=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --day) DAY="$2"; shift 2 ;;
    --scenario) SCENARIO="$2"; shift 2 ;;
    --variant) VARIANT="$2"; shift 2 ;;
    --repeat) REPEAT="$2"; shift 2 ;;
    --rates) RATES="$2"; shift 2 ;;
    --step-seconds) STEP_SECONDS="$2"; shift 2 ;;
    --worktree-root) WORKTREE_ROOT="$2"; shift 2 ;;
    --quick) QUICK=1; shift ;;   # class-length run (~2 min); never compare against a full run
    --keep-infra) KEEP_INFRA=1; shift ;;
    --reuse-infra) REUSE_INFRA=1; shift ;;
    --skip-build) SKIP_BUILD=1; shift ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

# Full run: 7 steps x 40 s after a 20 s warm-up (~5 min). Quick: 5 steps x 20 s after 10 s (~2 min). Explicit values win.
if [[ -z "$RATES" ]]; then if [[ $QUICK -eq 1 ]]; then RATES='10,25,50,100,200'; else RATES='5,10,25,50,100,200,400'; fi; fi
if (( STEP_SECONDS <= 0 )); then if [[ $QUICK -eq 1 ]]; then STEP_SECONDS=20; else STEP_SECONDS=40; fi; fi
WARMUP_SECONDS=20; [[ $QUICK -eq 1 ]] && WARMUP_SECONDS=10

step() { printf '\n\033[36m== %s\033[0m\n' "$*"; }
fail() { printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

command -v jq >/dev/null || fail "jq not found on PATH (sudo apt install jq / brew install jq)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HARNESS="$(dirname "$HERE")"
MANIFEST="$HERE/checkpoints.json"
PROJECT=tadka-evo
[[ -n "$WORKTREE_ROOT" ]] || WORKTREE_ROOT="$(dirname "$HARNESS")/tadka-evo"
[[ -n "$DAY" && -n "$SCENARIO" ]] || fail "--day and --scenario are required"
[[ "$SCENARIO" =~ ^(browse|history|hotkey|orders)$ ]] || fail "--scenario must be browse|history|hotkey|orders"

CP="$(jq -c --arg d "$DAY" '.checkpoints[$d] // empty' "$MANIFEST")"
[[ -n "$CP" ]] || fail "unknown checkpoint '$DAY'. Known: $(jq -r '.checkpoints | keys | join(", ")' "$MANIFEST")"
VAR='null'
if [[ -n "$VARIANT" ]]; then
  VAR="$(jq -c --arg v "$VARIANT" '.variants[$v] // empty' <<<"$CP")"
  [[ -n "$VAR" ]] || fail "checkpoint $DAY has no variant '$VARIANT'. Known: $(jq -r '.variants | keys | if length == 0 then "(none)" else join(", ") end' <<<"$CP")"
fi
SCALE_OUT="$(jq -c '.scaleOut // empty' <<<"$VAR")"
BASE_URL="$(jq -r 'if .scaleOut then .scaleOut.baseUrl else empty end' <<<"$VAR")"
[[ -n "$BASE_URL" ]] || BASE_URL="$(jq -r '.baseUrl' <<<"$CP")"
LABEL_BASE="$DAY-$SCENARIO${VARIANT:+-$VARIANT}"; [[ $QUICK -eq 1 ]] && LABEL_BASE="$LABEL_BASE-quick"
RESULTS_DIR="$HARNESS/evolution/results"
LOG_DIR="$HARNESS/evolution/logs/$LABEL_BASE"
mkdir -p "$RESULTS_DIR" "$LOG_DIR"

# ---- preflight -----------------------------------------------------------------------------------------
step "Preflight ($LABEL_BASE)"
for t in git docker k6 curl; do command -v "$t" >/dev/null || fail "$t not found on PATH"; done
[[ -n "$SCALE_OUT" ]] || command -v dotnet >/dev/null || fail "dotnet (the .NET 10 SDK) not found on PATH"
docker info >/dev/null 2>&1 || fail "Docker is not running"
OWNER="$(docker ps -a --filter 'name=^tadka-postgres$' --format '{{ index .Labels "com.docker.compose.project" }}' 2>/dev/null || true)"
if [[ -n "$OWNER" && "$OWNER" != "$PROJECT" ]]; then
  fail "container tadka-postgres belongs to compose project '$OWNER'. Stop that stack first (docker compose down in that folder)."
fi
if [[ -z "$SCALE_OUT" && $REUSE_INFRA -eq 0 ]]; then
  for port in $(jq -r '.services[].port' <<<"$CP"); do
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then fail "port $port is already in use; stop whatever is running there"; fi
  done
fi

# ---- worktree ------------------------------------------------------------------------------------------
step "Worktree for $DAY"
WT="$WORKTREE_ROOT/$DAY"
git -C "$HARNESS" fetch --quiet origin "$DAY"
if [[ -e "$WT/.git" ]]; then
  git -C "$WT" checkout --quiet --detach "origin/$DAY"
else
  mkdir -p "$WORKTREE_ROOT"
  git -C "$HARNESS" worktree add --quiet --detach "$WT" "origin/$DAY"
fi
SHA="$(git -C "$WT" rev-parse --short HEAD)"
echo "  $WT @ $SHA"

wait_healthy() {
  local deadline=$(( $(date +%s) + 240 )) n state
  for n in "$@"; do
    while true; do
      state="$(docker inspect "$n" --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' 2>/dev/null || true)"
      if [[ "$state" == healthy || "$state" == running ]]; then echo "  $n $state"; break; fi
      (( $(date +%s) < deadline )) || fail "$n did not become healthy (last: $state)"
      sleep 3
    done
  done
}
wait_http() {
  local url="$1" deadline=$(( $(date +%s) + ${2:-180} )) code
  while true; do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$url" || true)"
    if [[ "$code" =~ ^[1-4][0-9][0-9]$ ]]; then return 0; fi
    (( $(date +%s) < deadline )) || fail "$url did not answer in time"
    sleep 2
  done
}

PIDS=()
cleanup() {
  local pid
  for pid in "${PIDS[@]:-}"; do
    [[ -n "$pid" ]] || continue
    pkill -TERM -P "$pid" 2>/dev/null || true   # `dotnet run` starts the app as a child process
    kill -TERM "$pid" 2>/dev/null || true
  done
  if [[ $KEEP_INFRA -eq 0 ]]; then
    (cd "$WT" && docker compose -p "$PROJECT" --profile scale-out down -v --remove-orphans >/dev/null 2>&1) || true
  fi
}
trap cleanup EXIT

cd "$WT"
# ---- infrastructure ------------------------------------------------------------------------------------
if [[ $REUSE_INFRA -eq 0 ]]; then
  step "Reset infrastructure (docker compose down -v, up -d)"
  docker compose -p "$PROJECT" down -v --remove-orphans >/dev/null 2>&1 || true
  if [[ -n "$SCALE_OUT" ]]; then
    docker compose -p "$PROJECT" --profile scale-out up -d --build
  else
    docker compose -p "$PROJECT" up -d
  fi
fi
mapfile -t HEALTHY < <(jq -r '.waitHealthy[]' <<<"$CP")
wait_healthy "${HEALTHY[@]}"

# ---- services ------------------------------------------------------------------------------------------
if [[ -n "$SCALE_OUT" ]]; then
  mapfile -t RUNNING < <(jq -r '.running[]' <<<"$SCALE_OUT")
  mapfile -t STOPPED < <(jq -r '.stopped[]' <<<"$SCALE_OUT")
  step "Scale-out: running ${RUNNING[*]}"
  docker compose -p "$PROJECT" --profile scale-out start "${RUNNING[@]}" >/dev/null
  wait_http "$BASE_URL/health"
  if [[ ${#STOPPED[@]} -gt 0 ]]; then docker compose -p "$PROJECT" --profile scale-out stop "${STOPPED[@]}" >/dev/null; fi
  sleep 5
  wait_http "$BASE_URL/health"
else
  if [[ $SKIP_BUILD -eq 0 ]]; then
    step "dotnet build Tadka.slnx"
    dotnet build Tadka.slnx -c Debug --nologo -v q
  fi
  SKIP="$(jq -r '(.skip // []) | join(" ")' <<<"$VAR")"
  while IFS= read -r svc; do
    name="$(jq -r '.name' <<<"$svc")"; project="$(jq -r '.project' <<<"$svc")"; port="$(jq -r '.port' <<<"$svc")"
    if [[ " $SKIP " == *" $name "* ]]; then echo -e "  \033[33mskipping $name (variant $VARIANT)\033[0m"; continue; fi
    # service env, then the variant's env for this service on top
    mapfile -t ENVS < <(jq -r --arg n "$name" --argjson v "$VAR" \
      '((.env // {}) + (($v // {}).env[$n] // {})) | to_entries[] | "\(.key)=\(.value)"' <<<"$svc")
    log="$LOG_DIR/$name.log"
    env ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$port" "${ENVS[@]}" \
      dotnet run --no-build --no-launch-profile --project "$project" >"$log" 2>&1 &
    PIDS+=("$!")
    echo "  started $name on :$port (log: $log)"
  done < <(jq -c '.services[]' <<<"$CP")
  while IFS= read -r svc; do
    name="$(jq -r '.name' <<<"$svc")"; port="$(jq -r '.port' <<<"$svc")"
    [[ " $SKIP " == *" $name "* ]] && continue
    wait_http "http://localhost:$port/health"
    echo "  $name /health OK"
  done < <(jq -c '.services[]' <<<"$CP")
fi

# ---- seed ----------------------------------------------------------------------------------------------
if [[ $REUSE_INFRA -eq 0 ]]; then
  step "Seed ~200k orders (same data on every checkpoint)"
  docker exec -i tadka-postgres psql -U tadka -d tadka -v ON_ERROR_STOP=1 < "$HARNESS/$(jq -r '.seed' "$MANIFEST")"
  if [[ -n "$(docker ps --filter 'name=^tadka-postgres-replica$' --format '{{.Names}}')" ]]; then
    deadline=$(( $(date +%s) + 90 ))
    until [[ "$(docker exec tadka-postgres-replica psql -U tadka -d tadka -tAc 'select count(*) from ordering.orders' 2>/dev/null || echo 0)" -ge 150000 ]]; do
      (( $(date +%s) < deadline )) || fail "read replica did not catch up with the seed"
      sleep 2
    done
    echo "  read replica caught up"
  fi
fi

# ---- run -----------------------------------------------------------------------------------------------
AUTH="$(jq -r '.auth' <<<"$CP")"
TITLE="$(jq -r '.title' <<<"$CP")"; ADRS="$(jq -r '.adrs' <<<"$CP")"; NOTE="$(jq -r '.note // ""' <<<"$VAR")"
REPLICAS=1; [[ -n "$SCALE_OUT" ]] && REPLICAS="$(jq -r '.running | length' <<<"$SCALE_OUT")"
for (( i = 1; i <= REPEAT; i++ )); do
  LABEL="$LABEL_BASE"; (( REPEAT > 1 )) && LABEL="$LABEL_BASE-r$i"
  if [[ "$SCENARIO" == hotkey && -n "$(docker ps --filter 'name=^tadka-redis$' --format '{{.Names}}')" ]]; then
    docker exec tadka-redis redis-cli DEL "$(jq -r '.hotKey' "$MANIFEST")" >/dev/null   # start cold: the stampede is the point
  fi
  TOKEN=''
  if [[ "$AUTH" == true ]]; then   # tokens live 15 min; log in fresh for every repeat
    TOKEN="$(curl -s -X POST "$BASE_URL/api/v1/auth/login" -H 'Content-Type: application/json' \
      -d "$(jq -c '.loginBody' "$MANIFEST")" | jq -r '.accessToken // empty')"
    [[ -n "$TOKEN" ]] || fail "login returned no accessToken"
  fi
  step "k6: $LABEL  (base $BASE_URL, rates $RATES, ${STEP_SECONDS}s/step)"
  K6_ARGS=(run -e "BASE_URL=$BASE_URL" -e "SCENARIO=$SCENARIO" -e "LABEL=$LABEL" -e "OUT_DIR=$RESULTS_DIR"
           -e "RATES=$RATES" -e "STEP_SECONDS=$STEP_SECONDS" -e "WARMUP_SECONDS=$WARMUP_SECONDS")
  [[ -n "$TOKEN" ]] && K6_ARGS+=(-e "TOKEN=$TOKEN")
  k6 "${K6_ARGS[@]}" "$HARNESS/k6/evolution.js" || [[ -f "$RESULTS_DIR/$LABEL.json" ]] || fail "k6 failed for $LABEL"

  jq -n --arg label "$LABEL" --arg day "$DAY" --arg commit "$SHA" --arg scenario "$SCENARIO" --arg variant "$VARIANT" \
        --arg title "$TITLE" --arg adrs "$ADRS" --arg note "$NOTE" --argjson replicas "$REPLICAS" \
        --argjson quick "$( [[ $QUICK -eq 1 ]] && echo true || echo false )" --arg rates "$RATES" --argjson stepSeconds "$STEP_SECONDS" \
        --arg os "$(uname -sr)" --argjson cores "$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 0)" \
        --arg ranAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        '{label:$label, day:$day, commit:$commit, scenario:$scenario, variant:$variant, title:$title, adrs:$adrs,
          note:$note, replicas:$replicas, quick:$quick, rates:$rates, stepSeconds:$stepSeconds,
          machine:{os:$os, cores:$cores}, ranAt:$ranAt}' \
    > "$RESULTS_DIR/$LABEL.meta.json"
done

printf '\n\033[32mDone. Results in evolution/results/. Build the comparison with: node evolution/report.mjs\033[0m\n'
