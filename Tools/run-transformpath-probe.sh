#!/usr/bin/env bash
# Builds a Mono Mac Player from a snapshot of the working tree and runs
# TransformPathPlayerProbe several times, because one run says nothing about how
# much the numbers vary. The Player opens a window, because the measurements
# need real frames.
#
#   Tools/run-transformpath-probe.sh
#   Tools/run-transformpath-probe.sh --runs 3 --skip-build
#
# See Tools/snapshot-worktree.sh for the environment variables. The build stops
# with exit code 124 after UNITY_BUILD_TIMEOUT seconds, each run after
# TRANSFORMPATH_PROBE_TIMEOUT seconds (default 900).
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
    run_limited "$build_timeout" "$unity" -batchmode -quit -projectPath "$worktree/ToyProject" -buildTarget StandaloneOSX \
        -executeMethod Common.TransformPath.PlayerProbe.TransformPathPlayerProbeBuild.Build \
        -transformPathProbeApp "$app" -logFile "$results/transformpath-build.log" || {
            code=$?
            echo "Build failed. See $results/transformpath-build.log"
            grep -E 'error CS|Exception:' "$results/transformpath-build.log" | sort -u | head -20 || true
            exit "$code"
        }
fi

status=0
csvs=()
for run in $(seq 1 "$runs"); do
    csv=$results/transformpath-probe-$run.csv
    rm -f "$csv"
    echo "== Run $run"
    code=0
    run_limited "$timeout" "$app/Contents/MacOS/ToyProject" -logFile "$results/transformpath-run-$run.log" \
        -screen-fullscreen 0 -screen-width 960 -screen-height 540 \
        -transformPathProbeOut "$csv" || code=$?
    [ "$code" -eq 0 ] || status=$code
    [ ! -f "$csv" ] || csvs+=("$csv")
done

if [ "${#csvs[@]}" -gt 0 ]; then
    python3 "$(dirname "$0")/summarize-transformpath-probe.py" "${csvs[@]}"
else
    echo "No probe results. See $results/transformpath-run-*.log"
fi
exit "$status"
