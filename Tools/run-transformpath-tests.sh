#!/usr/bin/env bash
# Runs the TransformPath EditMode tests from a snapshot of the working tree, so
# an open Unity Editor keeps its project lock.
#
#   Tools/run-transformpath-tests.sh
#
# See Tools/snapshot-worktree.sh for the environment variables. The run stops
# with exit code 124 after TRANSFORMPATH_TEST_TIMEOUT seconds (default 900).
set -euo pipefail

source "$(dirname "$0")/snapshot-worktree.sh"

output=$results/transformpath-tests.xml
timeout=${TRANSFORMPATH_TEST_TIMEOUT:-900}
rm -f "$output"
echo "== EditMode tests"
code=0
run_limited "$timeout" "$unity" -batchmode -projectPath "$worktree/ToyProject" -runTests -testPlatform EditMode \
    -assemblyNames Common.TransformPath.Tests -testResults "$output" \
    -logFile "$results/transformpath-tests.log" || code=$?

if [ ! -f "$output" ]; then
    echo "No test results. See $results/transformpath-tests.log"
    grep -E 'error CS|No valid Unity Editor license' "$results/transformpath-tests.log" | sort -u | head -20 || true
    exit "$([ "$code" -ne 0 ] && echo "$code" || echo 1)"
fi
python3 - "$output" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
print(f"total {root.get('total')}  passed {root.get('passed')}  failed {root.get('failed')}")
for case in root.iter('test-case'):
    if case.get('result') != 'Passed':
        message = case.find('.//message')
        print('FAIL', case.get('fullname'), (message.text or '').strip()[:300] if message is not None else '')
PY
exit "$code"
