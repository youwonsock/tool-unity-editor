using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What Surface2D input owes the next build: the latest request, settings
    /// held back while a build ran, the dirty flags and regions, a removed Goal
    /// and, in BakedBase, every region a change reached. The builder and its
    /// base stage read and write it where they always did: the base stages
    /// mark more work during a build, so nothing copies it when a build begins.
    /// </summary>
    internal sealed class FlowFieldSurfaceInputLedger
    {
        internal readonly FlowFieldRequestInbox<FlowFieldSurfaceRequest> Requests
            = new FlowFieldRequestInbox<FlowFieldSurfaceRequest>();
        // Configuration input that arrived while a build ran. The next build
        // submits it before it begins.
        internal FlowFieldManagerConfig OwedConfig;
        internal bool ConfigOwed;
        internal FlowFieldSurfaceDirtyFlags DirtyFlags = FlowFieldSurfaceDirtyFlags.All;
        internal FlowFieldCellRect DirtyFinalRegion = FlowFieldCellRect.Invalid;
        internal FlowFieldCellRect DirtyObstacleRegion = FlowFieldCellRect.Invalid;
        // The owner removed the runtime Goal, so a build still composing for
        // the old one must not publish. A StaticBaked field keeps its baked
        // Goal and never sets it.
        internal bool GoalRemoved;
        // BakedBase: every region an obstacle change reached since Initialize.
        // A reloaded bake scans these again, because the bake still holds
        // what was there before the changes.
        internal Bounds? BakedBaseTouched;

        internal void ExpandFinalDirty(FlowFieldCellRect rect)
            => DirtyFinalRegion = FlowFieldCellRect.Union(DirtyFinalRegion, rect);

        internal void ExpandObstacleDirty(FlowFieldCellRect rect)
            => DirtyObstacleRegion = FlowFieldCellRect.Union(DirtyObstacleRegion, rect);

        // Release forgets every request; the next build after Initialize is complete.
        internal void Reset()
        {
            Requests.Clear();
            OwedConfig = default;
            ConfigOwed = false;
            DirtyFlags = FlowFieldSurfaceDirtyFlags.All;
            DirtyFinalRegion = FlowFieldCellRect.Invalid;
            DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            GoalRemoved = false;
            BakedBaseTouched = null;
        }
    }
}
