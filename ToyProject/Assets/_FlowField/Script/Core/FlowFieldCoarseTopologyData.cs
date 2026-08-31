using System;
using UnityEngine;

namespace Common.FlowField
{
    public sealed class FlowFieldCoarseTopologyData : ScriptableObject
    {
        private const float SIGNATURE_EPSILON = 0.0001f;
        public const float DefaultWalkableRatio = 0.25f;

        [SerializeField] private int _revision;
        [SerializeField] private Vector3 _gridOriginWorld;
        [SerializeField] private int _fineWidth;
        [SerializeField] private int _fineDepth;
        [SerializeField] private float _cellSize;
        [SerializeField] private int _coarseMultiplier = 4;
        [SerializeField] private float _walkableRatioThreshold = DefaultWalkableRatio;
        [SerializeField] private int _coarseWidth;
        [SerializeField] private int _coarseDepth;
        [SerializeField] private byte[] _walkable = Array.Empty<byte>();
        [SerializeField] private byte[] _neighborMasks = Array.Empty<byte>();

        public int Revision => _revision;
        public int CoarseMultiplier => _coarseMultiplier;
        public int CoarseWidth => _coarseWidth;
        public int CoarseDepth => _coarseDepth;
        public int CoarseCellCount
        {
            get
            {
                if (!FlowFieldBakeBoundsUtility.TryValidateCellCount(_coarseWidth, _coarseDepth, out int count))
                    return 0;
                return count;
            }
        }
        public float WalkableRatioThreshold => _walkableRatioThreshold;

        public bool HasValidData
        {
            get
            {
                if (!FlowFieldBakeBoundsUtility.TryValidateCellCount(_fineWidth, _fineDepth, out _)
                    || !FlowFieldBakeBoundsUtility.TryValidateCellCount(_coarseWidth, _coarseDepth, out int coarseCount)
                    || _coarseMultiplier < 2
                    || !FlowFieldGridSpace.IsFinite(_gridOriginWorld)
                    || !FlowFieldGridSpace.IsFinite(_cellSize)
                    || _cellSize < FlowFieldBakeBoundsUtility.MinCellSize
                    || !FlowFieldGridSpace.IsFinite(_walkableRatioThreshold)
                    || _walkableRatioThreshold < 0f
                    || _walkableRatioThreshold > 1f)
                {
                    return false;
                }

                long expectedCoarseWidth = ((long)_fineWidth + _coarseMultiplier - 1L) / _coarseMultiplier;
                long expectedCoarseDepth = ((long)_fineDepth + _coarseMultiplier - 1L) / _coarseMultiplier;
                return expectedCoarseWidth == _coarseWidth
                    && expectedCoarseDepth == _coarseDepth
                    && _walkable != null
                    && _neighborMasks != null
                    && _walkable.Length == coarseCount
                    && _neighborMasks.Length == coarseCount;
            }
        }

