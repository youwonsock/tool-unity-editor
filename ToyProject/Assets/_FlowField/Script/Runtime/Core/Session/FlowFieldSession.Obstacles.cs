using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Obstacle commands and observations of a Surface2D session.</summary>
    internal sealed partial class FlowFieldSession
    {
        internal bool RegisterObstacle(Collider collider, FlowFieldCellRect dirtyRegion)
        {
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return true;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            if (!_obstaclePipeline.RegisterDynamicObstacle(collider))
                return false;
            MarkDirty(
                ObstacleRegistrationFlags(collider),
                dirtyRegion,
                dirtyRegion);
            return true;
        }

        // Registering a Collider without a Rigidbody moves it between the static
        // and dynamic masks, so the static mask has to give up or take its cells.
        private static FlowFieldDirtyFlags ObstacleRegistrationFlags(Collider collider)
        {
            FlowFieldDirtyFlags flags = FlowFieldDirtyFlags.DynamicObstacles
                | FlowFieldDirtyFlags.Escape
                | FlowFieldDirtyFlags.FinalRegion;
            if (collider != null && collider.attachedRigidbody == null)
                flags |= FlowFieldDirtyFlags.StaticObstacles;
            return flags;
        }

        private protected override bool RegisterObstacleNow(Collider collider)
        {
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (collider == null)
                throw new InvalidOperationException("Cannot register a destroyed Collider.");
            if (TryGetRegisteredObstacleBounds(collider, out _))
                return false;
            FlowFieldCellRect dirty = _state.Grid.IsValid
                ? FlowFieldCellRect.FromBounds(_state.Grid, collider.bounds)
                : FlowFieldCellRect.Invalid;
            return RegisterObstacle(collider, dirty);
        }

        internal bool UnregisterObstacle(Collider collider, FlowFieldCellRect dirtyRegion)
        {
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return true;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            if (!_obstaclePipeline.UnregisterDynamicObstacle(collider))
                return false;
            MarkDirty(
                ObstacleRegistrationFlags(collider),
                dirtyRegion,
                dirtyRegion);
            return true;
        }

        private protected override bool UnregisterObstacleNow(Collider collider)
        {
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (!TryGetRegisteredObstacleBounds(collider, out Bounds bounds))
                return false;
            FlowFieldCellRect dirty = _state.Grid.IsValid
                ? FlowFieldCellRect.FromBounds(_state.Grid, bounds)
                : FlowFieldCellRect.Invalid;
            return UnregisterObstacle(collider, dirty);
        }

        internal bool TryGetRegisteredObstacleBounds(
            Collider collider,
            out Bounds bounds)
            => _obstaclePipeline.TryGetDynamicObstacleBounds(collider, out bounds);

        internal void MarkObstacleDirty(FlowFieldCellRect dirtyRegion)
        {
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            MarkDirty(
                FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape | FlowFieldDirtyFlags.FinalRegion,
                dirtyRegion,
                dirtyRegion);
        }

        internal override void NotifyObstacleRegionDirtyCommand(Bounds worldBounds)
        {
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center)
                || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                throw new ArgumentOutOfRangeException(nameof(worldBounds));
            if (!_state.Grid.IsValid)
                throw new InvalidOperationException("FlowField grid is not initialized.");
            MarkObstacleDirty(FlowFieldCellRect.FromBounds(_state.Grid, worldBounds));
        }

        internal bool DetectObstacleTransformsChanged()
        {
            if (BakeMode == FlowFieldBakeMode.StaticBaked || !_state.Grid.IsValid)
                return false;
            bool changed = _obstaclePipeline.DetectDynamicTransformsChanged(
                _state.Grid,
                out FlowFieldCellRect dirty);
            if (changed)
            {
                _obstaclePipeline.DiscardStagedDynamicProbe();
                _state.ObstacleProbePending = true;
                _state.ExpandObstacleDirty(dirty);
            }
            return changed;
        }

        internal void RecordObstacleObservation(FlowFieldCellRect dirtyRegion)
        {
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            _state.ObstacleProbePending = true;
            if (dirtyRegion.IsValid)
                _state.ExpandObstacleDirty(dirtyRegion);
        }

        internal bool ProbeObstacleChanges(in FlowFieldSessionRequest request)
        {
            if (BakeMode == FlowFieldBakeMode.StaticBaked
                || !_state.SurfaceReady
                || !_state.HasObstacleMask
                || _state.Workspace.Capacity != _state.Grid.CellCount)
                return false;
            bool changed = _obstaclePipeline.ProbeDynamicMask(
                new FlowFieldObstacleRequest(
                    _state.Grid,
                    _state.Surface,
                    _state.Workspace,
                    request.ObstacleLayer,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset,
                    request.ObstacleClearance,
                    request.UseUnregisteredObstacleSweep,
                    _state.DirtyObstacleRegion),
                out FlowFieldCellRect dirty);
            if (!changed)
            {
                _state.ObstacleProbePending = false;
                _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
                ClearNoOpObstacleRequest();
                return false;
            }
            _state.ObstacleProbePending = false;
            dirty = FlowFieldCellRect.Union(dirty, _state.DirtyObstacleRegion);
            MarkDirty(
                FlowFieldDirtyFlags.DynamicObstacles
                    | FlowFieldDirtyFlags.Escape
                    | FlowFieldDirtyFlags.FinalRegion,
                dirty,
                dirty);
            return true;
        }

        private void ClearNoOpObstacleRequest()
        {
            const FlowFieldDirtyFlags dynamicFlags = FlowFieldDirtyFlags.DynamicObstacles;
            bool staticStillDirty = (_state.DirtyFlags & FlowFieldDirtyFlags.StaticObstacles) != 0;
            FlowFieldDirtyFlags clearedFlags = dynamicFlags;
            if (!staticStillDirty)
                clearedFlags |= FlowFieldDirtyFlags.Escape;
            _state.DirtyFlags &= ~clearedFlags;

            // FinalRegion is derived for an obstacle request. Remove it only
            // when no independent Goal/default/Modifier change still owns the
            // pending final composition.
            FlowFieldDirtyFlags independent = _state.DirtyFlags
                & ~(clearedFlags | FlowFieldDirtyFlags.FinalRegion);
            if (independent == FlowFieldDirtyFlags.None)
            {
                _state.DirtyFlags &= ~FlowFieldDirtyFlags.FinalRegion;
                _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
            }
        }
    }
}
