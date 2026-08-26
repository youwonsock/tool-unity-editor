using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Supercent.Common.FlowField
{
    [MovedFrom(true, "Supercent.XpHero.Actor.Enemy.FlowField", "Supercent.XpHero.FlowField.Core", "FlowFieldSurfaceBakeData")]
    public sealed class FlowFieldSurfaceBakeData : ScriptableObject
    {
        internal const int CURRENT_FORMAT_VERSION = 2;
        private const byte VALID_SURFACE = 1 << 0;
        private const float SIGNATURE_EPSILON = 0.0001f;

        [SerializeField] private int _formatVersion;
        [SerializeField] private int _revision;
        [SerializeField] private Vector3 _gridOriginWorld;
        [SerializeField] private int _width;
        [SerializeField] private int _depth;
        [SerializeField] private float _cellSize;
        [SerializeField] private int _groundLayerMask;
        [SerializeField] private Vector3 _bakeBoundsCenterWorld;
        [SerializeField] private Vector3 _bakeBoundsSizeWorld;
        [SerializeField] private float _maxSurfaceSlope;
        [SerializeField] private float _maxStepHeight;
        [SerializeField] private int _validCellCount;
        [SerializeField] private float _minSurfaceHeight;
        [SerializeField] private float _maxSurfaceHeight;
        [SerializeField] private bool _hasHeightRange;
        [SerializeField] private float[] _surfaceHeights = Array.Empty<float>();
        [SerializeField] private Vector3[] _surfaceNormals = Array.Empty<Vector3>();
        [SerializeField] private byte[] _cellFlags = Array.Empty<byte>();
        [SerializeField] private byte[] _neighborMasks = Array.Empty<byte>();

        public int Width => _width;
        public int Depth => _depth;
        public float CellSize => _cellSize;
        public int CellCount => HasValidData ? _cellFlags.Length : 0;
        public int ValidCellCount => HasValidData ? _validCellCount : 0;
        public int Revision => _revision;
        public Vector3 GridOriginWorld => _gridOriginWorld;
        public Bounds BakeBoundsWorld => new Bounds(_bakeBoundsCenterWorld, _bakeBoundsSizeWorld);

        public bool HasValidData
        {
            get
            {
                bool hasValidCellCount = FlowFieldBakeBoundsUtility.TryValidateCellCount(
                    _width,
                    _depth,
                    out int expectedCount);
                return _formatVersion == CURRENT_FORMAT_VERSION
                    && hasValidCellCount
                    && FlowFieldGridSpace.IsFinite(_gridOriginWorld)
                    && FlowFieldGridSpace.IsFinite(_cellSize)
                    && _cellSize > 0f
                    && FlowFieldGridSpace.IsFinite(_bakeBoundsCenterWorld)
                    && FlowFieldGridSpace.IsFinite(_bakeBoundsSizeWorld)
                    && _bakeBoundsSizeWorld.x > 0f
                    && _bakeBoundsSizeWorld.y >= FlowFieldBakeBoundsUtility.MinBoundsHeight
                    && _bakeBoundsSizeWorld.z > 0f
                    && _surfaceHeights != null
                    && _surfaceHeights.Length == expectedCount
                    && _surfaceNormals != null
                    && _surfaceNormals.Length == expectedCount
                    && _cellFlags != null
                    && _cellFlags.Length == expectedCount
                    && _neighborMasks != null
                    && _neighborMasks.Length == expectedCount
                    && _validCellCount > 0
                    && _validCellCount <= expectedCount;
            }
        }

        internal bool Matches(in FlowFieldSurfaceBakeSettings settings, out string mismatchReason)
        {
            if (_formatVersion != CURRENT_FORMAT_VERSION)
            {
                mismatchReason = $"Bake Asset 포맷이 현재 버전과 다릅니다. ReBake가 필요합니다. "
                    + $"(Asset {_formatVersion}, Current {CURRENT_FORMAT_VERSION})";
                return false;
            }

            if (!HasValidData)
            {
                mismatchReason = "Bake Asset에 유효한 표면 데이터가 없습니다.";
                return false;
            }

            FlowFieldGridSpace grid = settings.Grid;
            if (_width != grid.Width || _depth != grid.Depth)
            {
                mismatchReason = "Grid Width/Depth가 Bake 시점과 다릅니다.";
                return false;
            }

            if (Mathf.Abs(_cellSize - grid.CellSize) > SIGNATURE_EPSILON)
            {
                mismatchReason = "Cell Size가 Bake 시점과 다릅니다.";
                return false;
            }

            if ((_gridOriginWorld - grid.Origin).sqrMagnitude > SIGNATURE_EPSILON * SIGNATURE_EPSILON)
            {
                mismatchReason = "Manager 위치 또는 Grid Origin이 Bake 시점과 다릅니다.";
                return false;
            }

            if (_groundLayerMask != settings.GroundLayer.value)
            {
                mismatchReason = "Ground Bake Layer가 Bake 시점과 다릅니다.";
                return false;
            }

            Bounds bakeBounds = settings.BakeBounds;
            if ((_bakeBoundsCenterWorld - bakeBounds.center).sqrMagnitude
                    > SIGNATURE_EPSILON * SIGNATURE_EPSILON
                || (_bakeBoundsSizeWorld - bakeBounds.size).sqrMagnitude
                    > SIGNATURE_EPSILON * SIGNATURE_EPSILON)
            {
                mismatchReason = "Bake Bounds Center 또는 Size가 Bake 시점과 다릅니다.";
                return false;
            }

            if (!Approximately(_maxSurfaceSlope, settings.MaxSurfaceSlope)
                || !Approximately(_maxStepHeight, settings.MaxStepHeight))
            {
                mismatchReason = "경사 또는 단차 설정이 Bake 시점과 다릅니다.";
                return false;
            }

            mismatchReason = string.Empty;
            return true;
        }

        internal bool IsSurfaceValid(int index)
            => HasValidData && index >= 0 && index < _cellFlags.Length && (_cellFlags[index] & VALID_SURFACE) != 0;

        internal Vector3 GetCellCenter(FlowFieldGridSpace grid, int index)
        {
            grid.FromFlatIndex(index, out int x, out int z);
            Vector3 center = grid.LocalToWorldCenter(x, z);
            center.y = _surfaceHeights[index];
            return center;
        }

        internal Vector3 GetSurfaceNormal(int index)
        {
            if (!IsSurfaceValid(index))
                return Vector3.up;

            Vector3 normal = _surfaceNormals[index];
            return normal.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                ? normal.normalized
                : Vector3.up;
        }

        internal byte GetNeighborMask(int index)
            => IsSurfaceValid(index) ? _neighborMasks[index] : (byte)0;

        internal bool HasConnection(int index, int directionIndex)
            => directionIndex >= 0
                && directionIndex < FlowFieldNeighborUtility.Count
                && (GetNeighborMask(index) & (1 << directionIndex)) != 0;

        internal bool TryGetHeightRange(out float minHeight, out float maxHeight)
        {
            minHeight = float.PositiveInfinity;
            maxHeight = float.NegativeInfinity;
            if (!HasValidData)
                return false;

            if (_hasHeightRange
                && FlowFieldGridSpace.IsFinite(_minSurfaceHeight)
                && FlowFieldGridSpace.IsFinite(_maxSurfaceHeight)
                && _minSurfaceHeight <= _maxSurfaceHeight)
            {
                minHeight = _minSurfaceHeight;
                maxHeight = _maxSurfaceHeight;
                return true;
            }

            for (int index = 0; index < _cellFlags.Length; index++)
            {
                if (!IsSurfaceValid(index))
                    continue;

                float height = _surfaceHeights[index];
                minHeight = Mathf.Min(minHeight, height);
                maxHeight = Mathf.Max(maxHeight, height);
            }

            return !float.IsInfinity(minHeight) && !float.IsInfinity(maxHeight);
        }

        internal void Apply(in FlowFieldSurfaceBakeSettings settings, FlowFieldSurfaceBakeResult result)
        {
            if (result == null || !result.IsValidFor(settings.Grid.CellCount))
                throw new ArgumentException("유효하지 않은 FlowField Surface Bake 결과입니다.", nameof(result));

            _formatVersion = CURRENT_FORMAT_VERSION;
            _revision++;
            _gridOriginWorld = settings.Grid.Origin;
            _width = settings.Grid.Width;
            _depth = settings.Grid.Depth;
            _cellSize = settings.Grid.CellSize;
            _groundLayerMask = settings.GroundLayer.value;
            _bakeBoundsCenterWorld = settings.BakeBounds.center;
            _bakeBoundsSizeWorld = settings.BakeBounds.size;
            _maxSurfaceSlope = settings.MaxSurfaceSlope;
            _maxStepHeight = settings.MaxStepHeight;
            _validCellCount = result.ValidCellCount;
            _surfaceHeights = Clone(result.SurfaceHeights);
            _surfaceNormals = Clone(result.SurfaceNormals);
            _cellFlags = Clone(result.CellFlags);
            _neighborMasks = Clone(result.NeighborMasks);
            CacheHeightRange();
        }

        private void CacheHeightRange()
        {
            _hasHeightRange = false;
            _minSurfaceHeight = 0f;
            _maxSurfaceHeight = 0f;
            float minHeight = float.PositiveInfinity;
            float maxHeight = float.NegativeInfinity;
            if (_cellFlags == null || _surfaceHeights == null)
                return;

            for (int index = 0; index < _cellFlags.Length; index++)
            {
                if ((_cellFlags[index] & VALID_SURFACE) == 0)
                    continue;

                float height = _surfaceHeights[index];
                minHeight = Mathf.Min(minHeight, height);
                maxHeight = Mathf.Max(maxHeight, height);
            }

            if (float.IsInfinity(minHeight) || float.IsInfinity(maxHeight))
                return;

            _minSurfaceHeight = minHeight;
            _maxSurfaceHeight = maxHeight;
            _hasHeightRange = true;
        }

        private static bool Approximately(float left, float right)
            => Mathf.Abs(left - right) <= SIGNATURE_EPSILON;

        private static T[] Clone<T>(T[] source)
        {
            var clone = new T[source.Length];
            Array.Copy(source, clone, source.Length);
            return clone;
        }
    }
}
