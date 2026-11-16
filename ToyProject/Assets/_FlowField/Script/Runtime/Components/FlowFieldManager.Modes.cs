using System;
using UnityEngine;

namespace Common.FlowField
{
    public partial class FlowFieldManager
    {
        private ModeDriver _surfaceMode;
        private ModeDriver _volumeMode;

        private ModeDriver ModeFor(FlowFieldSpaceMode mode)
            => mode == FlowFieldSpaceMode.Volume3D
                ? _volumeMode ??= new VolumeMode(this)
                : _surfaceMode ??= new SurfaceMode(this);

        /// <summary>
        /// What the façade does differently in each space mode, so its Unity
        /// callbacks and public commands need no mode branches of their own.
        /// </summary>
        private abstract class ModeDriver
        {
            protected readonly FlowFieldManager Owner;

            protected ModeDriver(FlowFieldManager owner)
            {
                Owner = owner;
            }

            protected abstract FlowFieldSpaceMode Mode { get; }

            // The Manager's session while it runs in this mode, otherwise
            // null: an Inspector edit may target the mode not running.
            protected FlowFieldSession Session => Owner.SessionFor(Mode);
            internal abstract void Initialize();
            internal abstract void SubmitRebuild();
            internal abstract void ObserveInputs();
            internal abstract void OnSettingsEdited();
            internal abstract void MarkGoalDirty();
            internal abstract void MarkCellsDirty();
            internal abstract Bounds SnapBakeBounds(Bounds localBounds);
        }

        private sealed class VolumeMode : ModeDriver
        {
            internal VolumeMode(FlowFieldManager owner) : base(owner)
            {
            }

            protected override FlowFieldSpaceMode Mode => FlowFieldSpaceMode.Volume3D;

            internal override void Initialize()
                => Session.Volume.Initialize(Owner._bakeMode);

            internal override void SubmitRebuild()
            {
                Owner.QueueVolumeRequest(force: true, requeryObstacles: true);
                Session.AttachRuntimeScheduler();
            }

            internal override void ObserveInputs()
            {
                if (Owner._enableUnregisteredObstacleSweep && Owner.ConsumeRefreshTick())
                    Session.Volume.DetectUnregisteredObstacleChanges(true);
                Session.Volume.DetectModifierChanges();
                Owner.QueueVolumeRequest();
            }

            // A changed request rebuilds; a new grid hides the old result at once.
            internal override void OnSettingsEdited()
                => Owner.QueueVolumeRequest();

            internal override void MarkGoalDirty()
            {
                // A Goal change keeps the obstacle result unless a tracked obstacle moved.
                if (Session?.IsInitialized == true)
                    Owner.QueueVolumeRequest(force: true);
            }

            internal override void MarkCellsDirty()
                => Owner.QueueVolumeRequest(force: true, requeryObstacles: true);

            internal override Bounds SnapBakeBounds(Bounds localBounds)
                => FlowFieldBakeBoundsUtility.SnapVolumeCenterAnchored(localBounds, Owner._grid.CellSize);
        }

        private sealed class SurfaceMode : ModeDriver
        {
            internal SurfaceMode(FlowFieldManager owner) : base(owner)
            {
            }

            protected override FlowFieldSpaceMode Mode => FlowFieldSpaceMode.Surface2D;

            internal override void Initialize()
            {
                FlowFieldSurfaceSourceKind sourceKind = Owner.BakeMode == FlowFieldBakeMode.StaticBaked
                    ? FlowFieldSurfaceSourceKind.StaticSnapshot
                    : FlowFieldSurfaceSourceKind.SceneBuild;
                Session.Surface.Initialize(Owner._bakeMode, sourceKind);
            }

            internal override void SubmitRebuild()
            {
                FlowFieldSurfaceBakeSettings settings = Owner.CreateSurfaceBakeSettings();
                Session.Surface.Submit(Owner.CreateSurfaceRequest(
                    settings,
                    FlowFieldSurfaceDirtyFlags.None,
                    Session.Surface.DirtyFinalRegion,
                    Session.Surface.DirtyObstacleRegion));
                Session.AttachRuntimeScheduler();
            }

            internal override void ObserveInputs()
            {
                if (Owner.ConsumeRefreshTick())
                    ObserveDueInputs();
                SubmitPendingRebuild();
            }

            private void ObserveDueInputs()
            {
                if (Owner.BakeMode == FlowFieldBakeMode.RuntimeDynamic)
                {
                    DetectGridTransformChange();
                    DetectGoalChange();
                    bool obstacleObserved = Session.Surface.DetectObstacleTransformsChanged();
                    bool probePending = Session.Surface.HasPendingObstacleProbe;
                    if (!Session.IsRebuilding
                        && (Owner._enableUnregisteredObstacleSweep || obstacleObserved || probePending))
                    {
                        if (Owner.TryCreateCurrentSurfaceRequest(out FlowFieldSurfaceRequest probeRequest))
                        {
                            Session.Surface.ProbeObstacleChanges(probeRequest);
                        }
                    }
                    else if (Session.IsRebuilding && obstacleObserved)
                    {
                        // Transform snapshots are updated immediately, but the
                        // physics probe waits until the in-flight BFS callback
                        // has returned. This keeps at most one probe and one BFS
                        // active while preserving the previous/next dirty bounds.
                        Session.Surface.RecordObstacleObservation(Session.Surface.DirtyObstacleRegion);
                    }
                }

                Session.Surface.DetectModifierChanges();
            }

