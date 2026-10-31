#!/usr/bin/env bash
# Runs the Unity tests against a snapshot of the current working tree inside a
# separate Git worktree, so an open Unity Editor keeps its project lock.
# Ignored files are left out, which also proves a fresh checkout is sufficient.
#
#   Tools/run-tests.sh                                  EditMode, PlayMode and Python
#   Tools/run-tests.sh --platform EditMode --filter 'Regex|Name'
#   FLOWFIELD_PERF=1 Tools/run-tests.sh --platform EditMode --filter FlowFieldPerformanceProbes
#
# UNITY_PATH           Unity executable (default: the Hub install of the project version)
# UNITY_TEST_WORKTREE  Reused test worktree (default: under TMPDIR, keeps its Library)
# UNITY_TEST_RESULTS   Folder for XML results and logs (default: <worktree>-results)
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

root=$(git rev-parse --show-toplevel)
version=$(sed -n 's/^m_EditorVersion: //p' "$root/ToyProject/ProjectSettings/ProjectVersion.txt")
unity=${UNITY_PATH:-/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity}
worktree=${UNITY_TEST_WORKTREE:-${TMPDIR:-/tmp}/tool-unity-editor-tests}
results=${UNITY_TEST_RESULTS:-$worktree-results}
mkdir -p "$results"

[ -e "$worktree/.git" ] || git -C "$root" worktree add -q --detach "$worktree" HEAD

# A temporary index captures tracked edits and new files without touching the
# real index, the stash or any branch.
index=$(mktemp "${TMPDIR:-/tmp}/run-tests-index.XXXXXX")
rm -f "$index"
trap 'rm -f "$index"' EXIT
GIT_INDEX_FILE=$index git -C "$root" add -A
tree=$(GIT_INDEX_FILE=$index git -C "$root" write-tree)
git -C "$worktree" read-tree -u --reset "$tree"
git -C "$worktree" clean -fdq

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
        -s "$root/ToyProject/Assets/_OptimizeTool/Docs/Performance" -p 'test_*.py' || status=$?
fi

exit "$status"
