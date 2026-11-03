#!/usr/bin/env bash
# Builds a Mono Mac Player from a snapshot of the working tree and runs
# FlowFieldPlayerProbe: frame measurements of Surface and Volume rebuilds, then
# every sample scene in both bake modes. The Player opens a window, because the
# measurements need real frames.
#
#   Tools/run-player-probe.sh
#   Tools/run-player-probe.sh --skip-build    Run the Player built last time
#
# See Tools/snapshot-worktree.sh for the environment variables. The build stops
# with exit code 124 after UNITY_BUILD_TIMEOUT seconds, the run after
# FLOWFIELD_PROBE_TIMEOUT seconds (default 1800).
set -euo pipefail

skip_build=
while [ $# -gt 0 ]; do
    case "$1" in
        --skip-build) skip_build=1; shift ;;
        *) echo "Unknown option: $1" >&2; exit 64 ;;
    esac
done

source "$(dirname "$0")/snapshot-worktree.sh"

app=$results/FlowFieldProbe.app
csv=$results/player-probe.csv
timeout=${FLOWFIELD_PROBE_TIMEOUT:-1800}

if [ -z "$skip_build" ]; then
    rm -rf "$app"
    echo "== Build"
    run_limited "$build_timeout" "$unity" -batchmode -quit -projectPath "$worktree/ToyProject" -buildTarget StandaloneOSX \
        -executeMethod Common.FlowField.Editor.FlowFieldPlayerProbeBuild.Build \
        -flowFieldProbeApp "$app" -logFile "$results/player-build.log" || {
            code=$?
            echo "Build failed. See $results/player-build.log"
            grep -E 'error CS|Exception:' "$results/player-build.log" | sort -u | head -20 || true
            exit "$code"
        }
fi

rm -f "$csv"
echo "== Run"
code=0
run_limited "$timeout" "$app/Contents/MacOS/ToyProject" -logFile "$results/player-run.log" \
    -screen-fullscreen 0 -screen-width 960 -screen-height 540 \
    -flowFieldProbeOut "$csv" || code=$?

if [ -f "$csv" ]; then
    python3 "$(dirname "$0")/summarize-player-probe.py" "$csv"
else
    echo "No probe results. See $results/player-run.log"
fi
echo "Results: $csv"
exit "$code"
