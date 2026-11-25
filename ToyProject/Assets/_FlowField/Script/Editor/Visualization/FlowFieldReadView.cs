using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Non-owning view shared by Surface2D and Volume3D readers. The view
    /// carries the result identity together with the arrays needed by one
    /// sampling or drawing operation and never exposes writable arrays.
    /// </summary>
    internal readonly struct FlowFieldReadView
    {
        private readonly FlowFieldGridSpace _grid;
        private readonly FlowFieldSpaceMode _spaceMode;
        private readonly FlowFieldSurfaceData _surface;
        private readonly bool[] _blocked;
        private readonly FlowFieldGoalFlags[] _goalFlags;
        private readonly Vector3[] _directions;
        private readonly float[] _speeds;
        private readonly bool[] _modifierInfluence;

        internal FlowFieldVolumeVisualizationSource Source { get; }
        internal FlowFieldGridSpace Grid => _grid;
        internal long ResultId => Source.ResultId;
        internal FlowFieldSurfaceData Surface => _surface;
        internal int CellCount => _grid.CellCount;
        internal bool IsValid
        {
            get
            {
                if (!_grid.IsValid
                    || _blocked == null
                    || _directions == null
                    || _speeds == null
                    || _blocked.Length != CellCount
                    || _directions.Length != CellCount
                    || _speeds.Length != CellCount)
                    return false;
                if (_spaceMode == FlowFieldSpaceMode.Volume3D)
                    return Source.IsValid;
                return _surface != null
                    && _surface.IsValid
                    && _goalFlags != null
                    && _modifierInfluence != null
                    && _goalFlags.Length == CellCount
                    && _modifierInfluence.Length == CellCount;
            }
        }

        private FlowFieldReadView(
            FlowFieldGridSpace grid,
            FlowFieldSpaceMode spaceMode,
            FlowFieldVolumeVisualizationSource source,
            FlowFieldSurfaceData surface,
            bool[] blocked,
            FlowFieldGoalFlags[] goalFlags,
            Vector3[] directions,
            float[] speeds,
            bool[] modifierInfluence)
        {
            _grid = grid;
            _spaceMode = spaceMode;
            Source = source;
            _surface = surface;
            _blocked = blocked;
            _goalFlags = goalFlags;
            _directions = directions;
            _speeds = speeds;
            _modifierInfluence = modifierInfluence;
        }

        internal bool TryGetCell(
            int flatIndex,
            out bool blocked,
            out Vector3 direction,
            out float speed)
        {
            blocked = false;
            direction = Vector3.zero;
            speed = 0f;
            if (!IsValid || _spaceMode != FlowFieldSpaceMode.Volume3D
                || flatIndex < 0 || flatIndex >= CellCount)
                return false;
            blocked = _blocked[flatIndex];
            direction = _directions[flatIndex];
            speed = _speeds[flatIndex];
            return FlowFieldGridSpace.IsFinite(direction)
                && FlowFieldGridSpace.IsFinite(speed)
                && speed >= 0f;
        }

        internal bool TryGetCell(
            int flatIndex,
            out bool blocked,
            out FlowFieldGoalFlags goalFlags,
            out Vector3 direction,
            out float speed,
            out bool modifierInfluence)
        {
            blocked = false;
            goalFlags = FlowFieldGoalFlags.None;
            direction = Vector3.zero;
            speed = 0f;
            modifierInfluence = false;
            if (!IsValid || _spaceMode != FlowFieldSpaceMode.Surface2D
                || flatIndex < 0 || flatIndex >= CellCount)
                return false;
            blocked = _blocked[flatIndex];
            goalFlags = _goalFlags[flatIndex];
            direction = _directions[flatIndex];
            speed = _speeds[flatIndex];
            modifierInfluence = _modifierInfluence[flatIndex];
            return FlowFieldGridSpace.IsFinite(direction)
                && FlowFieldGridSpace.IsFinite(speed)
                && speed >= 0f;
        }

        /// <summary>
        /// The Surface2D cell a Goal in this cell anchors to, as the build
        /// resolves it: the cell itself when it has ground, otherwise the
        /// nearest cell that has. -1 when no cell has ground.
        /// </summary>
        internal int FindSurfaceGoalAnchor(int localX, int localZ)
            => FlowFieldSurfaceGraphTraversal.FindNearestSurfaceAnchor(_grid, _surface, localX, localZ);

        internal static FlowFieldReadView CreateSurface(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            bool[] blocked,
            FlowFieldGoalFlags[] goalFlags,
            Vector3[] directions,
            float[] speeds,
            bool[] modifierInfluence)
            => new FlowFieldReadView(
                grid,
                FlowFieldSpaceMode.Surface2D,
                default,
                surface,
                blocked,
                goalFlags,
                directions,
                speeds,
                modifierInfluence);

        /// <summary>A view of a published Surface2D field, for this repaint only.</summary>
        internal static FlowFieldReadView FromSurfaceField(FlowFieldField field)
            => CreateSurface(
                field.Grid,
                field.Surface,
                field.Blocked,
                field.GoalFlags,
                field.FinalDirections,
                field.FinalSpeeds,
                field.ModifierInfluence);

        /// <summary>
        /// A view of a published Volume3D field, for this repaint only. The
        /// Manager's Revision tells one publication from the next.
        /// </summary>
        internal static FlowFieldReadView FromVolumeField(FlowFieldField<FlowFieldVolumeRequest> field, long resultId)
            => CreateVolume(
                new FlowFieldVolumeVisualizationSource(
                    field.Grid,
                    field.Request.WorldBounds,
                    field.Request.BakeMode,
                    field.Request.BakeData,
                    field.Request.BakeRevision,
                    resultId),
                field.Blocked,
                field.FinalDirections,
                field.FinalSpeeds);

        internal static FlowFieldReadView CreateVolume(
            FlowFieldVolumeVisualizationSource source,
            bool[] blocked,
            Vector3[] directions,
            float[] speeds)
            => new FlowFieldReadView(
                source.Grid,
                FlowFieldSpaceMode.Volume3D,
                source,
                null,
                blocked,
                null,
                directions,
                speeds,
                null);

        internal static FlowFieldReadView CreateStaticVolume(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData bakeData,
            long resultId,
            bool[] blocked,
            Vector3[] directions,
            float[] speeds)
        {
            FlowFieldVolumeVisualizationSource source =
                new FlowFieldVolumeVisualizationSource(
                    grid,
                    worldBounds,
                    bakeMode,
                    bakeData,
                    bakeData != null ? bakeData.Revision : -1,
                    bakeData != null ? bakeData.ContentGeneration : -1,
                    resultId);
            return CreateVolume(source, blocked, directions, speeds);
        }
    }
}
