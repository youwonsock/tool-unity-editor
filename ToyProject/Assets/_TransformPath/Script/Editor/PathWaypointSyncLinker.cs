using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common.TransformPath
{
    [InitializeOnLoad]
    internal static class PathWaypointSyncLinker
    {
        #region Constants

        private const float POSITION_SYNC_EPSILON_SQR =
            PathLinkEditorUtility.POSITION_SYNC_EPSILON_SQR;

        #endregion


        #region Inner Classes / Structs

        private sealed class TrackedRegistry : IPathTrackedTarget
        {
            private readonly List<PathWaypointSyncGroup> _groups = new List<PathWaypointSyncGroup>();
            private readonly List<Vector3[]> _groupSnapshots = new List<Vector3[]>();
            private Dictionary<Transform, PathData> _transformToPathData =
                new Dictionary<Transform, PathData>();
            private string _structureSignature = string.Empty;

            public PathWaypointSyncRegistry Target { get; }
            public MultiPathData MultiPathData { get; private set; }
            public int Id { get; }
            public bool IsAlive => Target != null;

            public TrackedRegistry(PathWaypointSyncRegistry target)
            {
                Target = target;
                Id = target.GetInstanceID();
            }

            public string CachedStructureSignature => _structureSignature;

            public void RefreshReferences()
            {
                if (Target == null)
                {
                    _structureSignature = string.Empty;
                    _groups.Clear();
                    _groupSnapshots.Clear();
                    _transformToPathData.Clear();
                    MultiPathData = null;
                    return;
                }

                MultiPathData = Target.GetComponent<MultiPathData>();
                _transformToPathData =
                    PathWaypointSyncEditorUtility.BuildTransformToPathDataMap(MultiPathData);

                _groups.Clear();
                IReadOnlyList<PathWaypointSyncGroup> sourceGroups = Target.Groups;
                for (int i = 0; i < sourceGroups.Count; i++)
                {
                    if (sourceGroups[i] != null)
                        _groups.Add(sourceGroups[i]);
                }

                string currentSignature = BuildStructureSignature();
                bool structureChanged = string.IsNullOrEmpty(_structureSignature)
                    || !string.Equals(
                        _structureSignature,
                        currentSignature,
                        StringComparison.Ordinal);

                if (structureChanged || _groupSnapshots.Count != _groups.Count)
                {
                    _structureSignature = currentSignature;
                    ResizeSnapshots();
                    CaptureSnapshots();
                }
            }

            public bool Sync()
            {
                if (Target == null || !Target.SyncInEditor || _groups.Count == 0)
                    return false;

                bool syncedAny = false;
                for (int groupIndex = 0; groupIndex < _groups.Count; groupIndex++)
                {
                    PathWaypointSyncGroup group = _groups[groupIndex];
                    if (group == null || !group.Enabled)
                        continue;

                    Vector3[] snapshots = _groupSnapshots[groupIndex];
                    if (PathWaypointSyncEditorUtility.TrySyncGroupByMovement(
                            group,
                            snapshots,
                            POSITION_SYNC_EPSILON_SQR,
                            _transformToPathData,
                            MultiPathData))
                        syncedAny = true;
                }

                CaptureSnapshots();
                return syncedAny;
            }

            private string BuildStructureSignature()
            {
                if (Target == null)
                    return "null";

                System.Text.StringBuilder signature = new System.Text.StringBuilder();
                signature.Append(Target.SyncInEditor);
                signature.Append('|');
                signature.Append(_groups.Count);
                signature.Append('|');

                for (int groupIndex = 0; groupIndex < _groups.Count; groupIndex++)
                {
                    PathWaypointSyncGroup group = _groups[groupIndex];
                    if (group == null)
                        continue;

                    signature.Append(group.Enabled);
                    signature.Append(':');
                    Transform[] members = group.Members;
                    if (members == null)
                        continue;

                    for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                    {
                        Transform member = members[memberIndex];
                        signature.Append(member == null ? 0 : member.GetInstanceID());
                        signature.Append(',');
                    }

                    signature.Append(';');
                }

                return signature.ToString();
            }

            private void ResizeSnapshots()
            {
                while (_groupSnapshots.Count < _groups.Count)
                    _groupSnapshots.Add(Array.Empty<Vector3>());

                if (_groupSnapshots.Count > _groups.Count)
                    _groupSnapshots.RemoveRange(_groups.Count, _groupSnapshots.Count - _groups.Count);
            }

            private void CaptureSnapshots()
            {
                for (int groupIndex = 0; groupIndex < _groups.Count; groupIndex++)
                {
                    PathWaypointSyncGroup group = _groups[groupIndex];
                    Transform[] members = group == null ? null : group.Members;
                    int memberCount = members == null ? 0 : members.Length;

                    if (_groupSnapshots[groupIndex] == null
                        || _groupSnapshots[groupIndex].Length != memberCount)
                        _groupSnapshots[groupIndex] = new Vector3[memberCount];

                    Vector3[] snapshots = _groupSnapshots[groupIndex];
                    for (int memberIndex = 0; memberIndex < memberCount; memberIndex++)
                    {
                        Transform member = members[memberIndex];
                        snapshots[memberIndex] = member == null
                            ? Vector3.zero
                            : member.position;
                    }
                }
            }
        }

        #endregion


        #region Member Variables

        private static readonly PathTrackedTargets<PathWaypointSyncRegistry, TrackedRegistry> TARGETS =
            new PathTrackedTargets<PathWaypointSyncRegistry, TrackedRegistry>(
                target => new TrackedRegistry(target),
                changed => changed is MultiPathData || changed is PathData || changed is PathWaypointSyncRegistry);

        #endregion


        // The static field subscribes to the change monitor when the Editor loads.
        static PathWaypointSyncLinker()
        {
        }
    }
}
