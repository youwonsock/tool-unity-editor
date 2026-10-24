using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Edit-mode waypoint sync groups. Positions are synced in the Editor only;
    /// Play uses saved Transform positions.
    /// </summary>
    public sealed class PathWaypointSyncRegistry : MonoBehaviour
    {
        [SerializeField] private bool _syncInEditor = true;
        [SerializeField] private List<PathWaypointSyncGroup> _groups = new List<PathWaypointSyncGroup>();

        public bool SyncInEditor => _syncInEditor;
        public IReadOnlyList<PathWaypointSyncGroup> Groups => _groups;
    }
}
