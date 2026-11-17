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

        internal abstract FlowFieldSpaceMode Mode { get; }
        internal abstract FlowFieldSession CreateSession(FlowFieldExecutionBoundary boundary);
        internal abstract void Validate(in FlowFieldManagerConfig config, bool bakeModeLocked, bool validateStaticGoal);
        internal abstract bool TryCreateLayout(in FlowFieldManagerConfig config, out Bounds worldBounds, out FlowFieldGridSpace grid);
        // Bake Bounds keep a whole number of cells around their centre.
        internal abstract Bounds SnapBounds(Bounds localBounds, float cellSize);

        private sealed class Surface : FlowFieldSpaceStrategy
        {
            internal override FlowFieldSpaceMode Mode => FlowFieldSpaceMode.Surface2D;

            internal override FlowFieldSession CreateSession(FlowFieldExecutionBoundary boundary)
                => FlowFieldSession.CreateSurface(boundary);

            internal override void Validate(in FlowFieldManagerConfig config, bool bakeModeLocked, bool validateStaticGoal)
                => FlowFieldSurfaceBuilder.Validate(in config, bakeModeLocked, validateStaticGoal);

            internal override bool TryCreateLayout(in FlowFieldManagerConfig config, out Bounds worldBounds, out FlowFieldGridSpace grid)
                => FlowFieldSurfaceBuilder.TryCreateLayout(in config, out worldBounds, out grid);

            internal override Bounds SnapBounds(Bounds localBounds, float cellSize)
                => FlowFieldBakeBoundsUtility.SnapCenterAnchored(localBounds, cellSize);
        }

        private sealed class Volume : FlowFieldSpaceStrategy
        {
            internal override FlowFieldSpaceMode Mode => FlowFieldSpaceMode.Volume3D;

            internal override FlowFieldSession CreateSession(FlowFieldExecutionBoundary boundary)
                => FlowFieldSession.CreateVolume(boundary);

            internal override void Validate(in FlowFieldManagerConfig config, bool bakeModeLocked, bool validateStaticGoal)
                => FlowFieldVolumeBuilder.Validate(in config, bakeModeLocked, validateStaticGoal);

            internal override bool TryCreateLayout(in FlowFieldManagerConfig config, out Bounds worldBounds, out FlowFieldGridSpace grid)
                => FlowFieldVolumeBuilder.TryCreateLayout(in config, out worldBounds, out grid);

            internal override Bounds SnapBounds(Bounds localBounds, float cellSize)
                => FlowFieldBakeBoundsUtility.SnapVolumeCenterAnchored(localBounds, cellSize);
        }
    }
}
