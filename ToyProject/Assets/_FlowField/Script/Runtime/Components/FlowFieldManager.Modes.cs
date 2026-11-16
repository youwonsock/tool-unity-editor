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
            internal virtual bool ConfigurationStale => false;
            internal abstract void Initialize();
            internal abstract void ActivateSuspended();
            internal abstract void SubmitRebuild();
            internal abstract void ObserveInputs();
            internal abstract void OnSettingsEdited();
            internal abstract void MarkGoalDirty();
            internal abstract void MarkCellsDirty();
            internal abstract void MarkConfigurationStale();
            internal abstract Bounds SnapBakeBounds(Bounds localBounds);
            internal abstract void OnBakeBoundsChanged();
        }

        private sealed class VolumeMode : ModeDriver
        {
            internal VolumeMode(FlowFieldManager owner) : base(owner)
            {
            }

            protected override FlowFieldSpaceMode Mode => FlowFieldSpaceMode.Volume3D;

            internal override void Initialize()
            {
                Session.Volume.Initialize(Owner._bakeMode);
                Owner._requiresActivationRebuild = !Owner.isActiveAndEnabled;
                if (Owner._requiresActivationRebuild)
                    Session.Suspend();
                Owner.RequestRebuild();
                Owner.Modifiers.Reattach();
            }

            internal override void ActivateSuspended()
            {
                Owner._requiresActivationRebuild = false;
                Session.Volume.Resume();
                // A Modifier enabled during the suspension could not register.
                Owner.Modifiers.Reattach();
            }

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

            internal override void OnSettingsEdited()
            {
                if (Session?.IsInitialized != true || Session.IsFaulted)
                    return;
                try
                {
                    if (!Session.IsRebuilding || Session.HasPendingRequest)
                        Owner.QueueVolumeRequest();
                }
                catch (Exception exception)
                {
                    Session.ReportFault(exception);
                }
            }


            internal override void MarkGoalDirty()
            {
                // A Goal change keeps the obstacle result unless a tracked obstacle moved.
                if (Session?.IsInitialized == true)
                    Owner.QueueVolumeRequest(force: true);
            }

            internal override void MarkCellsDirty()
                => Owner.QueueVolumeRequest(force: true, requeryObstacles: true);

            internal override void MarkConfigurationStale()
            {
                if (Session?.IsInitialized == true)
                    Owner.QueueVolumeRequest(force: true, requeryObstacles: true);
            }

            internal override Bounds SnapBakeBounds(Bounds localBounds)
                => FlowFieldBakeBoundsUtility.SnapVolumeCenterAnchored(localBounds, Owner._grid.CellSize);

            internal override void OnBakeBoundsChanged()
            {
                if (Session?.IsInitialized == true)
                    Owner.QueueVolumeRequest(force: true, requeryObstacles: true);
            }
        }

        private sealed class SurfaceMode : ModeDriver
        {
            internal SurfaceMode(FlowFieldManager owner) : base(owner)
            {
            }

            protected override FlowFieldSpaceMode Mode => FlowFieldSpaceMode.Surface2D;
            internal override bool ConfigurationStale => Session.Surface.ConfigurationStale;

            internal override void Initialize()
            {
                InitializeSession();
                Owner._requiresActivationRebuild = !Owner.isActiveAndEnabled;
                if (Owner._requiresActivationRebuild)
                    Session.Suspend();
                Owner.RequestRebuild();
            }

            internal override void ActivateSuspended()
            {
                Owner._requiresActivationRebuild = false;
                InitializeSession();
            }

            private void InitializeSession()
            {
                FlowFieldSurfaceSourceKind sourceKind = Owner.BakeMode == FlowFieldBakeMode.StaticBaked
                    ? FlowFieldSurfaceSourceKind.StaticSnapshot
                    : FlowFieldSurfaceSourceKind.SceneBuild;
                Session.Surface.Initialize(Owner._bakeMode, sourceKind);
                Owner.Modifiers.Reattach();
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
                if (!Session.Surface.ConfigurationStale && Owner.ConsumeRefreshTick())
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
                if (Session.Surface.RequiresExplicitRebuild || Session.IsRebuilding)
                    return;
                if (Session.HasPendingRequest
                    && Owner.TryCreateCurrentSurfaceRequest(out FlowFieldSurfaceRequest request))
                    Session.Surface.Submit(request);
            }

            internal override void OnSettingsEdited()
            {
                // OnValidate fires for every serialized property edit. Compare a
                // normalized request with the last submitted one so a default
                // direction/max-wave edit only rebuilds the final field, while a
                // real Surface/obstacle/Goal change invalidates the base build.
                // StaticBaked requests intentionally contain no runtime Goal or
                // dynamic-obstacle input, making those edits true no-ops.
                if (!Owner.TryCreateCurrentSurfaceRequest(out FlowFieldSurfaceRequest current)
                    || !Session.Surface.HasSubmittedRequest)
                {
                    if (!Session.Surface.ConfigurationStale)
                        Session.Surface.MarkConfigurationStale();
                    return;
                }

                // Keep the latest final-only scalar visible to a solve callback
                // even when the inspector edit lands between the solve starting and
                // the next RequestRebuild scheduling tick.
                Session.Surface.ObserveDefaultDirection(current.DefaultDirection);

                if (!Session.Surface.HasSameBaseInputs(current))
                {
                    MarkChangedBaseInputs(current);
                    return;
                }

                if (Session.Surface.HasSameFinalInputs(current))
                    return;

                FlowFieldSurfaceDirtyFlags finalFlags = FlowFieldSurfaceDirtyFlags.FinalRegion;
                try
                {
                    Vector3 normalized = FlowFieldVectorUtility.NormalizeDefaultDirection(Owner._flow.DefaultDirection);
                    if ((Session.Surface.ResolvedDefaultDirection - normalized).sqrMagnitude > FlowFieldManagerConfig.ValueEpsilon * FlowFieldManagerConfig.ValueEpsilon)
                        finalFlags |= FlowFieldSurfaceDirtyFlags.DefaultDirection;
                }
                catch
                {
                    if (!Session.Surface.ConfigurationStale)
                        Session.Surface.MarkConfigurationStale();
                    return;
                }

                if ((Session.Surface.DirtyFlags & finalFlags) != finalFlags)
                    Session.Surface.MarkDirty(finalFlags, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            }

            private void MarkChangedBaseInputs(in FlowFieldSurfaceRequest current)
            {
                FlowFieldSurfaceDirtyFlags changedFlags = Session.Surface.GetInputDirtyFlags(current);
                // Goal and obstacle scalar edits are base-field changes, but
                // they can reuse the already-baked Surface. Only a real Grid
                // or source/signature change makes the current Surface stale
                // and requires an explicit full rebuild.
                if (Owner.BakeMode == FlowFieldBakeMode.StaticBaked
                    || (changedFlags & FlowFieldSurfaceDirtyFlags.Grid) != 0)
                {
                    if (!Session.Surface.ConfigurationStale)
                        Session.Surface.MarkConfigurationStale();
                }
                else if (changedFlags != FlowFieldSurfaceDirtyFlags.None)
                {
                    Session.Surface.MarkDirty(
                        changedFlags,
                        FlowFieldCellRect.Invalid,
                        FlowFieldCellRect.Invalid);
                }
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

            internal override void MarkConfigurationStale()
            {
                if (Session?.IsInitialized != true)
                    return;
                Session.Surface.MarkConfigurationStale();
            }

            internal override Bounds SnapBakeBounds(Bounds localBounds)
                => FlowFieldBakeBoundsUtility.SnapCenterAnchored(localBounds, Owner._grid.CellSize);

            internal override void OnBakeBoundsChanged()
            {
                if (Session?.IsInitialized != true)
                    return;
                if (Owner.TryGetBakeLayout(out _, out FlowFieldGridSpace current))
                    Session.Surface.ObserveGrid(current);
                Session.Surface.MarkDirty(
                    FlowFieldSurfaceDirtyFlags.Grid,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
            }
        }
    }
}
