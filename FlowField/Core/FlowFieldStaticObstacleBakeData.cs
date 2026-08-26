using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Supercent.Common.FlowField
{
    [MovedFrom(true, "Supercent.XpHero.Actor.Enemy.FlowField", "Supercent.XpHero.FlowField.Core", "FlowFieldStaticObstacleBakeData")]
    public sealed class FlowFieldStaticObstacleBakeData : ScriptableObject
    {
        private const float SIGNATURE_EPSILON = 0.0001f;

        [SerializeField] private int _revision;
        [SerializeField] private Vector3 _gridOriginWorld;
        [SerializeField] private int _width;
        [SerializeField] private int _depth;
        [SerializeField] private float _cellSize;
        [SerializeField] private int _obstacleLayerMask;
        [SerializeField] private float _obstacleCheckHeight;
        [SerializeField] private float _obstacleCheckCenterOffset;
        [SerializeField] private float _obstacleClearance;
        [SerializeField] private bool[] _blocked = Array.Empty<bool>();

        public int Revision => _revision;
        public int CellCount => _blocked != null ? _blocked.Length : 0;
        public bool[] Blocked => _blocked;

        public bool HasValidData
        {
            get
            {
                bool hasValidCellCount = FlowFieldBakeBoundsUtility.TryValidateCellCount(
                    _width,
                    _depth,
                    out int expectedCount);
                return hasValidCellCount
                    && _blocked != null
                    && _blocked.Length == expectedCount
                    && FlowFieldGridSpace.IsFinite(_gridOriginWorld)
                    && FlowFieldGridSpace.IsFinite(_cellSize)
                    && _cellSize > 0f;
            }
        }

        public bool Matches(
            FlowFieldGridSpace grid,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            out string reason)
        {
            if (!HasValidData)
            {
                reason = "Static Obstacle Bake Asset에 유효한 데이터가 없습니다.";
                return false;
            }

            if (_width != grid.Width || _depth != grid.Depth)
            {
                reason = "Static Obstacle Grid Width/Depth가 Bake 시점과 다릅니다.";
                return false;
            }

            if (Mathf.Abs(_cellSize - grid.CellSize) > SIGNATURE_EPSILON
                || (_gridOriginWorld - grid.Origin).sqrMagnitude > SIGNATURE_EPSILON * SIGNATURE_EPSILON)
            {
                reason = "Static Obstacle Grid Origin/Cell Size가 Bake 시점과 다릅니다.";
                return false;
            }

            if (_obstacleLayerMask != obstacleLayer.value
                || Mathf.Abs(_obstacleCheckHeight - checkHeight) > SIGNATURE_EPSILON
                || Mathf.Abs(_obstacleCheckCenterOffset - centerOffset) > SIGNATURE_EPSILON
                || Mathf.Abs(_obstacleClearance - clearance) > SIGNATURE_EPSILON)
            {
                reason = "Static Obstacle Layer/Height/Clearance가 Bake 시점과 다릅니다.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public bool IsBlocked(int index)
            => HasValidData && index >= 0 && index < _blocked.Length && _blocked[index];

        public void CopyTo(bool[] destination)
        {
            if (destination == null || !HasValidData || destination.Length != _blocked.Length)
                return;

            Array.Copy(_blocked, destination, _blocked.Length);
        }

        internal void Apply(
            FlowFieldGridSpace grid,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool[] blocked)
        {
            if (!grid.IsValid || blocked == null || blocked.Length != grid.CellCount)
                throw new ArgumentException("유효하지 않은 Static Obstacle Bake 결과입니다.");

            _revision++;
            _gridOriginWorld = grid.Origin;
            _width = grid.Width;
            _depth = grid.Depth;
            _cellSize = grid.CellSize;
            _obstacleLayerMask = obstacleLayer.value;
            _obstacleCheckHeight = checkHeight;
            _obstacleCheckCenterOffset = centerOffset;
            _obstacleClearance = clearance;
            _blocked = new bool[blocked.Length];
            Array.Copy(blocked, _blocked, blocked.Length);
        }
    }
}
