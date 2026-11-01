# Sourced by the Unity scripts. Copies the current working tree into a separate
# Git worktree, so an open Unity Editor keeps its project lock. Ignored files
# are left out, which also proves a fresh checkout is sufficient.
#
# UNITY_PATH           Unity executable (default: the Hub install of the project version)
# UNITY_TEST_WORKTREE  Reused worktree (default: under TMPDIR, keeps its Library)
# UNITY_TEST_RESULTS   Folder for results and logs (default: <worktree>-results)
# UNITY_TEST_TREE      Commit to test instead of the working tree

root=$(git rev-parse --show-toplevel)
version=$(sed -n 's/^m_EditorVersion: //p' "$root/ToyProject/ProjectSettings/ProjectVersion.txt")
unity=${UNITY_PATH:-/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity}
worktree=${UNITY_TEST_WORKTREE:-${TMPDIR:-/tmp}/tool-unity-editor-tests}
results=${UNITY_TEST_RESULTS:-$worktree-results}
mkdir -p "$results"

[ -e "$worktree/.git" ] || git -C "$root" worktree add -q --detach "$worktree" HEAD

if [ -n "${UNITY_TEST_TREE:-}" ]; then
    tree=$UNITY_TEST_TREE
else
    # A temporary index captures tracked edits and new files without touching
    # the real index, the stash or any branch.
    index=$(mktemp "${TMPDIR:-/tmp}/run-tests-index.XXXXXX")
    rm -f "$index"
    trap 'rm -f "$index"' EXIT
    GIT_INDEX_FILE=$index git -C "$root" add -A
    tree=$(GIT_INDEX_FILE=$index git -C "$root" write-tree)
fi
git -C "$worktree" read-tree -u --reset "$tree"
git -C "$worktree" clean -fdq
# Tests generate large ignored assets. Library stays, because it holds the import.
git -C "$worktree" clean -fdqX -- ToyProject/Assets
