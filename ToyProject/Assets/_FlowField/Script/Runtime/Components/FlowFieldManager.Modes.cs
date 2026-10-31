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

            internal abstract FlowFieldSessionBase Session { get; }
            internal virtual bool ConfigurationStale => false;
            internal abstract void Initialize(int version);
            internal abstract bool ActivateSuspended(int version);
            internal abstract void SubmitRebuild(int version);
            internal abstract void ObserveInputs();
            internal virtual void OnFixedUpdate() { }
            internal abstract void OnSettingsEdited();
            internal virtual void OnReleased() { }
            internal abstract void ValidateConfiguration(bool validateStaticGoal);
            internal abstract void MarkGoalDirty();
            internal abstract void MarkCellsDirty();
            internal abstract void MarkConfigurationStale();
            internal abstract Bounds SnapBakeBounds(Bounds localBounds);
            internal abstract void OnBakeBoundsChanged();
        }

        private sealed class VolumeMode : ModeDriver
        {
            private readonly FlowFieldVolumeSession _session;

            internal VolumeMode(FlowFieldManager owner) : base(owner)
            {
                _session = owner._volumeSession;
            }

            internal override FlowFieldSessionBase Session => _session;

            internal override void Initialize(int version)
            {
                // Attach before Initialize so consumers observe the complete
                // lifecycle, including the first Uninitialized → Suspended →
                // Ready transition and any immediate fault.
                Owner.AttachSessionCallbacks(_session);
                _session.Initialize(Owner._bakeMode, Owner._frontierComputeShader);
                if (!Owner.OwnsLifecycleOperation(_session, version))
                    return;
                Owner._requiresActivationRebuild = !Owner.isActiveAndEnabled;
                if (Owner._requiresActivationRebuild)
                    _session.Suspend();
                if (!Owner.OwnsLifecycleOperation(_session, version))
                    return;
                Owner.RequestRebuild();
                if (Owner.OwnsLifecycleOperation(_session, version))
                    Owner.ReattachActiveModifiers();
            }

            internal override bool ActivateSuspended(int version)
            {
                Owner._requiresActivationRebuild = false;
                _session.Resume();
                if (!Owner.OwnsLifecycleOperation(_session, version))
                    return false;
                // A Modifier enabled during the suspension could not register.
                Owner.ReattachActiveModifiers();
                return Owner.OwnsLifecycleOperation(_session, version);
            }

            internal override void SubmitRebuild(int version)
            {
                Owner.QueueVolumeRequest(force: true, requeryObstacles: true);
                if (Owner.OwnsLifecycleOperation(_session, version))
                    _session.AttachRuntimeScheduler();
            }

            internal override void ObserveInputs()
            {
                if (Owner._enableUnregisteredObstacleSweep && Owner.ConsumeRefreshTick())
                    _session.DetectUnregisteredObstacleChanges(true);
                _session.DetectModifierChanges();
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

            internal override void OnReleased()
            {
#if UNITY_EDITOR
                // The Volume3D gizmo mesh is cached per result and must drop the released field.
                FlowFieldEditorVisualizationBridge.RequestRefresh(Owner);
#endif
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
                Owner.RefreshEditorPreview();
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

            internal SurfaceMode(FlowFieldManager owner) : base(owner)
            {
                _session = owner._session;
            }

            internal override FlowFieldSessionBase Session => _session;
            internal override bool ConfigurationStale => _session.ConfigurationStale;

            internal override void Initialize(int version)
            {
                if (!InitializeSession(version))
                    return;
                Owner._requiresActivationRebuild = !Owner.isActiveAndEnabled;
                if (Owner._requiresActivationRebuild)
                    _session.Suspend();
                if (Owner.OwnsLifecycleOperation(_session, version))
                    Owner.RequestRebuild();
            }

            internal override bool ActivateSuspended(int version)
            {
                Owner._requiresActivationRebuild = false;
                return InitializeSession(version);
            }

            private bool InitializeSession(int version)
            {
                FlowFieldSessionSourceKind sourceKind = Owner.BakeMode == FlowFieldBakeMode.StaticBaked
                    ? FlowFieldSessionSourceKind.StaticSnapshot
                    : FlowFieldSessionSourceKind.SceneBuild;
                FlowFieldBfsBackendPolicy policy = Application.isPlaying
                    ? FlowFieldBfsBackendPolicy.PreferGpu
                    : FlowFieldBfsBackendPolicy.ManagedOnly;
                Owner.AttachSessionCallbacks(_session);
                // Attach before Initialize so consumers observe the initial
                // Uninitialized → Building transition as well as later Ready and
                // Faulted transitions. Reattaching Modifiers remains after the
                // Session has created its registry.
                _session.Initialize(Owner._bakeMode, sourceKind, policy, Owner._frontierComputeShader);
                if (!Owner.OwnsLifecycleOperation(_session, version))
                    return false;
                Owner.ReattachActiveModifiers();
                return Owner.OwnsLifecycleOperation(_session, version);
            }

            internal override void SubmitRebuild(int version)
            {
                FlowFieldSurfaceBakeSettings settings = Owner.CreateSurfaceBakeSettings();
                _session.Submit(Owner.CreateSurfaceRequest(
                    settings,
                    FlowFieldDirtyFlags.None,
                    _session.DirtyFinalRegion,
                    _session.DirtyObstacleRegion));

                // A backend can fail synchronously (for example a RequireGpu
                // probe or an injected test backend). Treat that exactly like
                // a rejected request even when the backend reported that it
                // accepted a callback before transitioning the Session.
                if (version == Owner._lifecycleVersion
                    && ReferenceEquals(Owner._attachedSession, _session)
                    && _session.IsFaulted)
                    throw new InvalidOperationException($"{nameof(FlowFieldManager)} rebuild failed.", _session.Fault);
            }

            internal override void ObserveInputs()
            {
                if (!_session.ConfigurationStale && Owner.ConsumeRefreshTick())
                    ObserveDueInputs();
                // FixedUpdate does not run while the game time is stopped.
                if (Time.timeScale == 0f)
                    PumpPendingRebuild();
            }

            private void ObserveDueInputs()
            {
                if (Owner.BakeMode == FlowFieldBakeMode.RuntimeDynamic)
                {
                    DetectGridTransformChange();
                    DetectGoalChange();
                    bool obstacleObserved = _session.DetectObstacleTransformsChanged();
                    bool probePending = _session.HasPendingObstacleProbe;
                    if (!_session.IsRebuilding
                        && (Owner._enableUnregisteredObstacleSweep || obstacleObserved || probePending))
                    {
                        if (Owner.TryCreateCurrentSessionRequest(out FlowFieldSessionRequest probeRequest))
                        {
                            _session.ProbeObstacleChanges(probeRequest);
                        }
                    }
                    else if (_session.IsRebuilding && obstacleObserved)
                    {
                        // Transform snapshots are updated immediately, but the
                        // physics probe waits until the in-flight BFS callback
                        // has returned. This keeps at most one probe and one BFS
                        // active while preserving the previous/next dirty bounds.
                        _session.RecordObstacleObservation(_session.DirtyObstacleRegion);
                    }
                }

                _session.DetectModifierChanges();
            }

            private void DetectGridTransformChange()
            {
                if (!_session.StagingGrid.IsValid)
                    return;
                FlowFieldGridSpace current = Owner.CreateGridSpace();
                if (_session.ObserveGrid(current))
                    _session.MarkDirty(FlowFieldDirtyFlags.Grid, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            }

            private void DetectGoalChange()
            {
                if (!_session.StagingSurfaceReady || !_session.StagingGrid.IsValid)
                    return;
                if ((_session.DirtyFlags & FlowFieldDirtyFlags.Goal) != 0)
                    return;
                FlowFieldGoalChangeStatus status = _session.GoalTracker.DetectChange(
                    _session.StagingGrid,
                    _session.StagingSurfaceReady,
                    Owner._goalTransform,
                    Owner._hasExplicitGoal,
                    Owner._explicitGoalWorld,
                    Owner._goalInfluenceRadius);
                if (status == FlowFieldGoalChangeStatus.Invalid)
                    throw new InvalidOperationException("Active Goal became invalid.");
                if (status == FlowFieldGoalChangeStatus.Changed)
                {
                    _session.MarkDirty(
                        FlowFieldDirtyFlags.Goal,
                        FlowFieldCellRect.Invalid,
                        FlowFieldCellRect.Invalid,
                        deferGoalVersion: true);
                    Owner.RefreshEditorPreview();
                }
            }

            internal override void OnFixedUpdate() => PumpPendingRebuild();

            private void PumpPendingRebuild()
            {
                if (!Owner.isActiveAndEnabled || !_session.IsInitialized || _session.IsFaulted)
                    return;
                if (_session.RequiresExplicitRebuild || _session.IsRebuilding)
                    return;
                if (_session.HasPendingRequest
                    && Owner.TryCreateCurrentSessionRequest(out FlowFieldSessionRequest request))
                {
                    _session.Submit(request);
                    _session.Pump();
                }
            }

            internal override void OnSettingsEdited()
            {
                // OnValidate fires for every serialized property edit. Compare a
                // normalized request with the last submitted one so a default
                // direction/max-wave edit only rebuilds the final field, while a
                // real Surface/obstacle/Goal change invalidates the base build.
                // StaticBaked requests intentionally contain no runtime Goal or
                // dynamic-obstacle input, making those edits true no-ops.
                if (!Owner.TryCreateCurrentSessionRequest(out FlowFieldSessionRequest current)
                    || !_session.HasSubmittedRequest)
                {
                    if (!_session.ConfigurationStale)
                        _session.MarkConfigurationStale();
                    return;
                }

                // Keep the latest final-only scalar visible to a solve callback
                // even when the inspector edit lands between GPU dispatch and
                // the next RequestRebuild scheduling tick.
                _session.ObserveDefaultDirection(current.DefaultDirection);

                if (!_session.HasSameBaseInputs(current))
                {
                    MarkChangedBaseInputs(current);
                    return;
                }

                if (_session.HasSameFinalInputs(current))
                    return;

                FlowFieldDirtyFlags finalFlags = FlowFieldDirtyFlags.FinalRegion;
                try
                {
                    Vector3 normalized = FlowFieldVectorUtility.NormalizeDefaultDirection(Owner._defaultFlowDirection);
                    if ((_session.ResolvedDefaultDirection - normalized).sqrMagnitude > VALUE_EPSILON * VALUE_EPSILON)
                        finalFlags |= FlowFieldDirtyFlags.DefaultDirection;
                }
                catch
                {
                    if (!_session.ConfigurationStale)
                        _session.MarkConfigurationStale();
                    return;
                }

                if ((_session.DirtyFlags & finalFlags) != finalFlags)
                    _session.MarkDirty(finalFlags, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            }

            private void MarkChangedBaseInputs(in FlowFieldSessionRequest current)
            {
                FlowFieldDirtyFlags changedFlags = _session.GetInputDirtyFlags(current);
                // Goal and obstacle scalar edits are base-field changes, but
                // they can reuse the already-baked Surface. Only a real Grid
                // or source/signature change makes the current Surface stale
                // and requires an explicit full rebuild.
                if (Owner.BakeMode == FlowFieldBakeMode.StaticBaked
                    || (changedFlags & FlowFieldDirtyFlags.Grid) != 0)
                {
                    if (!_session.ConfigurationStale)
                        _session.MarkConfigurationStale();
                }
                else if (changedFlags != FlowFieldDirtyFlags.None)
                {
                    bool hasObstacleChange = (changedFlags
                        & (FlowFieldDirtyFlags.StaticObstacles
                            | FlowFieldDirtyFlags.DynamicObstacles
                            | FlowFieldDirtyFlags.Escape)) != 0;
                    bool hasGoalChange = (changedFlags & FlowFieldDirtyFlags.Goal) != 0;
                    bool hasGridChange = (changedFlags & FlowFieldDirtyFlags.Grid) != 0;
                    _session.MarkDirty(
                        changedFlags,
                        FlowFieldCellRect.Invalid,
                        FlowFieldCellRect.Invalid,
                        deferBaseVersion: hasObstacleChange && !hasGridChange,
                        deferGoalVersion: hasGoalChange && !hasGridChange);
                }
            }

            internal override void ValidateConfiguration(bool validateStaticGoal)
                => Owner.ValidateSurfaceConfiguration(validateStaticGoal);

            internal override void MarkGoalDirty()
            {
                _session.GoalTracker.ResetWarning();
                _session.MarkDirty(
                    FlowFieldDirtyFlags.Goal,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid,
                    deferGoalVersion: true);
            }

            internal override void MarkCellsDirty()
            {
                _session.MarkDirty(
                    FlowFieldDirtyFlags.Grid,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid,
                    deferSurfaceVersion: true);
                Owner.RefreshEditorPreview();
            }

            internal override void MarkConfigurationStale()
            {
                if (!_session.IsInitialized)
                    return;
                _session.MarkConfigurationStale();
                Owner.RefreshEditorPreview();
            }

            internal override Bounds SnapBakeBounds(Bounds localBounds)
                => FlowFieldBakeBoundsUtility.SnapCenterAnchored(localBounds, Owner._cellSize);

            internal override void OnBakeBoundsChanged()
            {
                if (!_session.IsInitialized)
                    return;
                if (Owner.TryGetBakeLayout(out _, out FlowFieldGridSpace current))
                    _session.ObserveGrid(current);
                _session.MarkDirty(
                    FlowFieldDirtyFlags.Grid,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid,
                    deferSurfaceVersion: true);
            }
        }
    }
}
