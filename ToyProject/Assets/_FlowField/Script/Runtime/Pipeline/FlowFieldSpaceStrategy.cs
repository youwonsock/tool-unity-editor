using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What differs between the space modes before a session exists: how the
    /// settings are checked, how they lay out a grid and which builder runs
    /// the session. One instance per mode, so callers never branch on it.
    /// </summary>
    internal abstract class FlowFieldSpaceStrategy
    {
        private static readonly FlowFieldSpaceStrategy SurfaceStrategy = new Surface();
        private static readonly FlowFieldSpaceStrategy VolumeStrategy = new Volume();

        internal static FlowFieldSpaceStrategy For(FlowFieldSpaceMode mode)
            => mode == FlowFieldSpaceMode.Volume3D ? VolumeStrategy : SurfaceStrategy;

        internal abstract FlowFieldSession CreateSession(FlowFieldExecutionBoundary boundary);

        /// <summary>
        /// False with the reason for the first invalid setting, the check Init
        /// and the Inspector share. <paramref name="bakeModeLocked"/> says a
        /// running session accepted a different Bake Mode. A Space Mode edit
        /// is never checked: it waits for Release.
        /// </summary>
        internal abstract bool TryValidate(
            in FlowFieldManagerConfig config,
            bool bakeModeLocked,
            bool validateStaticGoal,
            out string reason);

        /// <summary>Throws the reason <see cref="TryValidate"/> gives.</summary>
        internal void Validate(in FlowFieldManagerConfig config, bool bakeModeLocked, bool validateStaticGoal)
        {
            if (!TryValidate(in config, bakeModeLocked, validateStaticGoal, out string reason))
                throw new InvalidOperationException(reason);
        }

        /// <summary>The most cells the mode's grid may have.</summary>
        internal abstract int MaxCellCount { get; }

        internal abstract bool TryCreateLayout(in FlowFieldManagerConfig config, out Bounds worldBounds, out FlowFieldGridSpace grid);

        /// <summary>
        /// The box the unregistered obstacle sweep queries: every Collider that
        /// a cell's obstacle query can reach overlaps it.
        /// </summary>
        internal abstract Bounds ObstacleSweepBounds(in FlowFieldManagerConfig config);
        // Bake Bounds keep a whole number of cells around their centre.
        internal abstract Bounds SnapBounds(Bounds localBounds, float cellSize);
        // A Bounds handle resize keeps the face opposite the dragged one.
        internal abstract Bounds SnapResized(Bounds previousLocal, Bounds candidateLocal, float cellSize);

        private sealed class Surface : FlowFieldSpaceStrategy
        {
            internal override FlowFieldSession CreateSession(FlowFieldExecutionBoundary boundary)
                => FlowFieldSession.CreateSurface(boundary);

            internal override bool TryValidate(
                in FlowFieldManagerConfig config,
                bool bakeModeLocked,
                bool validateStaticGoal,
                out string reason)
                => FlowFieldSurfaceBuilder.TryValidate(in config, bakeModeLocked, validateStaticGoal, out reason);

            internal override int MaxCellCount => FlowFieldLayoutUtility.MaxSurfaceCellCount;

            internal override Bounds SnapResized(Bounds previousLocal, Bounds candidateLocal, float cellSize)
                => FlowFieldLayoutUtility.SnapResizedKeepingOppositeFace(previousLocal, candidateLocal, cellSize);

            internal override bool TryCreateLayout(in FlowFieldManagerConfig config, out Bounds worldBounds, out FlowFieldGridSpace grid)
                => FlowFieldSurfaceBuilder.TryCreateLayout(in config, out worldBounds, out grid);

            internal override Bounds SnapBounds(Bounds localBounds, float cellSize)
                => FlowFieldLayoutUtility.SnapCenterAnchored(localBounds, cellSize);

            // The grid in XZ with the Clearance, and in Y every surface height
            // the bake range allows with the obstacle check box above it.
            internal override Bounds ObstacleSweepBounds(in FlowFieldManagerConfig config)
            {
                FlowFieldSurfaceBuilder.TryCreateLayout(in config, out Bounds world, out FlowFieldGridSpace grid);
                float halfHeight = config.Surface.ObstacleCheckHeight * 0.5f;
                float offset = config.Surface.ObstacleCheckCenterOffset;
                float clearance = config.Obstacles.Clearance;
                var bounds = new Bounds();
                bounds.SetMinMax(
                    new Vector3(grid.Origin.x - clearance, world.min.y + offset - halfHeight, grid.Origin.z - clearance),
                    new Vector3(
                        grid.Origin.x + grid.WorldSizeX + clearance,
                        world.max.y + offset + halfHeight,
                        grid.Origin.z + grid.WorldSizeZ + clearance));
                return bounds;
            }
        }

        private sealed class Volume : FlowFieldSpaceStrategy
        {
            internal override FlowFieldSession CreateSession(FlowFieldExecutionBoundary boundary)
                => FlowFieldSession.CreateVolume(boundary);

            internal override bool TryValidate(
                in FlowFieldManagerConfig config,
                bool bakeModeLocked,
                bool validateStaticGoal,
                out string reason)
                => FlowFieldVolumeBuilder.TryValidate(in config, bakeModeLocked, validateStaticGoal, out reason);

            internal override int MaxCellCount => FlowFieldLayoutUtility.MaxVolumeCellCount;

            internal override Bounds SnapResized(Bounds previousLocal, Bounds candidateLocal, float cellSize)
                => FlowFieldLayoutUtility.SnapVolumeResizedKeepingOppositeFace(previousLocal, candidateLocal, cellSize);

            internal override bool TryCreateLayout(in FlowFieldManagerConfig config, out Bounds worldBounds, out FlowFieldGridSpace grid)
                => FlowFieldVolumeBuilder.TryCreateLayout(in config, out worldBounds, out grid);

            internal override Bounds SnapBounds(Bounds localBounds, float cellSize)
                => FlowFieldLayoutUtility.SnapVolumeCenterAnchored(localBounds, cellSize);

            // The Bounds with a cell and the Clearance on every side.
            internal override Bounds ObstacleSweepBounds(in FlowFieldManagerConfig config)
            {
                FlowFieldVolumeBuilder.TryCreateLayout(in config, out Bounds world, out FlowFieldGridSpace grid);
                world.Expand(2f * (grid.CellSize + config.Obstacles.Clearance));
                return world;
            }
        }
    }
}
