using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Immutable identity of the field source used by a Volume3D view.  The
    /// identity deliberately excludes ordinary Goal, obstacle and Modifier
    /// changes so an already published result can remain visible while the
    /// next result is being built.
    /// </summary>
    internal readonly struct FlowFieldVolumeVisualizationSource
    {
        internal readonly FlowFieldGridSpace Grid;
        internal readonly Bounds WorldBounds;
        internal readonly FlowFieldBakeMode BakeMode;
        internal readonly FlowFieldVolumeBakeData StaticBakeData;
        internal readonly int StaticBakeRevision;
        internal readonly int StaticBakeContentGeneration;
        internal readonly long SessionGeneration;
        internal readonly long ResultId;

        internal FlowFieldVolumeVisualizationSource(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData staticBakeData,
            int staticBakeRevision,
            long sessionGeneration,
            long resultId)
            : this(
                grid,
                worldBounds,
                bakeMode,
                staticBakeData,
                staticBakeRevision,
                staticBakeData != null ? staticBakeData.ContentGeneration : -1,
                sessionGeneration,
                resultId)
        {
        }

        internal FlowFieldVolumeVisualizationSource(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData staticBakeData,
            int staticBakeRevision,
            int staticBakeContentGeneration,
            long sessionGeneration,
            long resultId)
        {
            Grid = grid;
            WorldBounds = worldBounds;
            BakeMode = bakeMode;
            StaticBakeData = staticBakeData;
            StaticBakeRevision = staticBakeRevision;
            StaticBakeContentGeneration = staticBakeContentGeneration;
            SessionGeneration = sessionGeneration;
            ResultId = resultId;
        }

        internal bool IsValid => Grid.IsValid
            && Grid.SpaceMode == FlowFieldSpaceMode.Volume3D
            && FlowFieldGridSpace.IsFinite(WorldBounds.center)
            && FlowFieldGridSpace.IsFinite(WorldBounds.size)
            && WorldBounds.size.x > 0f
            && WorldBounds.size.y > 0f
            && WorldBounds.size.z > 0f;

        internal bool HasSameDisplaySource(in FlowFieldVolumeVisualizationSource other)
            => Grid.MatchesBounds(other.Grid)
                && Approximately(WorldBounds, other.WorldBounds)
                && BakeMode == other.BakeMode
                && ReferenceEquals(StaticBakeData, other.StaticBakeData)
                && StaticBakeRevision == other.StaticBakeRevision
                && StaticBakeContentGeneration == other.StaticBakeContentGeneration;

        private static bool Approximately(Bounds left, Bounds right)
            => FlowFieldGridSpace.Approximately(left.center, right.center, 0.00000001d)
                && FlowFieldGridSpace.Approximately(left.size, right.size, 0.00000001d);
    }

    /// <summary>
    /// Non-owning view shared by Surface2D and Volume3D readers. The view
    /// carries the result identity together with the arrays needed by one
    /// sampling or drawing operation and never exposes writable arrays.
    /// </summary>
    internal readonly struct FlowFieldReadView
    {
        private readonly FlowFieldGridSpace _grid;
        private readonly Bounds _worldBounds;
        private readonly FlowFieldSpaceMode _spaceMode;
        private readonly FlowFieldSurfaceData _surface;
        private readonly bool[] _blocked;
        private readonly FlowFieldGoalFlags[] _goalFlags;
        private readonly Vector3[] _directions;
        private readonly float[] _speeds;
        private readonly bool[] _modifierInfluence;

        internal FlowFieldVolumeVisualizationSource Source { get; }
        internal FlowFieldGridSpace Grid => _grid;
        internal Bounds WorldBounds => _worldBounds;
        internal FlowFieldSpaceMode SpaceMode => _spaceMode;
        internal int Revision { get; }
        internal long ResultId { get; }
        internal bool IsRuntimeFinal { get; }
        internal bool HasActiveGoal { get; }
        internal int ResolvedGoalIndex { get; }
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
            Bounds worldBounds,
            FlowFieldSpaceMode spaceMode,
            FlowFieldVolumeVisualizationSource source,
            int revision,
            long resultId,
            bool isRuntimeFinal,
            bool hasActiveGoal,
            int resolvedGoalIndex,
            FlowFieldSurfaceData surface,
            bool[] blocked,
            FlowFieldGoalFlags[] goalFlags,
            Vector3[] directions,
            float[] speeds,
            bool[] modifierInfluence)
        {
            _grid = grid;
            _worldBounds = worldBounds;
            _spaceMode = spaceMode;
            Source = source;
            Revision = revision;
            ResultId = resultId;
            IsRuntimeFinal = isRuntimeFinal;
            HasActiveGoal = hasActiveGoal;
            ResolvedGoalIndex = resolvedGoalIndex;
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

        internal static FlowFieldReadView CreateSurface(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            bool hasActiveGoal,
            int resolvedGoalIndex,
            int revision,
            long resultId,
            bool[] blocked,
            FlowFieldGoalFlags[] goalFlags,
            Vector3[] directions,
            float[] speeds,
            bool[] modifierInfluence)
            => new FlowFieldReadView(
                grid,
                surface != null ? surface.BakeBoundsWorld : default,
                FlowFieldSpaceMode.Surface2D,
                default,
                revision,
                resultId,
                true,
                hasActiveGoal,
                resolvedGoalIndex,
                surface,
                blocked,
                goalFlags,
                directions,
                speeds,
                modifierInfluence);

        internal static FlowFieldReadView CreateVolume(
            FlowFieldVolumeVisualizationSource source,
            int revision,
            bool[] blocked,
            Vector3[] directions,
            float[] speeds,
            bool isRuntimeFinal)
            => new FlowFieldReadView(
                source.Grid,
                source.WorldBounds,
                FlowFieldSpaceMode.Volume3D,
                source,
                revision,
                source.ResultId,
                isRuntimeFinal,
                false,
                -1,
                null,
                blocked,
                null,
                directions,
                speeds,
                null);

        internal static FlowFieldReadView CreateStaticVolume(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldVolumeBakeData bakeData,
            int revision,
            long resultId,
            bool[] blocked,
            Vector3[] directions,
            float[] speeds)
        {
            FlowFieldVolumeVisualizationSource source =
                new FlowFieldVolumeVisualizationSource(
                    grid,
                    worldBounds,
                    FlowFieldBakeMode.StaticBaked,
                    bakeData,
                    bakeData != null ? bakeData.Revision : -1,
                    bakeData != null ? bakeData.ContentGeneration : -1,
                    0L,
                    resultId);
            return CreateVolume(source, revision, blocked, directions, speeds, false);
        }
    }

    /// <summary>
    /// Selects display coordinates without changing the underlying grid.  A
    /// selection is an implicit sequence, so a full-volume repaint does not
    /// need a second array containing every displayed index.
    /// </summary>
    internal readonly struct FlowFieldVolumeDisplaySelection
    {
        internal const int DefaultFullVolumeLimit = 8192;
        internal const int DefaultSliceLimit = 4096;

        private readonly int _sampleX;
        private readonly int _sampleY;
        private readonly int _sampleZ;
        private readonly int _fixedCoordinate;
        private readonly FlowFieldVolumeSliceAxis _sliceAxis;

        internal FlowFieldVolumeGizmoMode Mode { get; }
        internal FlowFieldVolumeSliceAxis SliceAxis => _sliceAxis;
        internal int Slice => _fixedCoordinate;
        internal int SampleCountX => _sampleX;
        internal int SampleCountY => _sampleY;
        internal int SampleCountZ => _sampleZ;
        internal int DisplayCount { get; }
        internal int TotalCellCount { get; }
        internal int Stride { get; }

        private FlowFieldVolumeDisplaySelection(
            FlowFieldVolumeGizmoMode mode,
            FlowFieldVolumeSliceAxis sliceAxis,
            int fixedCoordinate,
            int sampleX,
            int sampleY,
            int sampleZ,
            int displayCount,
            int totalCellCount,
            int stride)
        {
            Mode = mode;
            _sliceAxis = sliceAxis;
            _fixedCoordinate = fixedCoordinate;
            _sampleX = sampleX;
            _sampleY = sampleY;
            _sampleZ = sampleZ;
            DisplayCount = displayCount;
            TotalCellCount = totalCellCount;
            Stride = stride;
        }

        internal static bool TryCreate(
            FlowFieldGridSpace grid,
            FlowFieldVolumeGizmoMode mode,
            FlowFieldVolumeSliceAxis sliceAxis,
            int requestedSlice,
            int maxDisplayCells,
            out FlowFieldVolumeDisplaySelection selection)
        {
            selection = default;
            if (!grid.IsValid || grid.SpaceMode != FlowFieldSpaceMode.Volume3D
                || maxDisplayCells <= 0
                || !Enum.IsDefined(typeof(FlowFieldVolumeGizmoMode), mode)
                || !Enum.IsDefined(typeof(FlowFieldVolumeSliceAxis), sliceAxis))
                return false;

            int totalCellCount = grid.CellCount;
            int fixedCoordinate = NormalizeSlice(grid, sliceAxis, requestedSlice);
            int limit = mode == FlowFieldVolumeGizmoMode.Slice
                ? Math.Min(maxDisplayCells, DefaultSliceLimit)
                : Math.Min(maxDisplayCells, DefaultFullVolumeLimit);

            int stride = FindSmallestStride(grid, mode, sliceAxis, limit);
            if (stride <= 0)
                return false;

            int sampleX = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.X
                ? 1
                : ComputeSampleCount(grid.Width, stride);
            int sampleY = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.Y
                ? 1
                : ComputeSampleCount(grid.Height, stride);
            int sampleZ = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.Z
                ? 1
                : ComputeSampleCount(grid.Depth, stride);
            long displayCount = (long)sampleX * sampleY * sampleZ;
            if (displayCount <= 0L || displayCount > limit || displayCount > int.MaxValue)
                return false;

            selection = new FlowFieldVolumeDisplaySelection(
                mode,
                sliceAxis,
                fixedCoordinate,
                sampleX,
                sampleY,
                sampleZ,
                (int)displayCount,
                totalCellCount,
                stride);
            return true;
        }

        internal bool TryGetCoordinate(
            FlowFieldGridSpace grid,
            int ordinal,
            out int x,
            out int y,
            out int z,
            out int flatIndex)
        {
            x = 0;
            y = 0;
            z = 0;
            flatIndex = -1;
            if (!grid.IsValid || ordinal < 0 || ordinal >= DisplayCount)
                return false;

            long remaining = ordinal;
            int sampleXPosition = (int)(remaining % _sampleX);
            remaining /= _sampleX;
            int sampleZPosition = (int)(remaining % _sampleZ);
            int sampleYPosition = (int)(remaining / _sampleZ);

            x = MapSampleCoordinate(sampleXPosition, _sampleX, grid.Width);
            y = MapSampleCoordinate(sampleYPosition, _sampleY, grid.Height);
            z = MapSampleCoordinate(sampleZPosition, _sampleZ, grid.Depth);
            if (Mode == FlowFieldVolumeGizmoMode.Slice)
            {
                if (_sliceAxis == FlowFieldVolumeSliceAxis.X)
                    x = _fixedCoordinate;
                else if (_sliceAxis == FlowFieldVolumeSliceAxis.Y)
                    y = _fixedCoordinate;
                else
                    z = _fixedCoordinate;
            }

            if (!grid.IsLocalInBounds(x, y, z))
                return false;
            flatIndex = grid.ToFlatIndex(x, y, z);
            return flatIndex >= 0 && flatIndex < grid.CellCount;
        }

        internal static int ComputeSampleCount(int dimension, int stride)
        {
            if (dimension <= 0 || stride <= 0)
                return 0;
            if (dimension == 1)
                return 1;

            long ceil = ((long)dimension + stride - 1L) / stride;
            long count = Math.Max(2L, ceil);
            return (int)Math.Min((long)dimension, count);
        }

        internal static int MapSampleCoordinate(int position, int sampleCount, int dimension)
        {
            if (dimension <= 1 || sampleCount <= 1)
                return 0;
            return (int)((long)position * (dimension - 1L) / (sampleCount - 1L));
        }

        private static int FindSmallestStride(
            FlowFieldGridSpace grid,
            FlowFieldVolumeGizmoMode mode,
            FlowFieldVolumeSliceAxis sliceAxis,
            int limit)
        {
            int largestDimension = Math.Max(grid.Width, Math.Max(grid.Height, grid.Depth));
            int low = 1;
            int high = Math.Max(1, largestDimension);
            if (!IsWithinLimit(grid, mode, sliceAxis, high, limit))
                return 0;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (IsWithinLimit(grid, mode, sliceAxis, middle, limit))
                    high = middle;
                else
                    low = middle + 1;
            }
            return low;
        }

        private static bool IsWithinLimit(
            FlowFieldGridSpace grid,
            FlowFieldVolumeGizmoMode mode,
            FlowFieldVolumeSliceAxis sliceAxis,
            int stride,
            int limit)
        {
            int sampleX = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.X
                ? 1
                : ComputeSampleCount(grid.Width, stride);
            int sampleY = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.Y
                ? 1
                : ComputeSampleCount(grid.Height, stride);
            int sampleZ = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.Z
                ? 1
                : ComputeSampleCount(grid.Depth, stride);
            return (long)sampleX * sampleY * sampleZ <= limit;
        }

        private static int NormalizeSlice(
            FlowFieldGridSpace grid,
            FlowFieldVolumeSliceAxis axis,
            int requestedSlice)
        {
            int length = axis == FlowFieldVolumeSliceAxis.X
                ? grid.Width
                : axis == FlowFieldVolumeSliceAxis.Y ? grid.Height : grid.Depth;
            if (length <= 1)
                return 0;
            return requestedSlice < 0
                ? length / 2
                : Math.Max(0, Math.Min(length - 1, requestedSlice));
        }
    }
}
