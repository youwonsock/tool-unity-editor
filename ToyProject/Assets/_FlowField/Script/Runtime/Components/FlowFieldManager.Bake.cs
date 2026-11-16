using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Bake inputs and the checks made before a bake.</summary>
    public partial class FlowFieldManager
    {
        internal FlowFieldSurfaceBakeSnapshot CreateStaticBakeSnapshot(
            in FlowFieldSurfaceBakeSettings settings)
        {
            if (_staticBakeData == null)
                throw new InvalidOperationException("StaticBaked mode requires a FlowFieldStaticBakeData asset.");
            return _staticBakeData.CreateSnapshot(
                settings,
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance);
        }

        internal void SetBakeBoundsLocal(Bounds localBounds)
        {
            Bounds snapped = ModeFor(_spaceMode).SnapBakeBounds(localBounds);
            if (FlowFieldBakeBoundsUtility.Approximately(_bakeBoundsLocal, snapped))
                return;
            _bakeBoundsLocal = snapped;
            ModeFor(_spaceMode).OnBakeBoundsChanged();
        }

        internal bool TryValidateSurfaceBake(out string reason)
            => TryValidateSurfaceBake(out reason, includeStaticGoal: true);

        internal bool TryValidateSurfaceBake(out string reason, bool includeStaticGoal)
        {
            if (ModeFor(_spaceMode) is VolumeMode)
                return TryValidateVolumeBake(out reason);
            reason = string.Empty;
            if (!TryGetBakeLayout(out _, out _))
            {
                reason = "Bake Bounds 또는 Cell Size가 유효하지 않습니다.";
                return false;
            }

            if (BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (_staticBakeData == null)
                {
                    reason = "StaticBaked 모드에는 FlowFieldStaticBakeData가 필요합니다.";
                    return false;
                }
                if (!_staticBakeData.MatchesSurface(CreateSurfaceBakeSettings(), out reason))
                    return false;
                if (!_staticBakeData.MatchesObstacles(
                        _obstacleLayer,
                        _obstacleCheckHeight,
                        _obstacleCheckCenterOffset,
                        _obstacleClearance,
                        out reason))
                    return false;
                if (includeStaticGoal
                    && !_staticBakeData.MatchesGoal(
                        HasConfiguredGoal,
                        ConfiguredGoalWorld,
                        _goalInfluenceRadius))
                {
                    reason = "Static Flow Bake Goal이 현재 Manager 설정과 다릅니다. ReBake가 필요합니다.";
                    return false;
                }
                return true;
            }

            return CreateSurfaceBakeSettings().IsValid;
        }

        internal bool TryValidateVolumeBake(out string reason)
        {
            reason = string.Empty;
            if (Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f
                || (transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
            {
                reason = "Volume3D는 회전 없는 unit-scale Manager Transform만 지원합니다.";
                return false;
            }
            if (_obstacleLayer.value == 0)
            {
                reason = "Volume3D Obstacle LayerMask가 비어 있습니다.";
                return false;
            }
            if (!FlowFieldGridSpace.IsFinite(_defaultFlowDirection)
                || _defaultFlowDirection.sqrMagnitude <= VALUE_EPSILON)
            {
                reason = "Volume3D Default Direction은 유한한 non-zero 벡터여야 합니다.";
                return false;
            }
            if (!FlowFieldGridSpace.IsFinite(_obstacleClearance) || _obstacleClearance < 0f
                || !FlowFieldGridSpace.IsFinite(_goalInfluenceRadius) || _goalInfluenceRadius < 0f)
            {
                reason = "Volume3D Obstacle Clearance와 Goal Influence Radius가 유효하지 않습니다.";
                return false;
            }
            if (!TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid) || !grid.IsValid)
            {
                reason = "Volume Bounds 또는 Cell Size가 유효하지 않습니다.";
                return false;
            }
            if (grid.CellCount > FlowFieldBakeBoundsUtility.MaxVolumeCellCount)
            {
                reason = "Volume3D 셀 수가 1,000,000개를 초과합니다.";
                return false;
            }
            if (_bakeMode != FlowFieldBakeMode.StaticBaked)
                return true;
            if (_volumeStaticBakeData == null)
            {
                reason = "Volume3D StaticBaked 모드에는 FlowFieldVolumeBakeData가 필요합니다.";
                return false;
            }
            if (!_volumeStaticBakeData.Matches(
                grid,
                worldBounds,
                _obstacleLayer,
                _obstacleClearance,
                out reason))
                return false;
            return _volumeStaticBakeData.MatchesGoal(
                HasConfiguredGoal,
                ConfiguredGoalWorld,
                _goalInfluenceRadius,
                out reason);
        }

        internal void AssignStaticBakeData(FlowFieldStaticBakeData bakeData)
        {
            if (_staticBakeData == bakeData)
                return;
            _staticBakeData = bakeData;
            if (_session.IsInitialized)
                _session.MarkConfigurationStale();
        }

        internal void AssignVolumeBakeData(FlowFieldVolumeBakeData bakeData)
        {
            if (_volumeStaticBakeData == bakeData)
                return;
            _volumeStaticBakeData = bakeData;
            if (_volumeSession.IsInitialized)
                QueueVolumeRequest(force: true, requeryObstacles: true);
        }

        /// <summary>A signature of every setting a bake depends on.</summary>
        internal int ComputeBakeInputHash()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (int)_spaceMode;
                hash = hash * 31 + (int)_bakeMode;
                hash = hash * 31 + _bakeBoundsLocal.center.GetHashCode();
                hash = hash * 31 + _bakeBoundsLocal.size.GetHashCode();
                hash = hash * 31 + _cellSize.GetHashCode();
                hash = hash * 31 + _groundBakeLayer.value;
                hash = hash * 31 + _maxSurfaceSlope.GetHashCode();
                hash = hash * 31 + _maxStepHeight.GetHashCode();
                hash = hash * 31 + _obstacleLayer.value;
                hash = hash * 31 + _obstacleCheckHeight.GetHashCode();
                hash = hash * 31 + _obstacleCheckCenterOffset.GetHashCode();
                hash = hash * 31 + _obstacleClearance.GetHashCode();
                hash = hash * 31 + _defaultFlowDirection.GetHashCode();
                hash = hash * 31 + (_goalTransform != null
                    ? _goalTransform.GetInstanceID()
                    : 0);
                if (_goalTransform != null)
                    hash = hash * 31 + _goalTransform.position.GetHashCode();
                hash = hash * 31 + (_hasExplicitGoal ? 1 : 0);
                hash = hash * 31 + _explicitGoalWorld.GetHashCode();
                hash = hash * 31 + _goalInfluenceRadius.GetHashCode();
                hash = hash * 31 + transform.position.GetHashCode();
                hash = hash * 31 + transform.rotation.GetHashCode();
                hash = hash * 31 + transform.lossyScale.GetHashCode();
                return hash;
            }
        }
    }
}
