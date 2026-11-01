#!/usr/bin/env bash
# Builds a Mono Mac Player from a snapshot of the working tree and runs
# TransformPathPlayerProbe several times, because one run says nothing about how
# much the numbers vary. The Player opens a window, because the measurements
# need real frames.
#
#   Tools/run-transformpath-probe.sh
#   Tools/run-transformpath-probe.sh --runs 3 --skip-build
#
# See Tools/snapshot-worktree.sh for the environment variables. Each run stops
# with exit code 124 after TRANSFORMPATH_PROBE_TIMEOUT seconds (default 900).
set -euo pipefail

runs=5
skip_build=
while [ $# -gt 0 ]; do
    case "$1" in
        --runs) runs=$2; shift 2 ;;
        --skip-build) skip_build=1; shift ;;
        *) echo "Unknown option: $1" >&2; exit 64 ;;
    esac
done

source "$(dirname "$0")/snapshot-worktree.sh"

app=$results/TransformPathProbe.app
timeout=${TRANSFORMPATH_PROBE_TIMEOUT:-900}

if [ -z "$skip_build" ]; then
    rm -rf "$app"
    echo "== Build"
    "$unity" -batchmode -quit -projectPath "$worktree/ToyProject" -buildTarget StandaloneOSX \
        -executeMethod Common.TransformPath.PlayerProbe.TransformPathPlayerProbeBuild.Build \
        -transformPathProbeApp "$app" -logFile "$results/transformpath-build.log" || {
            echo "Build failed. See $results/transformpath-build.log"
            grep -E 'error CS|Exception:' "$results/transformpath-build.log" | sort -u | head -20 || true
            exit 1
        }
fi

status=0
csvs=()
for run in $(seq 1 "$runs"); do
    csv=$results/transformpath-probe-$run.csv
    rm -f "$csv"
    echo "== Run $run"
    "$app/Contents/MacOS/ToyProject" -logFile "$results/transformpath-run-$run.log" \
        -screen-fullscreen 0 -screen-width 960 -screen-height 540 \
        -transformPathProbeOut "$csv" &
    pid=$!
    deadline=$((SECONDS + timeout))
    code=
    while kill -0 "$pid" 2>/dev/null; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            kill -9 "$pid" 2>/dev/null || true
            wait "$pid" 2>/dev/null || true
            echo "Timed out after $timeout seconds."
            code=124
            break
        fi
        sleep 1
    done
    if [ -z "$code" ]; then
        code=0
        wait "$pid" || code=$?
    fi
    [ "$code" -eq 0 ] || status=$code
    [ ! -f "$csv" ] || csvs+=("$csv")
done

if [ "${#csvs[@]}" -gt 0 ]; then
    python3 "$(dirname "$0")/summarize-transformpath-probe.py" "${csvs[@]}"
else
    echo "No probe results. See $results/transformpath-run-*.log"
fi
exit "$status"
