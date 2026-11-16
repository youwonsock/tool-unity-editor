using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Obstacle commands and observations of a Surface2D session.</summary>
    internal sealed partial class FlowFieldSurfaceBuilder
    {
        internal bool RegisterObstacle(Collider collider, FlowFieldCellRect dirtyRegion)
        {
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (_session.BakeMode == FlowFieldBakeMode.StaticBaked)
                return true;
            if (!_obstaclePipeline.RegisterDynamicObstacle(collider))
                return false;
            MarkUncertain(collider.bounds);
            MarkDirty(
                ObstacleRegistrationFlags(collider),
                dirtyRegion,
                dirtyRegion);
            return true;
        }

        // Registering a Collider without a Rigidbody moves it between the static
        // and dynamic masks, so the static mask has to give up or take its cells.
        private static FlowFieldSurfaceDirtyFlags ObstacleRegistrationFlags(Collider collider)
        {
            FlowFieldSurfaceDirtyFlags flags = FlowFieldSurfaceDirtyFlags.DynamicObstacles
                | FlowFieldSurfaceDirtyFlags.Escape
                | FlowFieldSurfaceDirtyFlags.FinalRegion;
            if (collider != null && collider.attachedRigidbody == null)
                flags |= FlowFieldSurfaceDirtyFlags.StaticObstacles;
            return flags;
        }

        public bool RegisterObstacleNow(Collider collider)
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
            if (_session.BakeMode == FlowFieldBakeMode.StaticBaked)
                return true;
            if (!_obstaclePipeline.UnregisterDynamicObstacle(collider))
                return false;
            MarkDirty(
                ObstacleRegistrationFlags(collider),
                dirtyRegion,
                dirtyRegion);
            return true;
        }

        public bool UnregisterObstacleNow(Collider collider)
        {
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (!TryGetRegisteredObstacleBounds(collider, out Bounds bounds))
                return false;
            MarkUncertain(bounds);
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
            if (_session.BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            MarkDirty(
                FlowFieldSurfaceDirtyFlags.DynamicObstacles | FlowFieldSurfaceDirtyFlags.Escape | FlowFieldSurfaceDirtyFlags.FinalRegion,
                dirtyRegion,
                dirtyRegion);
        }

        public void NotifyObstacleRegionDirtyCommand(Bounds worldBounds)
        {
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center)
                || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                throw new ArgumentOutOfRangeException(nameof(worldBounds));
            if (!_state.Grid.IsValid)
                throw new InvalidOperationException("FlowField grid is not initialized.");
            MarkUncertain(worldBounds);
            MarkObstacleDirty(FlowFieldCellRect.FromBounds(_state.Grid, worldBounds));
        }

        // Cells an obstacle change can reach stop from this frame until a
        // build that queried them is published.
        private void MarkUncertain(Bounds bounds)
        {
            if (_session.BakeMode == FlowFieldBakeMode.StaticBaked || !_state.Grid.IsValid
                || !FlowFieldGridSpace.IsFinite(bounds.center) || !FlowFieldGridSpace.IsFinite(bounds.size))
                return;
            float clearance = _requests.HasLatest ? _requests.Latest.ObstacleClearance : 0f;
            _safety.MarkDirty(bounds, _state.Grid.CellSize + clearance);
        }

        public bool NotifyObstacleMovedNow(Collider collider)
        {
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (_session.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            if (!_obstaclePipeline.TryObserveMove(collider, out Bounds built, out Bounds current, out bool moved))
                return false;
            if (!moved)
                return true;
            MarkUncertain(built);
            if (collider != null)
                MarkUncertain(current);
            if (!_state.Grid.IsValid)
                return true;
            // The cells it was built in and the cells it is in now.
            FlowFieldCellRect dirty = FlowFieldCellRect.Union(
                FlowFieldCellRect.FromBounds(_state.Grid, built),
                collider != null ? FlowFieldCellRect.FromBounds(_state.Grid, current) : FlowFieldCellRect.Invalid);
            MarkObstacleDirty(dirty);
            return true;
        }
    }
}
