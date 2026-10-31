using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Bake inputs, the requests built from them and the checks made before a bake.</summary>
    public partial class FlowFieldManager
    {
        internal int CaptureBakeInputGeneration()
        {
            int hash = ComputeBakeInputHash();
            if (!_hasBakeInputHash || hash != _lastBakeInputHash)
            {
                unchecked { _bakeInputGeneration++; }
                _lastBakeInputHash = hash;
                _hasBakeInputHash = true;
            }
            return _bakeInputGeneration;
        }

        internal FlowFieldStaticBakeSnapshot CreateStaticBakeSnapshot(
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

        private FlowFieldGridSpace CreateGridSpace()
        {
            if (!TryGetBakeLayout(out _, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Bake Bounds and Cell Size do not produce a valid grid.");
            return grid;
        }

        internal bool TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
            => FlowFieldBakeBoundsUtility.TryCreateVolumeWorldLayout(
                transform.position,
                _bakeBoundsLocal,
                _cellSize,
                out worldBounds,
                out grid);

        private FlowFieldVolumeRequest CreateVolumeRequest()
        {
            if (!TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Volume Bounds and Cell Size do not produce a valid grid.");
            return CreateVolumeRequest(
                grid,
                worldBounds,
                _bakeMode,
                _bakeMode == FlowFieldBakeMode.StaticBaked ? _volumeStaticBakeData : null);
        }

        private FlowFieldVolumeRequest CreateVolumeRequest(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData bakeData)
            => new FlowFieldVolumeRequest(
                grid,
                worldBounds,
                bakeMode,
                _obstacleLayer,
                _obstacleClearance,
                HasConfiguredGoal,
                ConfiguredGoalWorld,
                _goalInfluenceRadius,
                _defaultFlowDirection,
                _maxGpuWaves,
                _frontierComputeShader,
                bakeData,
                CaptureBakeInputGeneration());

        private void QueueVolumeRequest(bool force = false, bool requeryObstacles = false)
        {
            if (!_volumeSession.IsInitialized || _volumeSession.IsFaulted)
                return;
            FlowFieldVolumeRequest request = CreateVolumeRequest();
            if (!force
                && _volumeSession.TryGetLatestRequestedInput(out FlowFieldVolumeRequest latest)
                && latest.HasSameInputs(request))
                return;
            // The session owns the accepted input, including a newer input
            // submitted from its synchronous state-change callback.
            _volumeSession.Submit(request, requeryObstacles);
        }

        /// <summary>
        /// Creates an isolated managed Volume3D bake session. The caller owns
        /// and must dispose the returned session; it may pump it from either
        /// the Editor update loop or a test-controlled clock.
        /// </summary>
        internal FlowFieldVolumeSession CreateVolumeBakeSessionForEditor(
            out FlowFieldVolumeRequest request)
        {
            request = default;
            if (_spaceMode != FlowFieldSpaceMode.Volume3D)
                throw new InvalidOperationException("Volume bake requires Volume3D mode.");
            if (!TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Volume Bounds and Cell Size do not produce a valid grid.");

            request = CreateVolumeRequest(grid, worldBounds, FlowFieldBakeMode.RuntimeDynamic, null);

            FlowFieldVolumeSession session = new FlowFieldVolumeSession();
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, null);
                session.UseManagedBackendForBake();
                session.Submit(request);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        internal FlowFieldSurfaceBakeSettings CreateSurfaceBakeSettings()
        {
            if (!TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Bake Bounds and Cell Size do not produce a valid grid.");
            return new FlowFieldSurfaceBakeSettings(
                grid,
                worldBounds,
                _groundBakeLayer,
                _maxSurfaceSlope,
                _maxStepHeight);
        }

        internal bool TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
            => FlowFieldBakeBoundsUtility.TryCreateWorldLayout(
                transform.position,
                _bakeBoundsLocal,
                _cellSize,
                out worldBounds,
                out grid);

        private FlowFieldSessionRequest CreateSurfaceRequest(
            FlowFieldSurfaceBakeSettings settings,
            FlowFieldDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
        {
            int maxWaves = FlowFieldComputeSolver.ClampMaxWaves(settings.Grid.CellCount, _maxGpuWaves);
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                return FlowFieldSessionRequest.ForStaticSnapshot(
                    settings,
                    _staticBakeData,
                    CreateStaticBakeSnapshot(settings),
                    _obstacleLayer,
                    _obstacleCheckHeight,
                    _obstacleCheckCenterOffset,
                    _obstacleClearance,
                    _defaultFlowDirection,
                    dirtyFlags,
                    maxWaves);
            }

            return FlowFieldSessionRequest.ForSceneBuild(
                settings,
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance,
                _enableUnregisteredObstacleSweep,
                ResolveConfiguredGoal(settings.Grid),
                _defaultFlowDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion,
                maxWaves);
        }

        private bool TryCreateCurrentSessionRequest(out FlowFieldSessionRequest request)
        {
            request = default;
            try
            {
                FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
                request = CreateSurfaceRequest(
                    settings,
                    FlowFieldDirtyFlags.None,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal void SetBakeBoundsLocal(Bounds localBounds)
        {
            Bounds snapped = ModeFor(_spaceMode).SnapBakeBounds(localBounds);
            if (FlowFieldBakeBoundsUtility.Approximately(_bakeBoundsLocal, snapped))
                return;
            _bakeBoundsLocal = snapped;
            ModeFor(_spaceMode).OnBakeBoundsChanged();
            RefreshEditorPreview();
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
            RefreshEditorPreview();
        }

        internal void AssignVolumeBakeData(FlowFieldVolumeBakeData bakeData)
        {
            if (_volumeStaticBakeData == bakeData)
                return;
            _volumeStaticBakeData = bakeData;
            if (_volumeSession.IsInitialized)
                QueueVolumeRequest(force: true, requeryObstacles: true);
            RefreshEditorPreview();
        }

        private int ComputeBakeInputHash()
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
                hash = hash * 31 + _maxGpuWaves;
                hash = hash * 31 + (_frontierComputeShader != null
                    ? _frontierComputeShader.GetInstanceID()
                    : 0);
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
