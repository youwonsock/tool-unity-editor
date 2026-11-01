#!/usr/bin/env bash
# Builds a Mono Mac Player from a snapshot of the working tree and runs
# FlowFieldPlayerProbe: frame measurements of Surface and Volume rebuilds on
# the Managed and GPU paths, then every sample scene in both bake modes.
# The Player opens a window, because the GPU path needs real frames.
#
#   Tools/run-player-probe.sh
#   Tools/run-player-probe.sh --skip-build    Run the Player built last time
#
# See Tools/snapshot-worktree.sh for the environment variables.
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

if [ -z "$skip_build" ]; then
    rm -rf "$app"
    echo "== Build"
    "$unity" -batchmode -quit -projectPath "$worktree/ToyProject" -buildTarget StandaloneOSX \
        -executeMethod Common.FlowField.Editor.FlowFieldPlayerProbeBuild.Build \
        -flowFieldProbeApp "$app" -logFile "$results/player-build.log" || {
            echo "Build failed. See $results/player-build.log"
            grep -E 'error CS|Exception:' "$results/player-build.log" | sort -u | head -20 || true
            exit 1
        }
fi

rm -f "$csv"
echo "== Run"
code=0
"$app/Contents/MacOS/ToyProject" -logFile "$results/player-run.log" \
    -screen-fullscreen 0 -screen-width 960 -screen-height 540 \
    -flowFieldProbeOut "$csv" || code=$?

if [ -f "$csv" ]; then
    python3 "$(dirname "$0")/summarize-player-probe.py" "$csv"
else
    echo "No probe results. See $results/player-run.log"
fi
echo "Results: $csv"
exit "$code"
