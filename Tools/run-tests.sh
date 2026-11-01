#!/usr/bin/env bash
# Runs the Unity tests against a snapshot of the current working tree.
#
#   Tools/run-tests.sh                                  EditMode, PlayMode and Python
#   Tools/run-tests.sh --platform EditMode --filter 'Regex|Name'
#   FLOWFIELD_PERF=1 Tools/run-tests.sh --platform EditMode --filter FlowFieldPerformanceProbes
#
# See Tools/snapshot-worktree.sh for the environment variables.
#
# TransformPathEditorUiTests need a graphical Editor and are skipped here. Run
# them from the Test Runner window of the open Editor.
set -euo pipefail

platform=all
filter=
while [ $# -gt 0 ]; do
    case "$1" in
        --platform) platform=$2; shift 2 ;;
        --filter) filter=$2; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 64 ;;
    esac
done

source "$(dirname "$0")/snapshot-worktree.sh"

status=0

summarize() {
    python3 - "$1" <<'PY'
import sys
import xml.etree.ElementTree as ET
run = ET.parse(sys.argv[1]).getroot()
print({key: run.get(key) for key in ("total", "passed", "failed", "skipped", "result")})
for suite in run.iter("test-suite"):
    if suite.get("type") == "Assembly":
        print(" ", suite.get("name"), {key: suite.get(key) for key in ("total", "passed", "failed", "skipped")})
for case in run.iter("test-case"):
    if case.get("result") == "Failed":
        message = case.find("failure/message")
        text = "" if message is None else (message.text or "").strip().replace("\n", " ")
        print("FAIL", case.get("fullname"), text[:400])
PY
}

run_unity() {
    local mode=$1
    local arguments=(-batchmode -projectPath "$worktree/ToyProject" -runTests -testPlatform "$mode"
        -testResults "$results/$mode.xml" -logFile "$results/$mode.log")
    [ -z "$filter" ] || arguments+=(-testFilter "$filter")
    rm -f "$results/$mode.xml"
    echo "== $mode"
    local code=0
    "$unity" "${arguments[@]}" || code=$?
    if [ -f "$results/$mode.xml" ]; then
        summarize "$results/$mode.xml"
    else
        echo "No test results. See $results/$mode.log"
        grep -E 'error CS' "$results/$mode.log" | sort -u | head -20 || true
    fi
    [ "$code" -eq 0 ] || status=$code
}

case "$platform" in
    all) run_unity EditMode; run_unity PlayMode ;;
    EditMode | PlayMode) run_unity "$platform" ;;
    *) echo "Unknown platform: $platform" >&2; exit 64 ;;
esac

if [ "$platform" = all ] && [ -z "$filter" ]; then
    echo "== Python"
    PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover \
        -s "$worktree/ToyProject/Assets/_OptimizeTool/Docs/Performance" -p 'test_*.py' || status=$?
fi

git -C "$worktree" clean -fdqX -- ToyProject/Assets
exit "$status"