        public bool Matches(
            FlowFieldGridSpace fineGrid,
            int coarseMultiplier,
            float walkableRatioThreshold,
            out string reason)
        {
            if (!HasValidData)
            {
                reason = "Coarse Topology Bake Asset에 유효한 데이터가 없습니다.";
                return false;
            }

            if (_fineWidth != fineGrid.Width || _fineDepth != fineGrid.Depth)
            {
                reason = "Coarse Topology Fine Grid 크기가 Bake 시점과 다릅니다.";
                return false;
            }

            if (Mathf.Abs(_cellSize - fineGrid.CellSize) > SIGNATURE_EPSILON
                || (_gridOriginWorld - fineGrid.Origin).sqrMagnitude > SIGNATURE_EPSILON * SIGNATURE_EPSILON)
            {
                reason = "Coarse Topology Grid Origin/Cell Size가 Bake 시점과 다릅니다.";
                return false;
            }

            if (_coarseMultiplier != coarseMultiplier
                || Mathf.Abs(_walkableRatioThreshold - walkableRatioThreshold) > SIGNATURE_EPSILON)
            {
                reason = "Coarse Multiplier 또는 Walkable Ratio가 Bake 시점과 다릅니다.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public bool IsWalkable(int coarseIndex)
            => HasValidData
                && coarseIndex >= 0
                && coarseIndex < _walkable.Length
                && _walkable[coarseIndex] != 0;

        public bool HasConnection(int coarseIndex, int directionIndex)
            => HasValidData
                && coarseIndex >= 0
                && coarseIndex < _neighborMasks.Length
                && directionIndex >= 0
                && directionIndex < FlowFieldNeighborUtility.Count
                && (_neighborMasks[coarseIndex] & (1 << directionIndex)) != 0;

        public int ToFlatIndex(int coarseX, int coarseZ) => coarseZ * _coarseWidth + coarseX;

        public void FromFlatIndex(int index, out int coarseX, out int coarseZ)
        {
            coarseZ = index / _coarseWidth;
            coarseX = index - coarseZ * _coarseWidth;
        }

        /// <summary>
        /// Fine 셀 좌표를 Coarse 셀 좌표로 변환합니다. Bake 데이터가 없거나
        /// 범위를 벗어난 Fine 좌표는 정상적인 변환 실패로 false를 반환합니다.
        /// </summary>
        /// <returns>변환된 좌표가 Coarse Grid 안이면 true, 아니면 false입니다.</returns>
        public bool TryFineToCoarse(int fineX, int fineZ, out int coarseX, out int coarseZ)
        {
            coarseX = 0;
            coarseZ = 0;
            if (!HasValidData
                || _coarseMultiplier <= 0
                || fineX < 0
                || fineX >= _fineWidth
                || fineZ < 0
                || fineZ >= _fineDepth)
                return false;

            coarseX = fineX / _coarseMultiplier;
            coarseZ = fineZ / _coarseMultiplier;
            return coarseX >= 0 && coarseX < _coarseWidth && coarseZ >= 0 && coarseZ < _coarseDepth;
        }

        internal void Apply(
            FlowFieldGridSpace fineGrid,
            int coarseMultiplier,
            float walkableRatioThreshold,
            byte[] walkable,
            byte[] neighborMasks)
        {
            if (!fineGrid.IsValid)
                throw new ArgumentException("Fine Grid is invalid.", nameof(fineGrid));
            if (coarseMultiplier < 2)
                throw new ArgumentOutOfRangeException(nameof(coarseMultiplier));
            if (float.IsNaN(walkableRatioThreshold) || float.IsInfinity(walkableRatioThreshold)
                || walkableRatioThreshold < 0f || walkableRatioThreshold > 1f)
                throw new ArgumentOutOfRangeException(nameof(walkableRatioThreshold));

            long coarseWidthValue = ((long)fineGrid.Width + coarseMultiplier - 1L) / coarseMultiplier;
            long coarseDepthValue = ((long)fineGrid.Depth + coarseMultiplier - 1L) / coarseMultiplier;
            if (coarseWidthValue <= 0 || coarseWidthValue > int.MaxValue
                || coarseDepthValue <= 0 || coarseDepthValue > int.MaxValue)
                throw new ArgumentException("Fine Grid cannot produce a coarse grid.", nameof(fineGrid));
            int coarseWidth = (int)coarseWidthValue;
            int coarseDepth = (int)coarseDepthValue;
            if (!FlowFieldBakeBoundsUtility.TryValidateCellCount(coarseWidth, coarseDepth, out int count))
                throw new ArgumentException("Coarse grid exceeds the supported cell limit.", nameof(fineGrid));
            if (walkable == null)
                throw new ArgumentNullException(nameof(walkable));
            if (neighborMasks == null)
                throw new ArgumentNullException(nameof(neighborMasks));
            if (walkable.Length != count || neighborMasks.Length != count)
                throw new ArgumentException("Coarse topology result lengths must match the computed grid.");

            _revision++;
            _gridOriginWorld = fineGrid.Origin;
            _fineWidth = fineGrid.Width;
            _fineDepth = fineGrid.Depth;
            _cellSize = fineGrid.CellSize;
            _coarseMultiplier = coarseMultiplier;
            _walkableRatioThreshold = walkableRatioThreshold;
            _coarseWidth = coarseWidth;
            _coarseDepth = coarseDepth;
            _walkable = new byte[count];
            _neighborMasks = new byte[count];
            Array.Copy(walkable, _walkable, count);
            Array.Copy(neighborMasks, _neighborMasks, count);
        }
    }

}
