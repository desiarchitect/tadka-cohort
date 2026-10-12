#!/usr/bin/env bash
# Run the whole evolution ladder, then build the report. Bash twin of run-ladder.ps1 (same rows, same order,
# same infrastructure reuse); see that file and docs/runbooks/evolution-1-to-1m.md.
#
#   evolution/run-ladder.sh
#   evolution/run-ladder.sh --repeat 3 --only history
set -euo pipefail
(( BASH_VERSINFO[0] >= 4 )) || { echo "bash 4+ needed (macOS: brew install bash, then run with that bash)" >&2; exit 1; }

REPEAT=1 ONLY='' FORCE=0 PASS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repeat) REPEAT="$2"; shift 2 ;;
    --only) ONLY="$2"; shift 2 ;;
    --force) FORCE=1; shift ;;
    --rates|--step-seconds|--worktree-root) PASS+=("$1" "$2"); shift 2 ;;   # passed through to run-checkpoint.sh
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# day scenario variant  (in run order; a day's rows are contiguous; "-" = no variant)
LADDER=(
  "day-03 history -" "day-03 hotkey -" "day-03 browse -" "day-03 orders -"
  "day-05 history -" "day-05 hotkey -"
  "day-06 history -" "day-06 hotkey -" "day-06 browse -"
  "day-07 orders sync-unprotected" "day-07 orders sync-slow" "day-07 orders async-slow"
  "day-09 orders payment-slow" "day-09 orders payment-down"
  "day-16 history -" "day-16 hotkey -" "day-16 browse -"
  "day-16 orders payment-outage" "day-16 orders payment-down"
  "day-16 history replicas1" "day-16 history replicas3"
)
if [[ -n "$ONLY" ]]; then
  FILTERED=(); for row in "${LADDER[@]}"; do read -r _ s _ <<<"$row"; [[ "$s" == "$ONLY" ]] && FILTERED+=("$row"); done
  LADDER=("${FILTERED[@]}")
fi

kind() { [[ "$1" == replicas* ]] && echo scale || echo plain; }
n=${#LADDER[@]}
for (( i = 0; i < n; i++ )); do
  read -r day scenario variant <<<"${LADDER[$i]}"; [[ "$variant" == - ]] && variant=''
  label="$day-$scenario${variant:+-$variant}"
  probe="$label.json"; (( REPEAT > 1 )) && probe="$label-r1.json"
  if [[ $FORCE -eq 0 && -f "$HERE/results/$probe" ]]; then echo "skip $label (result exists; --force to redo)"; continue; fi

  args=(--day "$day" --scenario "$scenario" --repeat "$REPEAT" "${PASS[@]}")
  [[ -n "$variant" ]] && args+=(--variant "$variant")
  if (( i > 0 )); then
    read -r pday _ pvar <<<"${LADDER[$((i - 1))]}"
    # reuse the day's infrastructure only between rows of the same day and the same kind (scale-out has its own containers)
    [[ "$pday" == "$day" && "$(kind "$pvar")" == "$(kind "${variant:--}")" ]] && args+=(--reuse-infra --skip-build)
  fi
  if (( i < n - 1 )); then
    read -r nday _ nvar <<<"${LADDER[$((i + 1))]}"
    [[ "$nday" == "$day" && "$(kind "$nvar")" == "$(kind "${variant:--}")" ]] && args+=(--keep-infra)
  fi
  printf '\n\033[35m######## [%d/%d] %s\033[0m\n' "$((i + 1))" "$n" "$label"
  "$HERE/run-checkpoint.sh" "${args[@]}"
done

node "$HERE/report.mjs"
