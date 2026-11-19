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
        /// running session accepted a different Bake Mode.
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

        internal abstract bool TryCreateLayout(in FlowFieldManagerConfig config, out Bounds worldBounds, out FlowFieldGridSpace grid);
        // Bake Bounds keep a whole number of cells around their centre.
        internal abstract Bounds SnapBounds(Bounds localBounds, float cellSize);

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

            internal override bool TryCreateLayout(in FlowFieldManagerConfig config, out Bounds worldBounds, out FlowFieldGridSpace grid)
                => FlowFieldSurfaceBuilder.TryCreateLayout(in config, out worldBounds, out grid);

            internal override Bounds SnapBounds(Bounds localBounds, float cellSize)
                => FlowFieldBakeBoundsUtility.SnapCenterAnchored(localBounds, cellSize);
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

            internal override bool TryCreateLayout(in FlowFieldManagerConfig config, out Bounds worldBounds, out FlowFieldGridSpace grid)
                => FlowFieldVolumeBuilder.TryCreateLayout(in config, out worldBounds, out grid);

            internal override Bounds SnapBounds(Bounds localBounds, float cellSize)
                => FlowFieldBakeBoundsUtility.SnapVolumeCenterAnchored(localBounds, cellSize);
        }
    }
}
