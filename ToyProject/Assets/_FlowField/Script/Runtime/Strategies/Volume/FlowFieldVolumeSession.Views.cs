using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldField<Common.FlowField.FlowFieldVolumeRequest>;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    /// <summary>Exports and read views of the published Volume3D field for bakes and tools.</summary>
    internal sealed partial class FlowFieldVolumeSession
    {
        internal bool TryExport(
            out FlowFieldVolumeRequest request,
            out bool[] blocked,
            out uint[] topology,
            out FlowFieldGoalFlags[] goalFlags,
            out int[] next,
            out Vector3[] directions,
            out float[] speeds,
            out Vector3[] escapeDirections)
        {
            return TryExport(
                out request,
                out blocked,
                out topology,
                out goalFlags,
                out next,
                out directions,
                out speeds,
                out escapeDirections,
                out _);
        }

        internal bool TryExport(
            out FlowFieldVolumeRequest request,
            out bool[] blocked,
            out uint[] topology,
            out FlowFieldGoalFlags[] goalFlags,
            out int[] next,
            out Vector3[] directions,
            out float[] speeds,
            out Vector3[] escapeDirections,
            out int resolvedGoalIndex)
        {
            request = default;
            blocked = null;
            topology = null;
            goalFlags = null;
            next = null;
            directions = null;
            speeds = null;
            escapeDirections = null;
            resolvedGoalIndex = -1;
            if (_committed == null)
                return false;
            request = _committed.Request;
            blocked = (bool[])_committed.Blocked.Clone();
            topology = (uint[])_committed.Topology.Clone();
            goalFlags = (FlowFieldGoalFlags[])_committed.GoalFlags.Clone();
            next = (int[])_committed.NextCells.Clone();
            // Static bake assets store the immutable base field. The current
            // Default Direction and registered Modifiers are runtime inputs
            // and are composed again when the snapshot is loaded.
            directions = (Vector3[])_committed.BaseDirections.Clone();
            speeds = (float[])_committed.BaseSpeeds.Clone();
            escapeDirections = (Vector3[])_committed.EscapeDirections.Clone();
            resolvedGoalIndex = _committed.GoalIndex;
            return true;
        }

        internal override bool TryGetFieldInfo(out FlowFieldFieldInfo info)
        {
            info = default;
            if (_committed == null
                || State == FlowFieldRuntimeState.Uninitialized
                || State == FlowFieldRuntimeState.Released
                || State == FlowFieldRuntimeState.Suspended
                || State == FlowFieldRuntimeState.Faulted)
                return false;

            if (_requests.HasLatest
                && !_committed.Request.HasSameGrid(_requests.Latest))
                return false;

            FlowFieldVolumeRequest request = _committed.Request;
            // A running build or a move within the Goal cell leaves the
            // published result behind the requested Goal, as on Surface2D.
            FlowFieldVolumeRequest requested = _requests.HasLatest && request.BakeMode != FlowFieldBakeMode.StaticBaked
                ? _requests.Latest
                : request;
            Vector3 resolvedGoal = default;
            bool hasResolvedGoal = _committed.GoalIndex >= 0
                && _committed.GoalIndex < _committed.Grid.CellCount;
            if (hasResolvedGoal)
            {
                _committed.Grid.FromFlatIndex(
                    _committed.GoalIndex,
                    out int goalX,
                    out int goalY,
                    out int goalZ);
                resolvedGoal = _committed.Grid.LocalToWorldCenter(goalX, goalY, goalZ);
            }

            info = new FlowFieldFieldInfo(
                request.Grid,
                request.WorldBounds,
                FlowFieldSpaceMode.Volume3D,
                request.BakeMode,
                Revision,
                requested.HasGoal,
                requested.GoalWorld,
                requested.GoalInfluenceRadius,
                hasResolvedGoal,
                resolvedGoal);
            return info.IsValid;
        }

        internal override bool TryGetPublishedField(out FlowFieldField field)
        {
            field = null;
            if (_committed == null
                || State == FlowFieldRuntimeState.Uninitialized
                || State == FlowFieldRuntimeState.Released
                || State == FlowFieldRuntimeState.Suspended
                || State == FlowFieldRuntimeState.Faulted)
                return false;
            // A newly accepted grid makes the published cells meaningless.
            if (_requests.HasLatest
                && !_committed.Request.HasSameGrid(_requests.Latest))
                return false;
            field = _committed;
            return true;
        }
    }
}
