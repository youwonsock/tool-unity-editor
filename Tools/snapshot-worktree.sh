# Sourced by the Unity scripts. Copies the current working tree into a separate
# Git worktree, so an open Unity Editor keeps its project lock. Ignored files
# are left out, which also proves a fresh checkout is sufficient. Also provides
# run_limited.
#
# UNITY_PATH           Unity executable (default: the Hub install of the project version)
# UNITY_WORKTREE       Reused worktree (default: under TMPDIR, keeps its Library)
# UNITY_RESULTS        Folder for results and logs (default: <worktree>-results)
# UNITY_TREE           Commit to build instead of the working tree
# UNITY_BUILD_TIMEOUT  Seconds a Player build may take (default 3600)

root=$(git rev-parse --show-toplevel)
version=$(sed -n 's/^m_EditorVersion: //p' "$root/ToyProject/ProjectSettings/ProjectVersion.txt")
unity=${UNITY_PATH:-/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity}
worktree=${UNITY_WORKTREE:-${TMPDIR:-/tmp}/tool-unity-editor-snapshot}
results=${UNITY_RESULTS:-$worktree-results}
build_timeout=${UNITY_BUILD_TIMEOUT:-3600}
mkdir -p "$results"

stop_tree() {
    local child
    for child in $(pgrep -P "$1" 2>/dev/null); do
        stop_tree "$child"
    done
    kill -9 "$1" 2>/dev/null || true
}

stop_limited() {
    stop_tree "$limited_pid"
    wait "$limited_pid" 2>/dev/null || true
}

# run_limited <seconds> <command...> stops the command and everything it started
# with exit code 124 after that time. Unity can deadlock and never exit, and macOS
# has no timeout command. A run in the background ignores Ctrl+C, so the trap
# passes it on.
run_limited() {
    local limit=$1
    shift
    "$@" &
    limited_pid=$!
    trap 'stop_limited; exit 130' INT TERM
    local deadline=$((SECONDS + limit))
    while kill -0 "$limited_pid" 2>/dev/null; do
        if [ "$SECONDS" -ge "$deadline" ]; then
            stop_limited
            echo "Timed out after $limit seconds."
            return 124
        fi
        sleep 1
    done
    wait "$limited_pid"
}

[ -e "$worktree/.git" ] || git -C "$root" worktree add -q --detach "$worktree" HEAD

if [ -n "${UNITY_TREE:-}" ]; then
    tree=$UNITY_TREE
else
    # A temporary index captures tracked edits and new files without touching
    # the real index, the stash or any branch.
    index=$(mktemp "${TMPDIR:-/tmp}/snapshot-index.XXXXXX")
    rm -f "$index"
    trap 'rm -f "$index"' EXIT
    GIT_INDEX_FILE=$index git -C "$root" add -A
    tree=$(GIT_INDEX_FILE=$index git -C "$root" write-tree)
fi
git -C "$worktree" read-tree -u --reset "$tree"
git -C "$worktree" clean -fdq
# Earlier runs leave large ignored assets. Library stays, because it holds the import.
git -C "$worktree" clean -fdqX -- ToyProject/Assets
