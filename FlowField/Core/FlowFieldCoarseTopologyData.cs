using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Supercent.Common.FlowField
{
    [MovedFrom(true, "Supercent.XpHero.Actor.Enemy.FlowField", "Supercent.XpHero.FlowField.Core", "FlowFieldCoarseTopologyData")]
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
        public int CoarseCellCount => _coarseWidth * _coarseDepth;
        public float WalkableRatioThreshold => _walkableRatioThreshold;

        public bool HasValidData
            => _fineWidth > 0
                && _fineDepth > 0
                && _coarseMultiplier >= 2
                && _coarseWidth > 0
                && _coarseDepth > 0
                && _walkable != null
                && _neighborMasks != null
                && _walkable.Length == CoarseCellCount
                && _neighborMasks.Length == CoarseCellCount;

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

        public bool TryFineToCoarse(int fineX, int fineZ, out int coarseX, out int coarseZ)
        {
            coarseX = 0;
            coarseZ = 0;
            if (!HasValidData || _coarseMultiplier <= 0)
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
            int coarseWidth = Mathf.Max(1, (fineGrid.Width + coarseMultiplier - 1) / coarseMultiplier);
            int coarseDepth = Mathf.Max(1, (fineGrid.Depth + coarseMultiplier - 1) / coarseMultiplier);
            int count = coarseWidth * coarseDepth;
            if (walkable == null || neighborMasks == null
                || walkable.Length != count
                || neighborMasks.Length != count)
            {
                throw new ArgumentException("유효하지 않은 Coarse Topology Bake 결과입니다.");
            }

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
