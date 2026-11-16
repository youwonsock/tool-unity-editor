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

            internal abstract FlowFieldSession Session { get; }
            internal virtual bool ConfigurationStale => false;
            internal abstract void Initialize();
            internal abstract void ActivateSuspended();
            internal abstract void SubmitRebuild();
            internal abstract void ObserveInputs();
            internal abstract void OnSettingsEdited();
            internal abstract void ValidateConfiguration(bool validateStaticGoal);
            internal abstract void MarkGoalDirty();
            internal abstract void MarkCellsDirty();
            internal abstract void MarkConfigurationStale();
            internal abstract Bounds SnapBakeBounds(Bounds localBounds);
            internal abstract void OnBakeBoundsChanged();
        }

        private sealed class VolumeMode : ModeDriver
        {
            private readonly FlowFieldSession _session;
            private FlowFieldVolumeBuilder Volume => _session.Volume;

            internal VolumeMode(FlowFieldManager owner) : base(owner)
            {
                _session = owner._volumeSession;
            }

            internal override FlowFieldSession Session => _session;

            internal override void Initialize()
            {
                Owner._attachedSession = _session;
                _session.Volume.Initialize(Owner._bakeMode);
                Owner._requiresActivationRebuild = !Owner.isActiveAndEnabled;
                if (Owner._requiresActivationRebuild)
                    _session.Suspend();
                Owner.RequestRebuild();
                Owner.ReattachActiveModifiers();
            }

            internal override void ActivateSuspended()
            {
                Owner._requiresActivationRebuild = false;
                _session.Volume.Resume();
                // A Modifier enabled during the suspension could not register.
                Owner.ReattachActiveModifiers();
            }

            internal override void SubmitRebuild()
            {
                Owner.QueueVolumeRequest(force: true, requeryObstacles: true);
                _session.AttachRuntimeScheduler();
            }

            internal override void ObserveInputs()
            {
                if (Owner._enableUnregisteredObstacleSweep && Owner.ConsumeRefreshTick())
                    _session.Volume.DetectUnregisteredObstacleChanges(true);
                _session.Volume.DetectModifierChanges();
                Owner.QueueVolumeRequest();
            }

            internal override void OnSettingsEdited()
            {
                if (!_session.IsInitialized || _session.IsFaulted)
                    return;
                try
                {
                    if (!_session.IsRebuilding || _session.HasPendingRequest)
                        Owner.QueueVolumeRequest();
                }
                catch (Exception exception)
                {
                    _session.ReportFault(exception);
                }
            }


            internal override void ValidateConfiguration(bool validateStaticGoal)
                => Owner.ValidateVolumeConfiguration(validateStaticGoal);

            internal override void MarkGoalDirty()
            {
                // A Goal change keeps the obstacle result unless a tracked obstacle moved.
                if (_session.IsInitialized)
                    Owner.QueueVolumeRequest(force: true);
            }

            internal override void MarkCellsDirty()
                => Owner.QueueVolumeRequest(force: true, requeryObstacles: true);

            internal override void MarkConfigurationStale()
            {
                if (_session.IsInitialized)
                    Owner.QueueVolumeRequest(force: true, requeryObstacles: true);
            }

            internal override Bounds SnapBakeBounds(Bounds localBounds)
                => FlowFieldBakeBoundsUtility.SnapVolumeCenterAnchored(localBounds, Owner._cellSize);

            internal override void OnBakeBoundsChanged()
            {
                if (_session.IsInitialized)
                    Owner.QueueVolumeRequest(force: true, requeryObstacles: true);
            }
        }

        private sealed class SurfaceMode : ModeDriver
        {
            private readonly FlowFieldSession _session;
            private FlowFieldSurfaceBuilder Surface => _session.Surface;

            internal SurfaceMode(FlowFieldManager owner) : base(owner)
            {
                _session = owner._session;
            }

            internal override FlowFieldSession Session => _session;
            internal override bool ConfigurationStale => _session.Surface.ConfigurationStale;

            internal override void Initialize()
            {
                InitializeSession();
                Owner._requiresActivationRebuild = !Owner.isActiveAndEnabled;
                if (Owner._requiresActivationRebuild)
                    _session.Suspend();
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
                Owner._attachedSession = _session;
                _session.Surface.Initialize(Owner._bakeMode, sourceKind);
                Owner.ReattachActiveModifiers();
            }

            internal override void SubmitRebuild()
            {
                FlowFieldSurfaceBakeSettings settings = Owner.CreateSurfaceBakeSettings();
                _session.Surface.Submit(Owner.CreateSurfaceRequest(
                    settings,
                    FlowFieldSurfaceDirtyFlags.None,
                    _session.Surface.DirtyFinalRegion,
                    _session.Surface.DirtyObstacleRegion));
                _session.AttachRuntimeScheduler();
            }

            internal override void ObserveInputs()
            {
                if (!_session.Surface.ConfigurationStale && Owner.ConsumeRefreshTick())
                    ObserveDueInputs();
                SubmitPendingRebuild();
            }

            private void ObserveDueInputs()
            {
                if (Owner.BakeMode == FlowFieldBakeMode.RuntimeDynamic)
                {
                    DetectGridTransformChange();
                    DetectGoalChange();
                    bool obstacleObserved = _session.Surface.DetectObstacleTransformsChanged();
                    bool probePending = _session.Surface.HasPendingObstacleProbe;
                    if (!_session.IsRebuilding
                        && (Owner._enableUnregisteredObstacleSweep || obstacleObserved || probePending))
                    {
                        if (Owner.TryCreateCurrentSurfaceRequest(out FlowFieldSurfaceRequest probeRequest))
                        {
                            _session.Surface.ProbeObstacleChanges(probeRequest);
                        }
                    }
                    else if (_session.IsRebuilding && obstacleObserved)
                    {
                        // Transform snapshots are updated immediately, but the
                        // physics probe waits until the in-flight BFS callback
                        // has returned. This keeps at most one probe and one BFS
                        // active while preserving the previous/next dirty bounds.
                        _session.Surface.RecordObstacleObservation(_session.Surface.DirtyObstacleRegion);
                    }
                }

                _session.Surface.DetectModifierChanges();
            }

            private void DetectGridTransformChange()
            {
                if (!_session.Surface.StagingGrid.IsValid)
                    return;
                FlowFieldGridSpace current = Owner.CreateGridSpace();
                if (_session.Surface.ObserveGrid(current))
                    _session.Surface.MarkDirty(FlowFieldSurfaceDirtyFlags.Grid, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            }

            private void DetectGoalChange()
            {
                if (!_session.Surface.StagingSurfaceReady || !_session.Surface.StagingGrid.IsValid)
                    return;
                if ((_session.Surface.DirtyFlags & FlowFieldSurfaceDirtyFlags.Goal) != 0)
                    return;
                FlowFieldSurfaceGoalChangeStatus status = _session.Surface.GoalTracker.DetectChange(
                    _session.Surface.StagingGrid,
                    _session.Surface.StagingSurfaceReady,
                    Owner._goalTransform,
                    Owner._hasExplicitGoal,
                    Owner._explicitGoalWorld,
                    Owner._goalInfluenceRadius);
                if (status == FlowFieldSurfaceGoalChangeStatus.Changed)
                {
                    _session.Surface.MarkDirty(
                        FlowFieldSurfaceDirtyFlags.Goal,
                        FlowFieldCellRect.Invalid,
                        FlowFieldCellRect.Invalid);
                    if (!Owner.HasConfiguredGoal)
                        _session.Surface.ObserveGoalRemoved();
                }
            }

            // The scheduler builds it later in this frame.
            private void SubmitPendingRebuild()
            {
                if (!Owner.isActiveAndEnabled || !_session.IsInitialized || _session.IsFaulted)
                    return;
                if (_session.Surface.RequiresExplicitRebuild || _session.IsRebuilding)
                    return;
                if (_session.HasPendingRequest
                    && Owner.TryCreateCurrentSurfaceRequest(out FlowFieldSurfaceRequest request))
                    _session.Surface.Submit(request);
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
                    || !_session.Surface.HasSubmittedRequest)
                {
                    if (!_session.Surface.ConfigurationStale)
                        _session.Surface.MarkConfigurationStale();
                    return;
                }

                // Keep the latest final-only scalar visible to a solve callback
                // even when the inspector edit lands between the solve starting and
                // the next RequestRebuild scheduling tick.
                _session.Surface.ObserveDefaultDirection(current.DefaultDirection);

                if (!_session.Surface.HasSameBaseInputs(current))
                {
                    MarkChangedBaseInputs(current);
                    return;
                }

                if (_session.Surface.HasSameFinalInputs(current))
                    return;

                FlowFieldSurfaceDirtyFlags finalFlags = FlowFieldSurfaceDirtyFlags.FinalRegion;
                try
                {
                    Vector3 normalized = FlowFieldVectorUtility.NormalizeDefaultDirection(Owner._defaultFlowDirection);
                    if ((_session.Surface.ResolvedDefaultDirection - normalized).sqrMagnitude > VALUE_EPSILON * VALUE_EPSILON)
                        finalFlags |= FlowFieldSurfaceDirtyFlags.DefaultDirection;
                }
                catch
                {
                    if (!_session.Surface.ConfigurationStale)
                        _session.Surface.MarkConfigurationStale();
                    return;
                }

                if ((_session.Surface.DirtyFlags & finalFlags) != finalFlags)
                    _session.Surface.MarkDirty(finalFlags, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            }

            private void MarkChangedBaseInputs(in FlowFieldSurfaceRequest current)
            {
                FlowFieldSurfaceDirtyFlags changedFlags = _session.Surface.GetInputDirtyFlags(current);
                // Goal and obstacle scalar edits are base-field changes, but
                // they can reuse the already-baked Surface. Only a real Grid
                // or source/signature change makes the current Surface stale
                // and requires an explicit full rebuild.
                if (Owner.BakeMode == FlowFieldBakeMode.StaticBaked
                    || (changedFlags & FlowFieldSurfaceDirtyFlags.Grid) != 0)
                {
                    if (!_session.Surface.ConfigurationStale)
                        _session.Surface.MarkConfigurationStale();
                }
                else if (changedFlags != FlowFieldSurfaceDirtyFlags.None)
                {
                    _session.Surface.MarkDirty(
                        changedFlags,
                        FlowFieldCellRect.Invalid,
                        FlowFieldCellRect.Invalid);
                }
            }

            internal override void ValidateConfiguration(bool validateStaticGoal)
                => Owner.ValidateSurfaceConfiguration(validateStaticGoal);

            internal override void MarkGoalDirty()
            {
                _session.Surface.GoalTracker.ResetWarning();
                _session.Surface.MarkDirty(
                    FlowFieldSurfaceDirtyFlags.Goal,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
                if (!Owner.HasConfiguredGoal)
                    _session.Surface.ObserveGoalRemoved();
            }

            internal override void MarkCellsDirty()
            {
                _session.Surface.MarkDirty(
                    FlowFieldSurfaceDirtyFlags.Grid,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
            }

            internal override void MarkConfigurationStale()
            {
                if (!_session.IsInitialized)
                    return;
                _session.Surface.MarkConfigurationStale();
            }

            internal override Bounds SnapBakeBounds(Bounds localBounds)
                => FlowFieldBakeBoundsUtility.SnapCenterAnchored(localBounds, Owner._cellSize);

            internal override void OnBakeBoundsChanged()
            {
                if (!_session.IsInitialized)
                    return;
                if (Owner.TryGetBakeLayout(out _, out FlowFieldGridSpace current))
                    _session.Surface.ObserveGrid(current);
                _session.Surface.MarkDirty(
                    FlowFieldSurfaceDirtyFlags.Grid,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
            }
        }
    }
}
