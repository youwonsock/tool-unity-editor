#!/usr/bin/env bash
# Builds the sample scenes for Android with IL2CPP from a snapshot of the working
# tree. This proves the code survives IL2CPP translation, stripping and the native
# compile, not that it runs: nothing here starts the result on a device.
#
#   Tools/run-il2cpp-build.sh
#
# See Tools/snapshot-worktree.sh for the environment variables. The build needs
# about 2 GB of free disk, and switches the test worktree to Android until the
# next test run switches it back.
set -euo pipefail

source "$(dirname "$0")/snapshot-worktree.sh"

output=$results/FlowFieldIl2cpp.apk
rm -f "$output"
echo "== Build"
code=0
"$unity" -batchmode -quit -projectPath "$worktree/ToyProject" -buildTarget Android \
    -executeMethod Common.FlowField.Editor.FlowFieldPlayerProbeBuild.BuildIl2cppCheck \
    -flowFieldProbeApp "$output" -logFile "$results/il2cpp-build.log" || code=$?

if [ "$code" -ne 0 ]; then
    echo "Build failed. See $results/il2cpp-build.log"
    grep -E 'error CS|error:|Exception:|BuildFailedException' "$results/il2cpp-build.log" | sort -u | head -20 || true
else
    ls -la "$output"
fi
rm -rf "$worktree/ToyProject/Library/Il2cppBuildCache"
exit "$code"