            private void DetectGridTransformChange()
            {
                if (!Session.Surface.StagingGrid.IsValid)
                    return;
                FlowFieldGridSpace current = Owner.CreateGridSpace();
                if (Session.Surface.ObserveGrid(current))
                    Session.Surface.MarkDirty(FlowFieldSurfaceDirtyFlags.Grid, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            }

            private void DetectGoalChange()
            {
                if (!Session.Surface.StagingSurfaceReady || !Session.Surface.StagingGrid.IsValid)
                    return;
                if ((Session.Surface.DirtyFlags & FlowFieldSurfaceDirtyFlags.Goal) != 0)
                    return;
                FlowFieldSurfaceGoalChangeStatus status = Session.Surface.GoalTracker.DetectChange(
                    Session.Surface.StagingGrid,
                    Session.Surface.StagingSurfaceReady,
                    Owner._goalTransform,
                    Owner._hasExplicitGoal,
                    Owner._explicitGoalWorld,
                    Owner._flow.GoalInfluenceRadius);
                if (status == FlowFieldSurfaceGoalChangeStatus.Changed)
                {
                    Session.Surface.MarkDirty(
                        FlowFieldSurfaceDirtyFlags.Goal,
                        FlowFieldCellRect.Invalid,
                        FlowFieldCellRect.Invalid);
                    if (!Owner.HasConfiguredGoal)
                        Session.Surface.ObserveGoalRemoved();
                }
            }

            // The scheduler builds it later in this frame.
            private void SubmitPendingRebuild()
            {
                if (!Owner.isActiveAndEnabled || Session?.IsInitialized != true || Session.IsFaulted)
                    return;
                if (Session.IsRebuilding)
                    return;
                if (Session.HasPendingRequest
                    && Owner.TryCreateCurrentSurfaceRequest(out FlowFieldSurfaceRequest request))
                    Session.Surface.Submit(request);
            }

            internal override void OnSettingsEdited()
            {
                // The settings are valid here. Compare the request with the
                // last submitted one so a Default Direction edit rebuilds only
                // the final field and a Goal or obstacle edit reuses the
                // Surface. StaticBaked requests hold no runtime Goal or dynamic
                // obstacle input, so those edits change nothing.
                FlowFieldSurfaceRequest current = Owner.CreateSurfaceRequest(
                    Owner.CreateSurfaceBakeSettings(),
                    FlowFieldSurfaceDirtyFlags.None,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
                FlowFieldSurfaceBuilder surface = Session.Surface;
                if (surface.HasSubmittedRequest
                    && surface.HasSameBaseInputs(current)
                    && surface.HasSameFinalInputs(current))
                    return;

                // Visible to a solve callback even before the next Submit.
                surface.ObserveDefaultDirection(current.DefaultDirection);
                FlowFieldSurfaceDirtyFlags flags = surface.GetInputDirtyFlags(current);
                bool gridChanged = (flags & FlowFieldSurfaceDirtyFlags.Grid) != 0;
                if (gridChanged && Owner.TryGetBakeLayout(out _, out FlowFieldGridSpace grid))
                    surface.ObserveGrid(grid);
                Vector3 normalized = FlowFieldVectorUtility.NormalizeDefaultDirection(Owner._flow.DefaultDirection);
                if ((surface.ResolvedDefaultDirection - normalized).sqrMagnitude
                    > FlowFieldManagerConfig.ValueEpsilon * FlowFieldManagerConfig.ValueEpsilon)
                    flags |= FlowFieldSurfaceDirtyFlags.DefaultDirection | FlowFieldSurfaceDirtyFlags.FinalRegion;
                surface.MarkDirty(flags, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);

                // A new grid hides the published field at once. Other edits
                // wait for a running build, as observed changes do.
                if (gridChanged || !Session.IsRebuilding)
                    surface.Submit(current);
            }

            internal override void MarkGoalDirty()
            {
                Session.Surface.GoalTracker.ResetWarning();
                Session.Surface.MarkDirty(
                    FlowFieldSurfaceDirtyFlags.Goal,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
                if (!Owner.HasConfiguredGoal)
                    Session.Surface.ObserveGoalRemoved();
            }

            internal override void MarkCellsDirty()
            {
                Session.Surface.MarkDirty(
                    FlowFieldSurfaceDirtyFlags.Grid,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
            }

            internal override Bounds SnapBakeBounds(Bounds localBounds)
                => FlowFieldBakeBoundsUtility.SnapCenterAnchored(localBounds, Owner._grid.CellSize);
        }
    }
}
