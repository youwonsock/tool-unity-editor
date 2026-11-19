#!/usr/bin/env bash
# Builds the sample scenes for Android with IL2CPP from a snapshot of the working
# tree. This proves the code survives IL2CPP translation, stripping and the native
# compile, not that it runs: nothing here starts the result on a device. The
# entry point lives in FlowField's Dev/Editor assembly
# (FlowFieldPlayerProbeBuild.BuildIl2cppCheck), but the build covers every
# enabled scene in the build settings, from all three tools.
#
#   Tools/run-il2cpp-build.sh
#
# See Tools/snapshot-worktree.sh for the environment variables. The build stops
# with exit code 124 after UNITY_BUILD_TIMEOUT seconds. It needs about 2 GB of free
# disk, and leaves the snapshot worktree on Android until the next Player build
# switches it back.
set -euo pipefail

if [ $# -gt 0 ]; then
    echo "Unknown option: $1" >&2
    exit 64
fi

source "$(dirname "$0")/snapshot-worktree.sh"

output=$results/FlowFieldIl2cpp.apk
rm -f "$output"
echo "== Build"
code=0
run_limited "$build_timeout" "$unity" -batchmode -quit -projectPath "$worktree/ToyProject" -buildTarget Android \
    -executeMethod Common.FlowField.PlayerProbe.Editor.FlowFieldPlayerProbeBuild.BuildIl2cppCheck \
    -flowFieldProbeApp "$output" -logFile "$results/il2cpp-build.log" || code=$?

if [ "$code" -ne 0 ]; then
    echo "Build failed. See $results/il2cpp-build.log"
    grep -E 'error CS|error:|Exception:|BuildFailedException' "$results/il2cpp-build.log" | sort -u | head -20 || true
else
    ls -la "$output"
fi
rm -rf "$worktree/ToyProject/Library/Il2cppBuildCache"
exit "$code"
