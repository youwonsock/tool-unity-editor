using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Immutable geometry snapshot consumed by every build stage.  Runtime
    /// raycasts and serialized static snapshots both produce this same model;
    /// no calculation stage depends on a ScriptableObject.
    /// </summary>
    internal sealed class FlowFieldSurfaceData
    {
        private const byte ValidSurface = 1 << 0;
        private const float NormalEpsilonSqr = 0.000001f;

        private readonly FlowFieldSurfaceBakeSettings _settings;
        private readonly FlowFieldGridSpace _grid;
        private readonly float[] _surfaceHeights;
        private readonly Vector3[] _surfaceNormals;
        private readonly byte[] _surfaceFlags;
        private readonly byte[] _neighborMasks;
        private readonly float _minHeight;
        private readonly float _maxHeight;
        private readonly bool _hasHeightRange;

        internal FlowFieldGridSpace Grid => _grid;
        internal int CellCount => _grid.CellCount;
        internal int ValidCellCount { get; }
        internal int Revision { get; }
        internal Bounds BakeBoundsWorld => _settings.BakeBounds;
        internal LayerMask GroundLayer => _settings.GroundLayer;
        internal float MaxSurfaceSlope => _settings.MaxSurfaceSlope;
        internal float MaxStepHeight => _settings.MaxStepHeight;
        internal bool IsValid { get; }

        private FlowFieldSurfaceData(
            in FlowFieldSurfaceBakeSettings settings,
            float[] surfaceHeights,
            Vector3[] surfaceNormals,
            byte[] surfaceFlags,
            byte[] neighborMasks,
            int validCellCount,
            int revision,
            bool copyArrays)
        {
            if (!settings.IsValid)
                throw new ArgumentException("Surface settings are invalid.", nameof(settings));
            if (surfaceHeights == null
                || surfaceNormals == null
                || surfaceFlags == null
                || neighborMasks == null
                || surfaceHeights.Length != settings.Grid.CellCount
                || surfaceNormals.Length != settings.Grid.CellCount
                || surfaceFlags.Length != settings.Grid.CellCount
                || neighborMasks.Length != settings.Grid.CellCount)
                throw new ArgumentException("Surface arrays do not match the grid.");

            _settings = settings;
            _grid = settings.Grid;
            _surfaceHeights = copyArrays ? (float[])surfaceHeights.Clone() : surfaceHeights;
            _surfaceNormals = copyArrays ? (Vector3[])surfaceNormals.Clone() : surfaceNormals;
            _surfaceFlags = copyArrays ? (byte[])surfaceFlags.Clone() : surfaceFlags;
            _neighborMasks = copyArrays ? (byte[])neighborMasks.Clone() : neighborMasks;
            ValidCellCount = validCellCount;
            Revision = revision;
            IsValid = validCellCount > 0 && ValidateArrays(out _minHeight, out _maxHeight, out _hasHeightRange);
        }

        internal static FlowFieldSurfaceData FromRuntime(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeResult result,
            int revision)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));
            if (!result.IsValidFor(settings.Grid.CellCount))
                throw new ArgumentException("Surface bake result does not match the grid.", nameof(result));
            return new FlowFieldSurfaceData(
                settings,
                result.SurfaceHeights,
                result.SurfaceNormals,
                result.CellFlags,
                result.NeighborMasks,
                result.ValidCellCount,
                revision,
                copyArrays: false);
        }

        internal static FlowFieldSurfaceData FromSnapshot(
            in FlowFieldSurfaceBakeSettings settings,
            float[] surfaceHeights,
            Vector3[] surfaceNormals,
            byte[] surfaceFlags,
            byte[] neighborMasks,
            int validCellCount,
            int revision)
            => new FlowFieldSurfaceData(
                settings,
                surfaceHeights,
                surfaceNormals,
                surfaceFlags,
                neighborMasks,
                validCellCount,
                revision,
                copyArrays: true);

        internal bool Matches(in FlowFieldSurfaceBakeSettings settings, out string reason)
        {
            reason = string.Empty;
            if (!settings.IsValid)
            {
                reason = "Surface settings are invalid.";
                return false;
            }
            if (!FlowFieldSurfaceSignature.SurfaceMatches(_settings, settings))
            {
                reason = "Surface signature does not match the requested settings.";
                return false;
            }
            return IsValid;
        }

        internal bool ContentEquals(FlowFieldSurfaceData other)
        {
            if (other == null
                || !FlowFieldSurfaceSignature.SurfaceMatches(_settings, other._settings)
                || _surfaceHeights.Length != other._surfaceHeights.Length)
                return false;

            for (int index = 0; index < _surfaceHeights.Length; index++)
            {
                if (_surfaceFlags[index] != other._surfaceFlags[index]
                    || _neighborMasks[index] != other._neighborMasks[index]
                    || !Mathf.Approximately(_surfaceHeights[index], other._surfaceHeights[index])
                    || _surfaceNormals[index] != other._surfaceNormals[index])
                    return false;
            }
            return true;
        }

        internal bool IsSurfaceValid(int index)
            => IsValid
                && index >= 0
                && index < _surfaceFlags.Length
                && (_surfaceFlags[index] & ValidSurface) != 0;

        internal Vector3 GetCellCenter(FlowFieldGridSpace grid, int index)
        {
            if (index < 0 || index >= _surfaceHeights.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            grid.FromFlatIndex(index, out int x, out int z);
            Vector3 center = grid.LocalToWorldCenter(x, z);
            center.y = _surfaceHeights[index];
            return center;
        }

        internal Vector3 GetSurfaceNormal(int index)
        {
            if (!IsSurfaceValid(index))
                return Vector3.zero;
            Vector3 normal = _surfaceNormals[index];
            if (!FlowFieldGridSpace.IsFinite(normal) || normal.sqrMagnitude <= NormalEpsilonSqr)
                throw new ArgumentException("Surface contains an invalid normal.", nameof(index));
            return normal.normalized;
        }

        // For the Burst jobs, which pin these arrays and only read them.
        internal float[] Heights => _surfaceHeights;
        internal Vector3[] Normals => _surfaceNormals;
        internal byte[] Flags => _surfaceFlags;
        internal byte[] NeighborMasks => _neighborMasks;

        internal void CopyToArrays(
            out float[] heights,
            out Vector3[] normals,
            out byte[] flags,
            out byte[] neighborMasks)
        {
            heights = (float[])_surfaceHeights.Clone();
            normals = (Vector3[])_surfaceNormals.Clone();
            flags = (byte[])_surfaceFlags.Clone();
            neighborMasks = (byte[])_neighborMasks.Clone();
        }

        internal bool TryGetHeightRange(out float minHeight, out float maxHeight)
        {
            minHeight = _minHeight;
            maxHeight = _maxHeight;
            return _hasHeightRange;
        }

        private bool ValidateArrays(out float minHeight, out float maxHeight, out bool hasHeightRange)
        {
            minHeight = float.PositiveInfinity;
            maxHeight = float.NegativeInfinity;
            hasHeightRange = false;
            if (_surfaceHeights.Length != CellCount
                || _surfaceNormals.Length != CellCount
                || _surfaceFlags.Length != CellCount
                || _neighborMasks.Length != CellCount)
                return false;

            int validCount = 0;
            for (int index = 0; index < CellCount; index++)
            {
                if ((_surfaceFlags[index] & ~ValidSurface) != 0)
                    return false;
                if (!FlowFieldGridSpace.IsFinite(_surfaceHeights[index])
                    || !FlowFieldGridSpace.IsFinite(_surfaceNormals[index]))
                    return false;
                if (!IsSurfaceValidUnchecked(index))
                {
                    if (_neighborMasks[index] != 0)
                        return false;
                    continue;
                }

                Vector3 normal = _surfaceNormals[index];
                if (normal.sqrMagnitude <= NormalEpsilonSqr
                    || !FlowFieldGridSpace.IsFinite(normal)
                    || Vector3.Dot(normal, Vector3.up) <= 0f)
                    return false;
                validCount++;
                minHeight = Mathf.Min(minHeight, _surfaceHeights[index]);
                maxHeight = Mathf.Max(maxHeight, _surfaceHeights[index]);
            }

            if (validCount != ValidCellCount)
                return false;
            hasHeightRange = validCount > 0;
            return true;
        }

        private bool IsSurfaceValidUnchecked(int index)
            => (_surfaceFlags[index] & ValidSurface) != 0;
    }

    internal interface IFlowFieldSurfaceSource
    {
        FlowFieldSurfaceData Build(in FlowFieldSurfaceBakeSettings settings);
    }

    internal sealed class FlowFieldRaycastSurfaceSource : IFlowFieldSurfaceSource
    {
        internal static readonly FlowFieldRaycastSurfaceSource Instance = new FlowFieldRaycastSurfaceSource();
        private int _revision;

        private FlowFieldRaycastSurfaceSource()
        {
        }

        public FlowFieldSurfaceData Build(in FlowFieldSurfaceBakeSettings settings)
        {
            FlowFieldSurfaceBakeResult result = FlowFieldSurfaceBaker.Bake(settings);
            return FlowFieldSurfaceData.FromRuntime(settings, result, ++_revision);
        }
    }

    internal sealed class FlowFieldFixedSurfaceSource : IFlowFieldSurfaceSource
    {
        private readonly FlowFieldSurfaceData _surface;

        internal FlowFieldFixedSurfaceSource(FlowFieldSurfaceData surface)
        {
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        }

        public FlowFieldSurfaceData Build(in FlowFieldSurfaceBakeSettings settings)
        {
            if (!_surface.Matches(settings, out string reason))
                throw new InvalidOperationException(reason);
            return _surface;
        }
    }

}
