using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Shared owner for runtime and editor field state.  Stage algorithms stay
    /// in their existing files; this class owns only state, resource lifetime,
    /// request coalescing and the GPU/Managed backend boundary.
    /// </summary>
    internal sealed partial class FlowFieldSession : FlowFieldSessionBase
    {
        private readonly FlowFieldSessionState _state = new FlowFieldSessionState();
        private readonly FlowFieldFieldStore _fieldStore = new FlowFieldFieldStore();
        private readonly FlowFieldObstaclePipeline _obstaclePipeline = new FlowFieldObstaclePipeline();
        private readonly FlowFieldGoalTracker _goalTracker = new FlowFieldGoalTracker();
        private readonly IFlowFieldSurfaceSource _surfaceSource;
        private readonly IFlowFieldGpuBackend _injectedGpuBackend;
        private readonly Func<IFlowFieldGpuBackend> _gpuBackendFactory;
        private FlowFieldModifierRegistry _modifierRegistry;
        private FlowFieldModifierPipeline _modifierPipeline;
        private IFlowFieldGpuBackend _buildPipeline;
        private FlowFieldStaticBakeSnapshot _loadedStaticBakeSnapshot;

        private FlowFieldSessionRequest _committedRequest;
        private bool _hasCommittedRequest;
        private FlowFieldGridSpace _lastObservedGrid;
        private bool _hasObservedGrid;
        private bool _obstacleProbePending;
        private readonly FlowFieldSurfaceRebuildTracker _rebuild = new FlowFieldSurfaceRebuildTracker();
        private bool _hasPendingRequest;
        private FlowFieldSessionRequest _activeRequest;
        // Submit coalesces input here; the pending flag schedules it. Active
        // and committed requests retain their separate frozen lifetimes.
        private FlowFieldSessionRequest _latestRequestedInput;
        private bool _hasLatestRequestedInput;
        private Vector3 _latestDefaultDirection;
        private bool _hasLatestDefaultDirection;
        private bool _configurationStale;
        private FlowFieldSessionSourceKind _sourceKind;
        private FlowFieldBfsBackendPolicy _backendPolicy;
        private FlowFieldSessionLifecycle _lifecycle;
        private bool _pumping;
        // PreferGpu is permanently downgraded after a GPU failure for the
        // lifetime of this Session. Release clears the flag; Suspend keeps it
        // so a resume cannot repeatedly rediscover a broken backend.
        private bool _managedBackendForced;

        internal FlowFieldGridSpace StagingGrid => _state.Grid;
        internal bool StagingSurfaceReady => _state.SurfaceReady;
        internal FlowFieldDirtyFlags DirtyFlags => _state.DirtyFlags;
        internal FlowFieldCellRect DirtyFinalRegion => _state.DirtyFinalRegion;
        internal FlowFieldCellRect DirtyObstacleRegion => _state.DirtyObstacleRegion;
        internal Vector3 ResolvedDefaultDirection => _state.ResolvedDefaultDirection;
        // Editor and bake adapters request an explicit snapshot only at their
        // boundary. Runtime sampling reads the compact store directly.
        internal FlowFieldWorkspace CommittedWorkspace => _fieldStore.CreateWorkspaceSnapshot();
        internal FlowFieldReadView CommittedView => _fieldStore.CreateReadView();
        internal FlowFieldGoalTracker GoalTracker => _goalTracker;
        internal FlowFieldSurfaceData CommittedSurface => _fieldStore.Surface;
        internal FlowFieldGridSpace CommittedGrid => _fieldStore.Grid;
        internal override FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Surface2D;
        internal FlowFieldSessionLifecycle Lifecycle => _lifecycle;
        internal override FlowFieldRuntimeState State => RuntimeState;
        internal FlowFieldRuntimeState RuntimeState => _lifecycle switch
        {
            FlowFieldSessionLifecycle.Active => IsReady
                ? FlowFieldRuntimeState.Ready
                : FlowFieldRuntimeState.Building,
            FlowFieldSessionLifecycle.Building => FlowFieldRuntimeState.Building,
            FlowFieldSessionLifecycle.Suspended => FlowFieldRuntimeState.Suspended,
            FlowFieldSessionLifecycle.Faulted => FlowFieldRuntimeState.Faulted,
            FlowFieldSessionLifecycle.Released => FlowFieldRuntimeState.Released,
            _ => FlowFieldRuntimeState.Uninitialized,
        };
        internal override bool IsInitialized => _lifecycle == FlowFieldSessionLifecycle.Active
            || _lifecycle == FlowFieldSessionLifecycle.Building
            || _lifecycle == FlowFieldSessionLifecycle.Suspended;
        internal override bool IsFaulted => _lifecycle == FlowFieldSessionLifecycle.Faulted;
        internal override bool IsReady => (_lifecycle == FlowFieldSessionLifecycle.Active
                || _lifecycle == FlowFieldSessionLifecycle.Building)
            && !_configurationStale
            && _state.SurfaceReady
            && _fieldStore.IsValid
            && HasCompatibleCommittedSource();
        internal override bool IsRebuilding => _lifecycle == FlowFieldSessionLifecycle.Building;
        internal bool ConfigurationStale => _configurationStale;
        // Stale publications stay hidden until commit. A validated Submit
        // clears these unsubmitted flags, allowing that rebuild to be pumped.
        internal bool RequiresExplicitRebuild => _configurationStale
            && _rebuild.HasVersionedGrid;

        private bool HasCompatibleCommittedSource()
            => !_hasCommittedRequest
                || !_hasLatestRequestedInput
                || _latestRequestedInput.HasSameSource(_committedRequest);

        internal bool HasSubmittedRequest => _hasLatestRequestedInput;
        internal bool HasPendingRequest => _hasPendingRequest;

        /// <summary>
        /// Advances a pending Surface2D request at the owning execution
        /// boundary. Request submission only records/coalesces input; it does
        /// not recursively enter the build pipeline.
        /// </summary>
        internal bool Pump()
        {
            if (Disposed
                || _pumping
                || _lifecycle == FlowFieldSessionLifecycle.Suspended
                || _lifecycle == FlowFieldSessionLifecycle.Released
                || _lifecycle == FlowFieldSessionLifecycle.Faulted
                || _lifecycle == FlowFieldSessionLifecycle.Building)
                return false;
            if (!_hasPendingRequest)
                return false;
            _pumping = true;
            try { return BuildPending(); }
            finally { _pumping = false; }
        }
        internal bool HasPendingObstacleProbe => _obstacleProbePending;

        internal override bool TryGetFieldInfo(out FlowFieldFieldInfo info)
        {
            info = default;
            if (!IsReady || !_fieldStore.Grid.IsValid || _fieldStore.Surface == null || !_fieldStore.Surface.IsValid)
                return false;

            bool hasRequestedGoal = false;
            Vector3 requestedGoal = default;
            float influenceRadius = 0f;
            if (_hasLatestRequestedInput)
            {
                if (_latestRequestedInput.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot)
                {
                    FlowFieldStaticBakeSnapshot snapshot = _latestRequestedInput.StaticBakeSnapshot;
                    hasRequestedGoal = snapshot != null && snapshot.HasGoal;
                    requestedGoal = snapshot != null ? snapshot.RequestedGoalWorld : default;
                    influenceRadius = snapshot != null ? snapshot.GoalInfluenceRadius : 0f;
                }
                else
                {
                    hasRequestedGoal = _latestRequestedInput.Goal.IsValid;
                    requestedGoal = _latestRequestedInput.Goal.RequestedWorld;
                    influenceRadius = _latestRequestedInput.Goal.InfluenceRadius;
                }
            }

            Vector3 resolvedGoal = default;
            bool hasResolvedGoal = _fieldStore.HasActiveGoal
                && _fieldStore.ResolvedGoalIndex >= 0;
            if (hasResolvedGoal)
            {
                resolvedGoal = _fieldStore.Surface.GetCellCenter(_fieldStore.Grid, _fieldStore.ResolvedGoalIndex);
            }

            info = new FlowFieldFieldInfo(
                _fieldStore.Grid,
                _fieldStore.Surface.BakeBoundsWorld,
                FlowFieldSpaceMode.Surface2D,
                BakeMode,
                Revision,
                hasRequestedGoal,
                requestedGoal,
                influenceRadius,
                hasResolvedGoal,
                resolvedGoal);
            return info.IsValid;
        }

        /// <summary>
        /// Records a final-only scalar as soon as the Unity façade observes
        /// it. This closes the small window in which an inspector change can
        /// arrive after a GPU dispatch but before its callback: the callback
        /// must compose the freshly solved base with the newest default, even
        /// when no replacement request has been submitted yet.
        /// </summary>
        internal void ObserveDefaultDirection(Vector3 direction)
        {
            if (!FlowFieldGridSpace.IsFinite(direction)
                || direction.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                return;
            _latestDefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(direction);
            _hasLatestDefaultDirection = true;
        }

        /// <summary>
        /// Reports a non-solver failure (for example an invalid live Modifier
        /// configuration) through the same one-shot Faulted transition used
        /// by GPU/Managed failures. The caller deliberately stays outside the
        /// solver exception boundary.
        /// </summary>
        internal override void RetryFault()
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (_lifecycle != FlowFieldSessionLifecycle.Faulted)
                return;
            InvalidateCallbacks();
            Fault = null;
            // The fault may have interrupted a Goal that was prepared but not
            // solved. Forget it so the Goal stage cannot report it as unchanged.
            _goalTracker.ForgetLastBuild();
            // A retry is a new build attempt, not a re-publication of the
            // field that was available before the fault.  Keep the public
            // state in Building until the retry commits successfully; this
            // also preserves the contract that Faulted/initial retry paths
            // report IsReady=false rather than emitting a transient Ready.
            _configurationStale = true;
            _state.DirtyFlags |= FlowFieldDirtyFlags.All;
            _state.DirtyFinalRegion = _state.Grid.IsValid
                ? FlowFieldCellRect.Full(_state.Grid)
                : FlowFieldCellRect.Invalid;
            _state.DirtyObstacleRegion = _state.Grid.IsValid
                ? FlowFieldCellRect.Full(_state.Grid)
                : FlowFieldCellRect.Invalid;
            _hasPendingRequest = false;
            SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        internal bool HasSameBaseInputs(in FlowFieldSessionRequest request)
            => _hasLatestRequestedInput && _latestRequestedInput.HasSameBaseInputs(request);

        internal bool HasSameFinalInputs(in FlowFieldSessionRequest request)
            => _hasLatestRequestedInput && _latestRequestedInput.HasSameFinalInputs(request);

        internal FlowFieldDirtyFlags GetInputDirtyFlags(in FlowFieldSessionRequest request)
            => !_hasLatestRequestedInput
                ? FlowFieldDirtyFlags.All
                : _latestRequestedInput.GetInputDirtyFlags(request);

        /// <summary>
        /// Records the latest authoring grid observed by the Unity adapter.
        /// The adapter may call this while a GPU solve is in flight; keeping
        /// the observation in the Session prevents repeated dirty increments
        /// for one transform and still captures a second transform before the
        /// active callback returns.
        /// </summary>
        internal bool ObserveGrid(in FlowFieldGridSpace grid)
        {
            if (!grid.IsValid)
                return false;
            if (!_hasObservedGrid)
            {
                _lastObservedGrid = grid;
                _hasObservedGrid = true;
                return false;
            }

            if (_lastObservedGrid.MatchesBounds(grid))
                return false;

            _lastObservedGrid = grid;
            return true;
        }
        internal FlowFieldSession(
            IFlowFieldSurfaceSource surfaceSource = null,
            IFlowFieldGpuBackend gpuBackend = null,
            Func<IFlowFieldGpuBackend> gpuBackendFactory = null)
        {
            if (gpuBackend != null && gpuBackendFactory != null)
                throw new ArgumentException("Specify either a GPU backend or a backend factory, not both.");
            _surfaceSource = surfaceSource ?? FlowFieldRaycastSurfaceSource.Instance;
            _injectedGpuBackend = gpuBackend;
            _gpuBackendFactory = gpuBackendFactory;
            _lifecycle = FlowFieldSessionLifecycle.Uninitialized;
        }

        internal void Initialize(
            FlowFieldBakeMode bakeMode,
            FlowFieldSessionSourceKind sourceKind,
            FlowFieldBfsBackendPolicy backendPolicy,
            ComputeShader shader)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (_lifecycle == FlowFieldSessionLifecycle.Active
                || _lifecycle == FlowFieldSessionLifecycle.Building)
                throw new InvalidOperationException("FlowField session is already initialized.");
            if (_lifecycle == FlowFieldSessionLifecycle.Faulted)
                throw new InvalidOperationException("FlowField session is faulted. Retry the pending request or Release before Initialize.", Fault);
            if (_lifecycle == FlowFieldSessionLifecycle.Suspended
                && (BakeMode != bakeMode
                    || _sourceKind != sourceKind
                    || _backendPolicy != backendPolicy))
            {
                throw new InvalidOperationException(
                    "FlowField bake mode, source and backend policy are fixed until Release.");
            }

            // A resume performs a complete initial build from the current
            // inputs, so deferred probes that were queued before Suspend are
            // already represented by that build and must not be promoted a
            // second time.
            if (_lifecycle == FlowFieldSessionLifecycle.Suspended)
            {
                _rebuild.DropDeferredObstacle();
                _rebuild.DropDeferredSurface();
                _rebuild.DropDeferredGoal();
            }

            BakeMode = bakeMode;
            _sourceKind = sourceKind;
            _backendPolicy = backendPolicy;
            _modifierRegistry ??= new FlowFieldModifierRegistry();
            _modifierPipeline ??= new FlowFieldModifierPipeline(_modifierRegistry);
            if (_buildPipeline == null
                && sourceKind == FlowFieldSessionSourceKind.SceneBuild
                && backendPolicy != FlowFieldBfsBackendPolicy.ManagedOnly)
            {
                _buildPipeline = _gpuBackendFactory != null
                    ? _gpuBackendFactory()
                    : _injectedGpuBackend ?? new FlowFieldSurfaceBuildPipeline(shader);
                if (_buildPipeline == null)
                    throw new InvalidOperationException("The FlowField GPU backend factory returned null.");
            }

            Fault = null;
            _configurationStale = false;
            MarkDirty(FlowFieldDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        internal void MarkDirty(
            FlowFieldDirtyFlags flags,
            FlowFieldCellRect finalRegion,
            FlowFieldCellRect obstacleRegion,
            bool deferBaseVersion = false,
            bool deferSurfaceVersion = false,
            bool deferGoalVersion = false)
        {
            if (flags == FlowFieldDirtyFlags.None)
                return;
            if ((flags & (FlowFieldDirtyFlags.Grid | FlowFieldDirtyFlags.StaticObstacles)) != 0)
                _obstaclePipeline.DiscardStagedDynamicProbe();
            // A base or final-input change always requires final composition.
            // Adding the derived flag at the point where the input is
            // versioned keeps Submit from counting the same change twice when
            // a Manager marks dirty and then submits the latest request.
            if (FlowFieldSurfaceRebuildTracker.IsBaseDirty(flags)
                || (flags & (FlowFieldDirtyFlags.DefaultDirection
                    | FlowFieldDirtyFlags.ModifierArea
                    | FlowFieldDirtyFlags.ModifierValue)) != 0)
            {
                flags |= FlowFieldDirtyFlags.FinalRegion;
            }
            _state.DirtyFlags |= flags;
            if (finalRegion.IsValid)
                _state.ExpandFinalDirty(finalRegion);
            if (obstacleRegion.IsValid)
                _state.ExpandObstacleDirty(obstacleRegion);
            _rebuild.RecordMarkDirty(flags, deferBaseVersion, deferSurfaceVersion, deferGoalVersion);
            // Dirty observations reuse the latest accepted request. Initial
            // setup has no request yet and must wait for its first Submit.
            if (_hasLatestRequestedInput)
                _hasPendingRequest = true;
        }

        internal void MarkConfigurationStale()
        {
            if (!IsInitialized || _configurationStale)
                return;
            _configurationStale = true;
            MarkDirty(FlowFieldDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            // RuntimeState is derived from the lifecycle plus readiness. A
            // stale configuration therefore becomes Building immediately,
            // and observers receive the same single transition they would see
            // when a normal rebuild actually starts.
            PublishRuntimeState();
        }

        internal bool Submit(in FlowFieldSessionRequest request)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (!IsInitialized)
                throw new InvalidOperationException("FlowField session is not initialized.");
            if (request.SourceKind != _sourceKind)
                throw new InvalidOperationException("FlowField request source does not match the active session.");

            FlowFieldDirtyFlags inputFlags = FlowFieldDirtyFlags.None;
            if (_hasLatestRequestedInput && !request.HasSameInputs(_latestRequestedInput))
                inputFlags = _latestRequestedInput.GetInputDirtyFlags(request);
            ObserveDefaultDirection(request.DefaultDirection);

            // A staged obstacle probe is valid only for the exact Surface,
            // obstacle settings and registered collider set that produced it.
            // If a newer request changes one of those inputs, discard the
            // candidate before merging the request so the next build probes
            // against the new input instead of consuming stale scratch data.
            if ((inputFlags & (FlowFieldDirtyFlags.Grid
                    | FlowFieldDirtyFlags.StaticObstacles
                    | FlowFieldDirtyFlags.DynamicObstacles
                    | FlowFieldDirtyFlags.Escape)) != 0)
            {
                _obstaclePipeline.DiscardStagedDynamicProbe();
            }

            FlowFieldDirtyFlags combinedFlags = inputFlags | request.DirtyFlags;
            _state.DirtyFlags |= combinedFlags;
            if ((combinedFlags & FlowFieldDirtyFlags.FinalRegion) != 0)
            {
                _state.ExpandFinalDirty(
                    request.DirtyFinalRegion.IsValid
                        ? request.DirtyFinalRegion
                        : FlowFieldCellRect.Full(_state.Grid));
            }
            if (request.DirtyObstacleRegion.IsValid)
                _state.ExpandObstacleDirty(request.DirtyObstacleRegion);

            _latestRequestedInput = request;
            _hasLatestRequestedInput = true;
            _rebuild.RecordSubmit(combinedFlags);
            _hasPendingRequest = true;
            return true;
        }

        internal override bool RegisterModifierCommand(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.Register(modifier);
            ApplyModifierRegistryResult(result);
            return result.FinalDirty;
        }

        internal override bool UnregisterModifierCommand(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.Unregister(modifier);
            ApplyModifierRegistryResult(result);
            return result.FinalDirty;
        }

        internal FlowFieldModifierRegistryResult DetectModifierChanges()
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.DetectChanges();
            ApplyModifierRegistryResult(result);
            return result;
        }

        internal override void NotifyModifierChangedCommand(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            if ((change & (FlowFieldModifierChange.Value | FlowFieldModifierChange.Priority)) != 0)
            {
                EnsureModifierServices();
                FlowFieldModifierRegistryResult result = _modifierRegistry.MarkDirty(
                    modifier,
                    (change & FlowFieldModifierChange.Value) != 0,
                    out bool completed);
                if (!completed)
                    return;
                ApplyModifierRegistryResult(result);
            }
            if ((change & FlowFieldModifierChange.Area) != 0)
            {
                EnsureModifierServices();
                ApplyModifierRegistryResult(_modifierRegistry.MarkAreaDirty(modifier));
            }
        }

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
                FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape | FlowFieldDirtyFlags.FinalRegion,
                dirtyRegion,
                dirtyRegion,
                deferBaseVersion: true);
            return true;
        }

        internal override bool RegisterObstacleCommand(Collider collider)
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
                FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape | FlowFieldDirtyFlags.FinalRegion,
                dirtyRegion,
                dirtyRegion,
                deferBaseVersion: true);
            return true;
        }

        internal override bool UnregisterObstacleCommand(Collider collider)
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
                dirtyRegion,
                deferBaseVersion: true);
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
                _obstacleProbePending = true;
                _state.ExpandObstacleDirty(dirty);
            }
            return changed;
        }

        internal void RecordObstacleObservation(FlowFieldCellRect dirtyRegion)
        {
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            _obstacleProbePending = true;
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
                _obstacleProbePending = false;
                _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
                ClearNoOpObstacleRequest();
                return false;
            }
            _obstacleProbePending = false;
            dirty = FlowFieldCellRect.Union(dirty, _state.DirtyObstacleRegion);
            MarkDirty(
                FlowFieldDirtyFlags.DynamicObstacles
                    | FlowFieldDirtyFlags.Escape
                    | FlowFieldDirtyFlags.FinalRegion,
                dirty,
                dirty,
                deferBaseVersion: true);
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
            _rebuild.ClearVersioned(clearedFlags);
            _rebuild.DropDeferredObstacle();

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

        private void EnsureModifierServices()
        {
            if (_modifierRegistry == null || _modifierPipeline == null)
                throw new InvalidOperationException("FlowField modifier services are not initialized.");
        }

        private void ApplyModifierRegistryResult(FlowFieldModifierRegistryResult result)
        {
            FlowFieldDirtyFlags flags = FlowFieldDirtyFlags.None;
            if (result.AreaDirty)
                flags |= FlowFieldDirtyFlags.ModifierArea;
            if (result.ValueDirty)
                flags |= FlowFieldDirtyFlags.ModifierValue;
            if (result.FinalDirty)
                flags |= FlowFieldDirtyFlags.FinalRegion;
            if (flags != FlowFieldDirtyFlags.None)
                MarkDirty(flags, result.DirtyRegion, FlowFieldCellRect.Invalid);
        }

        private void InvalidateCallbacks()
        {
            AdvanceOperationGeneration();
            _modifierRegistry?.InvalidateOperations();
        }

        private protected override void AbandonWorkForFault() => InvalidateCallbacks();

        private protected override void ApplyFaultedState()
        {
            _state.DirtyFlags = FlowFieldDirtyFlags.None;
            SetLifecycle(FlowFieldSessionLifecycle.Faulted);
        }

        internal override void Suspend()
        {
            if (Disposed
                || _lifecycle == FlowFieldSessionLifecycle.Uninitialized
                || _lifecycle == FlowFieldSessionLifecycle.Released
                || _lifecycle == FlowFieldSessionLifecycle.Faulted
                || _lifecycle == FlowFieldSessionLifecycle.Suspended)
                return;
            InvalidateCallbacks();
            DetachBackend();
            _loadedStaticBakeSnapshot = null;

            _hasCommittedRequest = false;
            _committedRequest = default;
            _fieldStore.Clear();

            _state.BaseComposed = false;
            _state.LastComposedRegion = FlowFieldCellRect.Invalid;
            _state.HasObstacleMask = false;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            _state.Surface = null;
            _state.Workspace.ClearAll();
            // The complete rebuild on resume supersedes any probe that was
            // waiting for an in-flight callback. Keep the registered
            // colliders themselves, but do not run a duplicate probe against
            // the same pose after activation.
            _obstacleProbePending = false;
            _rebuild.DropDeferredGoal();
            _rebuild.ResetSupersededGoal();
            _state.DirtyFlags = FlowFieldDirtyFlags.All;
            _configurationStale = false;
            SetLifecycle(FlowFieldSessionLifecycle.Suspended);
        }

        internal override void Release()
        {
            if (Disposed)
                return;
            ReleaseResources();
        }

        private void ReleaseResources()
        {
            InvalidateCallbacks();
            DetachBackend();
            _modifierPipeline?.Clear();
            _modifierRegistry?.Clear();
            _modifierPipeline = null;
            _modifierRegistry = null;
            _obstaclePipeline.ClearDynamicObstacles();
            _goalTracker.Clear();
            _loadedStaticBakeSnapshot = null;

            _hasCommittedRequest = false;
            _committedRequest = default;
            _fieldStore.Clear();
            _state.Workspace.Release();
            _state.Grid = default;
            _state.Surface = null;

            _state.HasObstacleMask = false;
            _state.BaseComposed = false;
            _state.LastComposedRegion = FlowFieldCellRect.Invalid;
            _state.DirtyFlags = FlowFieldDirtyFlags.All;
            _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
            _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            _state.ResolvedDefaultDirection = Vector3.zero;
            _lastObservedGrid = default;
            _hasObservedGrid = false;
            _hasPendingRequest = false;
            _hasLatestRequestedInput = false;
            _obstacleProbePending = false;
            _rebuild.DropDeferredObstacle();
            _rebuild.DropDeferredSurface();
            _rebuild.DropDeferredGoal();
            _rebuild.ClearVersioned(FlowFieldDirtyFlags.All);
            _rebuild.ResetSupersededGoal();
            _managedBackendForced = false;
            GpuFallbackReason = null;
            _latestDefaultDirection = Vector3.zero;
            _hasLatestDefaultDirection = false;
            Fault = null;
            SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        internal void DisposePermanently()
        {
            if (Disposed)
                return;
            // Disposal is terminal before Released subscribers run, so they
            // cannot recreate resources or recursively dispose this owner.
            Disposed = true;
            ReleaseResources();
            // A directly injected backend is deliberately kept alive across
            // Release so the same Session can be initialized again. Permanent
            // disposal is the ownership boundary where it is finally freed.
            if (_injectedGpuBackend != null)
                _injectedGpuBackend.Dispose();
            MarkBaseDisposed();
            SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        public override void Dispose()
            => DisposePermanently();

        private void DetachBackend()
        {
            if (_buildPipeline == null)
                return;
            if (ReferenceEquals(_buildPipeline, _injectedGpuBackend))
            {
                // Keep a directly injected backend reusable for Release →
                // Initialize. DisposePermanently is the final owner boundary.
                _buildPipeline = null;
                return;
            }

            _buildPipeline.Dispose();
            _buildPipeline = null;
        }

        private void SetLifecycle(FlowFieldSessionLifecycle next)
        {
            if (_lifecycle == next)
                return;
            _lifecycle = next;
            PublishRuntimeState();
        }

        private void PublishRuntimeState() => PublishState(RuntimeState);
    }

}
