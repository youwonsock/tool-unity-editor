using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Mutable build state owned by a FlowFieldSession.  This type deliberately
    /// contains no Unity lifecycle or callback policy; it is the data plane
    /// shared by the common runtime/editor session and the existing stage
    /// implementations.
    /// </summary>
    internal sealed class FlowFieldSessionState
    {
        internal FlowFieldGridSpace Grid;
        internal FlowFieldSurfaceData Surface;
        internal readonly FlowFieldWorkspace Workspace = new FlowFieldWorkspace();
        internal FlowFieldDirtyFlags DirtyFlags = FlowFieldDirtyFlags.All;
        internal FlowFieldCellRect DirtyFinalRegion = FlowFieldCellRect.Invalid;
        internal FlowFieldCellRect DirtyObstacleRegion = FlowFieldCellRect.Invalid;
        internal FlowFieldCellRect LastComposedRegion = FlowFieldCellRect.Invalid;
        internal Vector3 ResolvedDefaultDirection = Vector3.zero;
        internal bool SurfaceReady;
        internal bool HasObstacleMask;
        internal bool BaseComposed;
        internal int LastSurfaceRevision = -1;
        internal int LastStaticObstacleRevision = -1;

        public void MarkDirty(FlowFieldDirtyFlags flags)
            => DirtyFlags |= flags;

        public void ExpandFinalDirty(FlowFieldCellRect rect)
            => DirtyFinalRegion = FlowFieldCellRect.Union(DirtyFinalRegion, rect);

        public void ExpandObstacleDirty(FlowFieldCellRect rect)
            => DirtyObstacleRegion = FlowFieldCellRect.Union(DirtyObstacleRegion, rect);

        public void Release()
            => Workspace.Release();
    }

    /// <summary>
    /// Shared owner for runtime and editor field state.  Stage algorithms stay
    /// in their existing files; this class owns only state, resource lifetime,
    /// request coalescing and the GPU/Managed backend boundary.
    /// </summary>
    internal sealed class FlowFieldSession : IDisposable
    {
        private readonly FlowFieldSessionState _state = new FlowFieldSessionState();
        private readonly FlowFieldFieldStore _fieldStore = new FlowFieldFieldStore();
        private readonly FlowFieldObstaclePipeline _obstaclePipeline = new FlowFieldObstaclePipeline();
        private readonly FlowFieldGoalTracker _goalTracker = new FlowFieldGoalTracker();
        private readonly IFlowFieldSurfaceSource _surfaceSource;
        private readonly IFlowFieldBfsBackend _injectedBfsBackend;
        private readonly Func<IFlowFieldBfsBackend> _bfsBackendFactory;
        private FlowFieldModifierRegistry _modifierRegistry;
        private FlowFieldModifierPipeline _modifierPipeline;
        private IFlowFieldBfsBackend _buildPipeline;
        private FlowFieldSurfaceData _runtimeSurface;
        private FlowFieldSurfaceData _committedSurface;
        private FlowFieldSurfaceData _committedSourceSurface;
        private FlowFieldGridSpace _committedGrid;
        private FlowFieldStaticBakeSnapshot _loadedStaticBakeSnapshot;
        private int _loadedStaticBakeRevision = -1;
        private FlowFieldGridSpace _lastObservedGrid;
        private bool _hasObservedGrid;
        private bool _obstacleProbePending;
        private int _committedSurfaceRevision = -1;
        private int _requestVersion;
        private int _baseVersion;
        private int _activeBaseVersion = -1;
        private int _activeRequestVersion = -1;
        private bool _hasPendingRequest;
        private FlowFieldSessionRequest _pendingRequest;
        private FlowFieldSessionRequest _activeRequest;
        private FlowFieldSessionRequest _lastRequest;
        private bool _hasLastRequest;
        private Vector3 _latestDefaultDirection;
        private bool _hasLatestDefaultDirection;
        // Obstacle registration/movement is first treated as a non-versioning
        // request.  The effective mask is not known until the physics probe
        // runs; only then is the pending input promoted to a base version.
        private bool _deferredObstacleVersion;
        private bool _deferredSurfaceVersion;
        private bool _deferredGoalVersion;
        // MarkDirty is also used by the Unity-facing façade before it submits
        // a request. Keep track of those flags so Submit can detect changes
        // from test/editor adapters without incrementing the version twice.
        private FlowFieldDirtyFlags _versionedDirtyFlags;
        private bool _configurationStale;
        private FlowFieldBakeMode _bakeMode;
        private FlowFieldSessionSourceKind _sourceKind;
        private FlowFieldBfsBackendPolicy _backendPolicy;
        private FlowFieldSessionLifecycle _lifecycle;
        private Exception _fault;
        private int _revision;
        private bool _disposed;
        // PreferGpu is permanently downgraded after a GPU failure for the
        // lifetime of this Session. Release clears the flag; Suspend keeps it
        // so a resume cannot repeatedly rediscover a broken backend.
        private bool _managedBackendForced;
        private int _callbackGeneration;
        private FlowFieldRuntimeState _publishedRuntimeState = FlowFieldRuntimeState.Uninitialized;

        internal FlowFieldGridSpace StagingGrid => _state.Grid;
        internal bool StagingSurfaceReady => _state.SurfaceReady;
        internal FlowFieldDirtyFlags DirtyFlags => _state.DirtyFlags;
        internal FlowFieldCellRect DirtyFinalRegion => _state.DirtyFinalRegion;
        internal FlowFieldCellRect DirtyObstacleRegion => _state.DirtyObstacleRegion;
        internal Vector3 ResolvedDefaultDirection => _state.ResolvedDefaultDirection;
        // Editor and bake adapters request an explicit snapshot only at their
        // boundary. Runtime sampling reads the compact store directly.
        internal FlowFieldWorkspace CommittedWorkspace => _fieldStore.CreateWorkspaceSnapshot();
        internal FlowFieldObstaclePipeline ObstaclePipeline => _obstaclePipeline;
        internal FlowFieldGoalTracker GoalTracker => _goalTracker;
        internal FlowFieldModifierRegistry ModifierRegistry => _modifierRegistry;
        internal FlowFieldModifierPipeline ModifierPipeline => _modifierPipeline;
        internal IFlowFieldBfsBackend BuildPipeline => _buildPipeline;
        internal FlowFieldSurfaceData RuntimeSurface => _runtimeSurface;
        internal FlowFieldSurfaceData CommittedSurface => _committedSurface;
        internal FlowFieldSurfaceData CommittedSourceSurface => _committedSourceSurface;
        internal FlowFieldGridSpace CommittedGrid => _committedGrid;
        internal int CommittedSurfaceRevision => _committedSurfaceRevision;
        internal FlowFieldBakeMode BakeMode => _bakeMode;
        internal FlowFieldSessionSourceKind SourceKind => _sourceKind;
        internal FlowFieldSessionLifecycle Lifecycle => _lifecycle;
        internal Exception Fault => _fault;
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
        internal string LastError => _fault?.Message;
        internal bool IsInitialized => _lifecycle == FlowFieldSessionLifecycle.Active
            || _lifecycle == FlowFieldSessionLifecycle.Building
            || _lifecycle == FlowFieldSessionLifecycle.Suspended
            || _lifecycle == FlowFieldSessionLifecycle.Faulted;
        internal bool IsFaulted => _lifecycle == FlowFieldSessionLifecycle.Faulted;
        internal bool IsReady => (_lifecycle == FlowFieldSessionLifecycle.Active
                || _lifecycle == FlowFieldSessionLifecycle.Building)
            && !_configurationStale
            && _state.SurfaceReady
            && _fieldStore.IsValid;
        internal bool IsRebuilding => _lifecycle == FlowFieldSessionLifecycle.Building;
        internal bool ConfigurationStale => _configurationStale;
        internal int Revision => _revision;
        internal int RequestVersion => _requestVersion;
        internal int BaseVersion => _baseVersion;
        internal bool HasSubmittedRequest => _hasLastRequest;
        internal bool HasPendingRequest => _hasPendingRequest || _state.DirtyFlags != FlowFieldDirtyFlags.None;
        internal bool HasPendingObstacleProbe => _obstacleProbePending;

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
        internal void ReportFault(Exception exception)
        {
            if (_disposed
                || _lifecycle == FlowFieldSessionLifecycle.Uninitialized
                || _lifecycle == FlowFieldSessionLifecycle.Released
                || _lifecycle == FlowFieldSessionLifecycle.Faulted)
                return;
            Fail(exception ?? new InvalidOperationException("FlowField session failed."));
        }

        internal void RetryFault()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (_lifecycle != FlowFieldSessionLifecycle.Faulted)
                return;
            _fault = null;
            // A retry is a new build attempt, not a re-publication of the
            // field that was available before the fault.  Keep the public
            // state in Building until the retry commits successfully; this
            // also preserves the contract that Faulted/initial retry paths
            // report IsReady=false rather than emitting a transient Ready.
            _configurationStale = true;
            SetLifecycle(FlowFieldSessionLifecycle.Active);
            _state.DirtyFlags |= FlowFieldDirtyFlags.All;
            _state.DirtyFinalRegion = _state.Grid.IsValid
                ? FlowFieldCellRect.Full(_state.Grid)
                : FlowFieldCellRect.Invalid;
            _state.DirtyObstacleRegion = _state.Grid.IsValid
                ? FlowFieldCellRect.Full(_state.Grid)
                : FlowFieldCellRect.Invalid;
            _hasPendingRequest = false;
        }

        internal bool HasSameBaseInputs(in FlowFieldSessionRequest request)
            => _hasLastRequest && _lastRequest.HasSameBaseInputs(request);

        internal bool HasSameFinalInputs(in FlowFieldSessionRequest request)
            => _hasLastRequest && _lastRequest.HasSameFinalInputs(request);

        internal FlowFieldDirtyFlags GetInputDirtyFlags(in FlowFieldSessionRequest request)
            => !_hasLastRequest
                ? FlowFieldDirtyFlags.All
                : _lastRequest.GetInputDirtyFlags(request);

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
        internal event Action<bool> FieldCommitted;
        internal event Action<Exception> Failed;
        internal event Action<FlowFieldRuntimeState> StateChanged;

        internal FlowFieldSession(
            IFlowFieldSurfaceSource surfaceSource = null,
            IFlowFieldBfsBackend bfsBackend = null,
            Func<IFlowFieldBfsBackend> bfsBackendFactory = null)
        {
            if (bfsBackend != null && bfsBackendFactory != null)
                throw new ArgumentException("Specify either a BFS backend or a backend factory, not both.");
            _surfaceSource = surfaceSource ?? FlowFieldRaycastSurfaceSource.Instance;
            _injectedBfsBackend = bfsBackend;
            _bfsBackendFactory = bfsBackendFactory;
            _lifecycle = FlowFieldSessionLifecycle.Uninitialized;
        }

        internal void Initialize(
            FlowFieldBakeMode bakeMode,
            FlowFieldSessionSourceKind sourceKind,
            FlowFieldBfsBackendPolicy backendPolicy,
            ComputeShader shader)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (_lifecycle == FlowFieldSessionLifecycle.Active
                || _lifecycle == FlowFieldSessionLifecycle.Building)
                throw new InvalidOperationException("FlowField session is already initialized.");
            if (_lifecycle == FlowFieldSessionLifecycle.Faulted)
                throw new InvalidOperationException("FlowField session is faulted. Retry the pending request or Release before Initialize.", _fault);
            if (_lifecycle == FlowFieldSessionLifecycle.Suspended
                && (_bakeMode != bakeMode
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
                _deferredObstacleVersion = false;
                _deferredSurfaceVersion = false;
                _deferredGoalVersion = false;
            }

            _bakeMode = bakeMode;
            _sourceKind = sourceKind;
            _backendPolicy = backendPolicy;
            _modifierRegistry ??= new FlowFieldModifierRegistry();
            _modifierPipeline ??= new FlowFieldModifierPipeline(_modifierRegistry);
            if (_buildPipeline == null
                && sourceKind == FlowFieldSessionSourceKind.SceneBuild
                && backendPolicy != FlowFieldBfsBackendPolicy.ManagedOnly)
            {
                _buildPipeline = _bfsBackendFactory != null
                    ? _bfsBackendFactory()
                    : _injectedBfsBackend ?? new FlowFieldBuildPipeline(shader);
                if (_buildPipeline == null)
                    throw new InvalidOperationException("The FlowField BFS backend factory returned null.");
            }

            _fault = null;
            _configurationStale = false;
            SetLifecycle(FlowFieldSessionLifecycle.Active);
            MarkDirty(FlowFieldDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid, baseChange: true);
        }

        internal void MarkDirty(
            FlowFieldDirtyFlags flags,
            FlowFieldCellRect finalRegion,
            FlowFieldCellRect obstacleRegion,
            bool baseChange,
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
            if (IsBaseDirty(flags)
                || (flags & (FlowFieldDirtyFlags.DefaultDirection
                    | FlowFieldDirtyFlags.ModifierArea
                    | FlowFieldDirtyFlags.ModifierValue)) != 0)
            {
                flags |= FlowFieldDirtyFlags.FinalRegion;
            }
            // Keep BaseVersion restricted to actual BFS-affecting flags even
            // if an adapter supplies an overly broad baseChange hint. Final-
            // only changes (DefaultDirection, Modifier*, FinalRegion) never
            // bump it.
            baseChange = IsBaseDirty(flags);
            _state.DirtyFlags |= flags;
            if (finalRegion.IsValid)
                _state.ExpandFinalDirty(finalRegion);
            if (obstacleRegion.IsValid)
                _state.ExpandObstacleDirty(obstacleRegion);
            unchecked
            {
                if (deferBaseVersion)
                    _deferredObstacleVersion = true;
                if (deferSurfaceVersion)
                    _deferredSurfaceVersion = true;
                if (deferGoalVersion)
                {
                    // Goal authoring changes are accepted immediately, but
                    // the resolved cell/influence result is not known until
                    // the shared Goal stage runs. RequestVersion therefore
                    // advances now while BaseVersion waits for that stage to
                    // prove that the base graph actually changed.
                    _deferredGoalVersion = true;
                }
                // Obstacle-only requests intentionally wait for the physics
                // probe before becoming observable. Surface and Goal edits
                // are authoring changes, so they advance RequestVersion as
                // soon as they are accepted. If several deferred categories
                // are marked together, still advance it only once.
                if (!deferBaseVersion || deferSurfaceVersion || deferGoalVersion)
                    _requestVersion++;
                FlowFieldDirtyFlags deferredBaseFlags = FlowFieldDirtyFlags.None;
                if (deferBaseVersion)
                    deferredBaseFlags |= FlowFieldDirtyFlags.StaticObstacles
                        | FlowFieldDirtyFlags.DynamicObstacles
                        | FlowFieldDirtyFlags.Escape;
                if (deferSurfaceVersion)
                    deferredBaseFlags |= FlowFieldDirtyFlags.Grid;
                if (deferGoalVersion)
                    deferredBaseFlags |= FlowFieldDirtyFlags.Goal;
                FlowFieldDirtyFlags immediateBaseFlags = (flags & BaseDirtyMask)
                    & ~deferredBaseFlags;
                if (immediateBaseFlags != FlowFieldDirtyFlags.None)
                    _baseVersion++;
            }
            _versionedDirtyFlags |= flags;
        }

        internal void MarkConfigurationStale()
        {
            if (!IsInitialized || _configurationStale)
                return;
            _configurationStale = true;
            MarkDirty(FlowFieldDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid, baseChange: true);
            // RuntimeState is derived from the lifecycle plus readiness. A
            // stale configuration therefore becomes Building immediately,
            // and observers receive the same single transition they would see
            // when a normal rebuild actually starts.
            PublishRuntimeState();
        }

        internal bool Submit(in FlowFieldSessionRequest request)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (!IsInitialized)
                throw new InvalidOperationException("FlowField session is not initialized.");
            if (request.SourceKind != _sourceKind)
                throw new InvalidOperationException("FlowField request source does not match the active session.");

            FlowFieldDirtyFlags inputFlags = FlowFieldDirtyFlags.None;
            if (_hasLastRequest && !request.HasSameInputs(_lastRequest))
                inputFlags = _lastRequest.GetInputDirtyFlags(request);
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

            // A request can be submitted by a test/editor adapter without a
            // preceding MarkDirty call. Merge those inferred and explicit
            // flags once, then subtract the flags already versioned by the
            // façade. This gives one RequestVersion/BaseVersion increment for
            // one logical input change regardless of which entry point marked
            // it first.
            FlowFieldDirtyFlags combinedFlags = inputFlags | request.DirtyFlags;
            FlowFieldDirtyFlags unversionedFlags = combinedFlags & ~_versionedDirtyFlags;
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

            _lastRequest = request;
            _hasLastRequest = true;
            if (unversionedFlags != FlowFieldDirtyFlags.None)
            {
                FlowFieldDirtyFlags deferredObstacleFlags = unversionedFlags
                    & (FlowFieldDirtyFlags.StaticObstacles
                        | FlowFieldDirtyFlags.DynamicObstacles
                        | FlowFieldDirtyFlags.Escape);
                if (deferredObstacleFlags != FlowFieldDirtyFlags.None)
                    _deferredObstacleVersion = true;
                FlowFieldDirtyFlags deferredGoalFlags = unversionedFlags & FlowFieldDirtyFlags.Goal;
                if (deferredGoalFlags != FlowFieldDirtyFlags.None)
                    _deferredGoalVersion = true;
                FlowFieldDirtyFlags deferredSurfaceFlags = unversionedFlags & FlowFieldDirtyFlags.Grid;
                if (deferredSurfaceFlags != FlowFieldDirtyFlags.None)
                    _deferredSurfaceVersion = true;
                FlowFieldDirtyFlags immediatelyVersioned = unversionedFlags
                    & ~(deferredObstacleFlags | deferredSurfaceFlags | deferredGoalFlags);
                // FinalRegion is a derived flag for an obstacle-only request.
                // Do not turn that derived bit into a visible RequestVersion;
                // the probe below decides whether the base input really
                // changed. If another source (Goal, DefaultDirection or a
                // Modifier) is present, keep it and version normally.
                if ((deferredObstacleFlags != FlowFieldDirtyFlags.None
                        || deferredSurfaceFlags != FlowFieldDirtyFlags.None
                        || deferredGoalFlags != FlowFieldDirtyFlags.None)
                    && (unversionedFlags
                        & ~(deferredObstacleFlags
                            | deferredSurfaceFlags
                            | deferredGoalFlags
                            | FlowFieldDirtyFlags.FinalRegion)) == 0)
                {
                    immediatelyVersioned &= ~FlowFieldDirtyFlags.FinalRegion;
                }
                unchecked
                {
                    if (deferredSurfaceFlags != FlowFieldDirtyFlags.None
                        || deferredGoalFlags != FlowFieldDirtyFlags.None
                        || immediatelyVersioned != FlowFieldDirtyFlags.None)
                    {
                        _requestVersion++;
                        if (IsBaseDirty(immediatelyVersioned))
                            _baseVersion++;
                    }
                }
            }
            _versionedDirtyFlags = FlowFieldDirtyFlags.None;
            _pendingRequest = request;
            _hasPendingRequest = true;
            if (_lifecycle == FlowFieldSessionLifecycle.Suspended)
                return true;
            if (IsRebuilding)
                return true;

            return BuildPending();
        }

        internal FlowFieldModifierRegistryResult RegisterModifier(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.Register(modifier);
            ApplyModifierRegistryResult(result);
            return result;
        }

        internal FlowFieldModifierRegistryResult UnregisterModifier(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.Unregister(modifier);
            ApplyModifierRegistryResult(result);
            return result;
        }

        internal FlowFieldModifierRegistryResult MarkModifierDirty(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.MarkDirty(modifier);
            ApplyModifierRegistryResult(result);
            return result;
        }

        internal FlowFieldModifierRegistryResult MarkModifierAreaDirty(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.MarkAreaDirty(modifier);
            ApplyModifierRegistryResult(result);
            return result;
        }

        internal FlowFieldModifierRegistryResult DetectModifierChanges()
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result = _modifierRegistry.DetectChanges();
            ApplyModifierRegistryResult(result);
            return result;
        }

        internal bool RegisterObstacle(Collider collider, FlowFieldCellRect dirtyRegion)
        {
            if (collider == null)
                throw new ArgumentNullException(nameof(collider));
            if (_bakeMode == FlowFieldBakeMode.StaticBaked)
                return true;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            if (!_obstaclePipeline.RegisterDynamicObstacle(collider))
                return false;
            MarkDirty(
                FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape | FlowFieldDirtyFlags.FinalRegion,
                dirtyRegion,
                dirtyRegion,
                baseChange: true,
                deferBaseVersion: true);
            return true;
        }

        internal bool UnregisterObstacle(Collider collider, FlowFieldCellRect dirtyRegion)
        {
            if (collider == null)
                throw new ArgumentNullException(nameof(collider));
            if (_bakeMode == FlowFieldBakeMode.StaticBaked)
                return true;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            if (!_obstaclePipeline.UnregisterDynamicObstacle(collider))
                return false;
            MarkDirty(
                FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape | FlowFieldDirtyFlags.FinalRegion,
                dirtyRegion,
                dirtyRegion,
                baseChange: true,
                deferBaseVersion: true);
            return true;
        }

        internal void MarkObstacleDirty(FlowFieldCellRect dirtyRegion)
        {
            if (_bakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            MarkDirty(
                FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape | FlowFieldDirtyFlags.FinalRegion,
                dirtyRegion,
                dirtyRegion,
                baseChange: true,
                deferBaseVersion: true);
        }

        internal bool DetectObstacleTransformsChanged()
        {
            if (_bakeMode == FlowFieldBakeMode.StaticBaked || !_state.Grid.IsValid)
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
            if (_bakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            _obstacleProbePending = true;
            if (dirtyRegion.IsValid)
                _state.ExpandObstacleDirty(dirtyRegion);
        }

        internal bool ProbeObstacleChanges(in FlowFieldSessionRequest request)
        {
            if (_bakeMode == FlowFieldBakeMode.StaticBaked
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
                baseChange: true,
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
            _versionedDirtyFlags &= ~clearedFlags;
            _deferredObstacleVersion = false;

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

        internal FlowFieldSample Sample(Vector3 worldPosition)
        {
            if (!IsReady)
                throw new InvalidOperationException("FlowField session is not ready.");
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));

            bool useCommitted = IsRebuilding && _fieldStore.IsValid;
            FlowFieldGridSpace sampleGrid = useCommitted ? _fieldStore.Grid : _state.Grid;
            FlowFieldSurfaceData sampleSurface = useCommitted ? _fieldStore.Surface : _state.Surface;
            if (!sampleGrid.ContainsWorldPosition(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition), "Position is outside the FlowField grid.");
            FlowFieldSample sample;
            if (useCommitted)
            {
                if (!_fieldStore.TrySample(worldPosition, out sample))
                    throw new InvalidOperationException("FlowField sampling data is inconsistent.");
            }
            else if (!FlowFieldCellSampler.TrySample(sampleGrid, sampleSurface, _state.Workspace, worldPosition, out sample))
                throw new InvalidOperationException("FlowField sampling data is inconsistent.");

            if (sampleGrid.TryWorldToLocal(worldPosition, out int sampleX, out int sampleZ))
            {
                int sampleIndex = sampleGrid.ToFlatIndex(sampleX, sampleZ);
                bool stagedObstacleBlocksSample = _obstaclePipeline.HasStagedDynamicProbe
                    && sampleIndex >= 0
                    && sampleIndex < _state.Workspace.Capacity
                    && _state.Workspace.ObstacleScratch[sampleIndex];
                if ((IsRebuilding || _obstaclePipeline.HasStagedDynamicProbe)
                    && sampleIndex >= 0
                    && sampleIndex < _state.Workspace.Capacity
                    && _state.Workspace.Capacity == _state.Grid.CellCount
                    && (_state.Workspace.Blocked[sampleIndex] || stagedObstacleBlocksSample))
                    return new FlowFieldSample(Vector3.zero, 0f, sample.SurfaceNormal, sample.HasSurface);

                int next = useCommitted
                    ? _fieldStore.NextCells[sampleIndex]
                    : _state.Workspace.NextCells[sampleIndex];
                if (next >= 0)
                {
                    FlowFieldWorkspace latestWorkspace = _state.Workspace;
                    FlowFieldSurfaceData latestSurface = _state.Surface;
                    bool nextBlocked = next >= sampleGrid.CellCount
                        || latestWorkspace.Capacity != _state.Grid.CellCount
                        || next >= latestWorkspace.Capacity
                        || latestWorkspace.Blocked[next]
                        || _obstaclePipeline.HasStagedDynamicProbe
                            && latestWorkspace.ObstacleScratch[next]
                        || latestSurface == null
                        || !latestSurface.IsSurfaceValid(next);
                    bool topologyChanged = false;
                    if (!IsRebuilding && !nextBlocked && next != sampleIndex)
                    {
                        sampleGrid.FromFlatIndex(sampleIndex, out int currentX, out int currentZ);
                        sampleGrid.FromFlatIndex(next, out int nextX, out int nextZ);
                        int directionIndex = FlowFieldNeighborUtility.FindDirectionIndex(nextX - currentX, nextZ - currentZ);
                        topologyChanged = directionIndex < 0
                            || latestWorkspace.TopologyMasks == null
                            || sampleIndex >= latestWorkspace.TopologyMasks.Length
                            || (latestWorkspace.TopologyMasks[sampleIndex] & (1 << directionIndex)) == 0;
                    }
                    if (nextBlocked || topologyChanged)
                        return new FlowFieldSample(Vector3.zero, 0f, sample.SurfaceNormal, sample.HasSurface);
                }
            }
            return sample;
        }

        internal bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsReady || !FlowFieldGridSpace.IsFinite(worldPosition))
                return false;
            FlowFieldGridSpace grid = _committedGrid.IsValid ? _committedGrid : _state.Grid;
            if (!grid.IsValid || !grid.ContainsWorldPosition(worldPosition))
                return false;
            try
            {
                sample = Sample(worldPosition);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        internal FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("FlowField session is not initialized.");
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));
            FlowFieldGridSpace grid = IsRebuilding && _committedGrid.IsValid ? _committedGrid : _state.Grid;
            if (!grid.IsValid)
                throw new InvalidOperationException("FlowField grid is not initialized.");
            Vector3 clamped = grid.ClampWorldXZ(worldPosition);
            return new FlowFieldClampResult(
                clamped,
                !Mathf.Approximately(worldPosition.x, clamped.x),
                !Mathf.Approximately(worldPosition.z, clamped.z));
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
                MarkDirty(flags, result.DirtyRegion, FlowFieldCellRect.Invalid, baseChange: false);
        }

        private bool BuildPending()
        {
            if (!_hasPendingRequest)
                return true;

            FlowFieldSessionRequest request = _pendingRequest;
            _hasPendingRequest = false;
            FlowFieldDirtyFlags pending = _state.DirtyFlags;
            _state.DirtyFlags = FlowFieldDirtyFlags.None;

            try
            {
                bool surfaceChanged = PrepareSurface(request, pending);
                if (surfaceChanged)
                    PromoteDeferredSurfaceVersion();
                else
                    _deferredSurfaceVersion = false;
                // Surface preparation may invalidate every downstream stage
                // (for example after a grid change). Include those flags in
                // this build rather than leaving them for a later no-op tick.
                if (surfaceChanged)
                    pending |= _state.DirtyFlags;
                else
                    pending &= ~FlowFieldDirtyFlags.Grid;
                _state.DirtyFlags = FlowFieldDirtyFlags.None;
                if (request.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot)
                {
                    BuildStaticSnapshot(request, pending);
                    return true;
                }

                BuildSceneBase(request, ref pending, surfaceChanged);
                if ((pending & FlowFieldDirtyFlags.ModifierArea) != 0)
                {
                    bool areaChanged = RebuildModifierAreaData(
                        request,
                        out bool modifierAreaChanged,
                        out FlowFieldCellRect modifierRegion);
                    if (areaChanged)
                    {
                        pending |= FlowFieldDirtyFlags.FinalRegion;
                        _state.ExpandFinalDirty(
                            modifierRegion.IsValid
                                ? modifierRegion
                                : FlowFieldCellRect.Full(_state.Grid));
                    }
                    if (!modifierAreaChanged
                        && (pending & (FlowFieldDirtyFlags.ModifierValue
                            | FlowFieldDirtyFlags.DefaultDirection
                            | FlowFieldDirtyFlags.Grid
                            | FlowFieldDirtyFlags.StaticObstacles
                            | FlowFieldDirtyFlags.DynamicObstacles
                            | FlowFieldDirtyFlags.Escape
                            | FlowFieldDirtyFlags.Goal)) == 0)
                    {
                        pending &= ~(FlowFieldDirtyFlags.ModifierArea | FlowFieldDirtyFlags.FinalRegion);
                        _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                    }
                }
                if ((pending & (FlowFieldDirtyFlags.ModifierValue | FlowFieldDirtyFlags.FinalRegion)) != 0)
                    ComposeFinal(
                        request,
                        IsBaseDirty(pending)
                            || (pending & FlowFieldDirtyFlags.DefaultDirection) != 0
                            || !_state.BaseComposed);

                bool topologyChanged = (pending & (FlowFieldDirtyFlags.Grid
                    | FlowFieldDirtyFlags.StaticObstacles
                    | FlowFieldDirtyFlags.DynamicObstacles
                    | FlowFieldDirtyFlags.Escape
                    | FlowFieldDirtyFlags.Goal)) != 0;
                if (_state.Workspace.HasActiveGoal && topologyChanged)
                    return StartBfs(request);

                Commit(
                    request,
                    resultChanged: (pending & FlowFieldDirtyFlags.FinalRegion) != 0,
                    includeBase: IsBaseDirty(pending) || !_fieldStore.IsValid,
                    finalRegion: _state.LastComposedRegion);
                return true;
            }
            catch (Exception exception)
            {
                Fail(exception);
                return false;
            }
        }

        private bool PrepareSurface(in FlowFieldSessionRequest request, FlowFieldDirtyFlags pending)
        {
            if ((pending & FlowFieldDirtyFlags.Grid) == 0
                && _state.Grid.IsValid
                && _state.SurfaceReady)
                return false;

            // Any Surface/grid rebuild invalidates an obstacle candidate that
            // was measured against the previous cell heights and bounds.
            // Leave the registered collider set intact; the downstream base
            // stage will issue one fresh dynamic query for the new Surface.
            _obstaclePipeline.DiscardStagedDynamicProbe();

            FlowFieldSurfaceData surface;
            if (request.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot)
            {
                if (request.StaticBakeSnapshot == null || !request.StaticBakeSnapshot.HasValidData)
                    throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");
                bool surfaceMatches = request.StaticBakeSnapshot.MatchesSurface(
                    request.SurfaceSettings,
                    out string surfaceMismatch);
                bool obstaclesMatch = request.StaticBakeSnapshot.MatchesObstacles(
                    request.ObstacleLayer,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset,
                    request.ObstacleClearance,
                    out string obstacleMismatch);
                if (!surfaceMatches || !obstaclesMatch)
                {
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(surfaceMismatch) ? obstacleMismatch : surfaceMismatch);
                }
                surface = request.StaticBakeSnapshot.Surface;
                _runtimeSurface = surface;
            }
            else
            {
                FlowFieldSurfaceData supplied = _surfaceSource.Build(
                    request.SurfaceSettings,
                    request.SurfaceName);
                if (supplied == null)
                    throw new InvalidOperationException("FlowField surface source returned no surface data.");
                _runtimeSurface = supplied;
                surface = supplied;
            }

            if (!surface.IsValid)
                throw new InvalidOperationException("FlowField surface source returned invalid data.");

            bool boundsChanged = !_state.Grid.MatchesBounds(request.SurfaceSettings.Grid);
            FlowFieldSurfaceData previousSurface = _state.Surface;
            bool staticSnapshotChanged = request.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot
                && (!ReferenceEquals(_loadedStaticBakeSnapshot, request.StaticBakeSnapshot)
                    || _loadedStaticBakeRevision != request.StaticBakeSnapshot.Revision);
            bool surfaceChanged = boundsChanged
                || !_state.SurfaceReady
                || previousSurface == null
                || staticSnapshotChanged
                || !ReferenceEquals(previousSurface, surface)
                    && !previousSurface.ContentEquals(surface);

            // A full Surface notification may legitimately produce the same
            // immutable hit/normal arrays. Preserve the previous object in
            // that case so Goal/obstacle-only work does not trigger a
            // committed Surface copy or an unnecessary downstream BFS.
            if (!surfaceChanged)
            {
                _runtimeSurface = previousSurface;
                _state.SurfaceReady = true;
                _state.LastSurfaceRevision = previousSurface.Revision;
                return false;
            }

            _state.Grid = request.SurfaceSettings.Grid;
            _lastObservedGrid = _state.Grid;
            _hasObservedGrid = true;
            _state.Workspace.Resize(_state.Grid.CellCount);
            _state.BaseComposed = false;
            _state.LastComposedRegion = FlowFieldCellRect.Invalid;
            _state.Surface = surface;
            _state.SurfaceReady = true;
            _state.LastSurfaceRevision = surface.Revision;
            if (boundsChanged)
                _state.HasObstacleMask = false;
            _state.DirtyFlags |= FlowFieldDirtyFlags.StaticObstacles
                | FlowFieldDirtyFlags.DynamicObstacles
                | FlowFieldDirtyFlags.Escape
                | FlowFieldDirtyFlags.DefaultDirection
                | FlowFieldDirtyFlags.Goal
                | FlowFieldDirtyFlags.ModifierArea
                | FlowFieldDirtyFlags.FinalRegion;
            _state.DirtyFinalRegion = FlowFieldCellRect.Full(_state.Grid);
            _state.DirtyObstacleRegion = FlowFieldCellRect.Full(_state.Grid);
            _modifierRegistry?.MarkAllAreasDirty();
            return true;
        }

        private void BuildStaticSnapshot(in FlowFieldSessionRequest request, FlowFieldDirtyFlags pending)
        {
            if (request.StaticBakeSnapshot == null)
                throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");

            bool reloadSnapshot = !_fieldStore.IsValid
                || !_state.SurfaceReady
                || !_state.Grid.MatchesBounds(request.SurfaceSettings.Grid)
                || !ReferenceEquals(_loadedStaticBakeSnapshot, request.StaticBakeSnapshot)
                || _loadedStaticBakeRevision != request.StaticBakeSnapshot.Revision;
            if (reloadSnapshot)
            {
                if (!request.StaticBakeSnapshot.HasValidData)
                    throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");
                // Static runtime never runs a Physics obstacle query or a BFS
                // backend. Loading the asset is the base-stage equivalent and
                // is only needed for the first build or when the referenced
                // snapshot/signature changed.
                request.StaticBakeSnapshot.CopyToWorkspace(_state.Grid, _state.Workspace);
                _loadedStaticBakeSnapshot = request.StaticBakeSnapshot;
                _loadedStaticBakeRevision = request.StaticBakeSnapshot.Revision;
                _state.HasObstacleMask = true;
                _state.BaseComposed = false;
                _state.ResolvedDefaultDirection =
                    FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection);
            }
            else if ((pending & FlowFieldDirtyFlags.DefaultDirection) != 0)
            {
                _state.ResolvedDefaultDirection =
                    FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection);
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }

            bool modifierAreaChanged = false;
            FlowFieldCellRect modifierRegion = FlowFieldCellRect.Invalid;
            if ((pending & FlowFieldDirtyFlags.ModifierArea) != 0 || reloadSnapshot)
            {
                RebuildModifierAreaData(request, out modifierAreaChanged, out modifierRegion);
                if (modifierAreaChanged)
                {
                    pending |= FlowFieldDirtyFlags.FinalRegion;
                    _state.ExpandFinalDirty(
                        modifierRegion.IsValid
                            ? modifierRegion
                            : FlowFieldCellRect.Full(_state.Grid));
                }
            }

            // Default Direction is not serialized in a static snapshot. It
            // therefore changes the cached BaseDirection/Speed arrays even
            // though it never requires Physics or BFS; rebuild the base
            // vectors before applying the final modifier stack.
            bool rebuildBase = reloadSnapshot
                || !_state.BaseComposed
                || (pending & FlowFieldDirtyFlags.DefaultDirection) != 0;
            bool finalDirty = rebuildBase
                || (pending & (FlowFieldDirtyFlags.FinalRegion
                    | FlowFieldDirtyFlags.ModifierArea
                    | FlowFieldDirtyFlags.ModifierValue
                    | FlowFieldDirtyFlags.DefaultDirection)) != 0;
            if (rebuildBase)
            {
                ComposeFinal(request, rebuildBase: true);
            }
            else if (finalDirty)
            {
                ComposeFinal(request, rebuildBase: false);
            }

            Commit(
                request,
                resultChanged: finalDirty,
                includeBase: rebuildBase,
                finalRegion: _state.LastComposedRegion);
        }

        private void BuildSceneBase(
            in FlowFieldSessionRequest request,
            ref FlowFieldDirtyFlags pending,
            bool surfaceChanged)
        {
            if ((pending & FlowFieldDirtyFlags.DefaultDirection) != 0)
            {
                _state.ResolvedDefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection);
                pending |= FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }

            bool rebuildStatic = (pending & FlowFieldDirtyFlags.StaticObstacles) != 0;
            bool rebuildDynamic = (pending & FlowFieldDirtyFlags.DynamicObstacles) != 0;
            bool rebuildGoal = (pending & FlowFieldDirtyFlags.Goal) != 0;
            if (!_state.HasObstacleMask)
            {
                // A direct Goal request is allowed before the first obstacle
                // preparation (notably in injected/test sessions). Ensure the
                // solver never consumes the zero-initialized mask.
                rebuildStatic = true;
                rebuildDynamic = true;
            }
            if (!rebuildStatic && !rebuildDynamic && !rebuildGoal)
                return;

            bool hadObstacleMask = _state.HasObstacleMask;
            FlowFieldGoalResolution goal = request.Goal;
            bool goalChanged = rebuildGoal && _goalTracker.HasChanged(goal);
            FlowFieldBuildResult prepared = FlowFieldBuildPipeline.PrepareBase(
                new FlowFieldBuildRequest(
                    _state.Grid,
                    _state.Surface,
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
                        goal,
                        pending,
                        Mathf.Min(_state.Grid.CellCount, Mathf.Max(64, request.MaxGpuWaves)),
                        _requestVersion,
                        surfaceChanged),
                _obstaclePipeline,
                _goalTracker,
                rebuildStatic,
                rebuildDynamic,
                rebuildGoal);

            _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            if (rebuildStatic || rebuildDynamic)
                _state.HasObstacleMask = true;
            if ((rebuildStatic || rebuildDynamic)
                && hadObstacleMask
                && !prepared.ObstacleMaskChanged)
            {
                // A probe that produces the same effective mask is not a
                // base change. Drop the request-only obstacle flags so no
                // BFS, Revision or FieldChanged event is generated.
                pending &= ~(FlowFieldDirtyFlags.StaticObstacles
                    | FlowFieldDirtyFlags.DynamicObstacles
                    | FlowFieldDirtyFlags.Escape);
                if ((pending & ~(FlowFieldDirtyFlags.StaticObstacles
                        | FlowFieldDirtyFlags.DynamicObstacles
                        | FlowFieldDirtyFlags.Escape
                        | FlowFieldDirtyFlags.FinalRegion)) == FlowFieldDirtyFlags.None)
                {
                    pending &= ~FlowFieldDirtyFlags.FinalRegion;
                    _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                }
            }
            if (rebuildStatic || rebuildDynamic)
            {
                // The obstacle API deliberately does not advance either
                // version until this comparison has established that the
                // effective mask changed.  This keeps a registration,
                // transform notification or probe that produces the same
                // mask completely invisible to BFS and consumers.
                if (prepared.ObstacleMaskChanged)
                    PromoteDeferredObstacleVersion();
                else
                    _deferredObstacleVersion = false;
            }
            if (rebuildGoal)
            {
                if (goalChanged)
                    PromoteDeferredGoalVersion();
                else
                    _deferredGoalVersion = false;
            }
            if (prepared.ObstacleMaskChanged || !hadObstacleMask)
            {
                if (!prepared.ObstacleMaskChanged)
                    _obstaclePipeline.CommitCombinedAndBuildEscape(
                        _state.Grid,
                        _state.Surface,
                        _state.Workspace,
                        out _);
                _state.ExpandObstacleDirty(prepared.ObstacleDirtyRegion);
                UpdateBlockedWarning(prepared.HasWalkableSurface);
                pending |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }
            if (prepared.GoalStatus == FlowFieldGoalBuildStatus.NoWalkableSurface
                && _goalTracker.TryConsumeMissingWalkableWarning())
            {
                Debug.LogWarning("[FlowField] Goal 범위에 이동 가능한 표면 셀이 없습니다.");
            }
            if (rebuildGoal && prepared.GoalStatus == FlowFieldGoalBuildStatus.Unchanged)
            {
                // A repeated Goal request can be produced by an explicit
                // rebuild call even though the resolved source cell and
                // influence radius are identical. The prepared workspace is
                // already the committed Goal graph, so make this a true
                // no-op instead of launching another BFS.
                pending &= ~FlowFieldDirtyFlags.Goal;
                if ((pending & ~(FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion))
                    == FlowFieldDirtyFlags.None)
                {
                    pending &= ~FlowFieldDirtyFlags.FinalRegion;
                    _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                }
            }
            if ((rebuildGoal && prepared.Delta.GoalChanged)
                || prepared.ObstacleMaskChanged)
            {
                pending |= FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
            }
        }

        private void PromoteDeferredObstacleVersion()
        {
            if (!_deferredObstacleVersion)
                return;
            unchecked
            {
                _requestVersion++;
                _baseVersion++;
            }
            _deferredObstacleVersion = false;
        }

        private void PromoteDeferredSurfaceVersion()
        {
            if (!_deferredSurfaceVersion)
                return;
            unchecked { _baseVersion++; }
            _deferredSurfaceVersion = false;
        }

        private void PromoteDeferredGoalVersion()
        {
            if (!_deferredGoalVersion)
                return;
            unchecked { _baseVersion++; }
            _deferredGoalVersion = false;
        }

        private bool RebuildModifierAreaData(
            in FlowFieldSessionRequest request,
            out bool changed,
            out FlowFieldCellRect changedRegion)
        {
            changed = false;
            changedRegion = FlowFieldCellRect.Invalid;
            if (_modifierPipeline == null)
                return false;
            return _modifierPipeline.RebuildAreaData(
                new FlowFieldModifierBuildRequest(
                    _state.Grid,
                    _state.Surface,
                    _state.Workspace,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset),
                out changed,
                out changedRegion);
        }

        private void ComposeFinal(in FlowFieldSessionRequest request, bool rebuildBase = false)
        {
            if (_modifierPipeline == null)
                return;
            _modifierPipeline.RebuildFinalField(
                new FlowFieldModifierBuildRequest(
                    _state.Grid,
                    _state.Surface,
                    _state.Workspace,
                    request.ObstacleCheckHeight,
                    request.ObstacleCheckCenterOffset),
                _state.ResolvedDefaultDirection,
                _state.DirtyFinalRegion.IsValid
                    ? _state.DirtyFinalRegion
                    : FlowFieldCellRect.Full(_state.Grid),
                rebuildBase);
            _state.LastComposedRegion = rebuildBase
                ? FlowFieldCellRect.Full(_state.Grid)
                : _state.DirtyFinalRegion.IsValid
                    ? _state.DirtyFinalRegion
                    : FlowFieldCellRect.Full(_state.Grid);
            _state.BaseComposed |= rebuildBase;
            _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
        }

        private bool StartBfs(in FlowFieldSessionRequest request)
        {
            if (!_state.Workspace.HasActiveGoal)
            {
                ComposeFinal(request, rebuildBase: true);
                Commit(request, resultChanged: true, includeBase: true, finalRegion: FlowFieldCellRect.Full(_state.Grid));
                return true;
            }

            FlowFieldBfsRequest bfsRequest = new FlowFieldBfsRequest(
                _state.Grid,
                _state.Surface,
                _state.Workspace,
                true,
                request.Goal.LocalX,
                request.Goal.LocalZ,
                request.Goal.InfluenceRadius,
                _state.Workspace.ResolvedGoalIndex,
                Mathf.Min(_state.Grid.CellCount, Mathf.Max(64, request.MaxGpuWaves)),
                _requestVersion);

            if (_backendPolicy == FlowFieldBfsBackendPolicy.RequireGpu
                && (_buildPipeline == null || !_buildPipeline.SupportsGpu))
                throw new PlatformNotSupportedException("FlowField GPU backend is required but unavailable.");

            _activeBaseVersion = _baseVersion;
            _activeRequestVersion = _requestVersion;
            _activeRequest = request;
            SetLifecycle(FlowFieldSessionLifecycle.Building);
            int callbackGeneration = _callbackGeneration;
            bool accepted;
            if (_backendPolicy == FlowFieldBfsBackendPolicy.ManagedOnly
                || _managedBackendForced)
            {
                accepted = FlowFieldBuildPipeline.BuildManaged(bfsRequest);
                if (accepted)
                    OnBfsCompleted(bfsRequest);
                return accepted;
            }

            if (_buildPipeline == null)
                throw new InvalidOperationException("FlowField BFS backend is not initialized.");

            accepted = _buildPipeline.StartBfs(
                bfsRequest,
                completed =>
                {
                    if (callbackGeneration != _callbackGeneration
                        || _lifecycle != FlowFieldSessionLifecycle.Building)
                        return;
                    try { OnBfsCompleted(completed); }
                    catch (Exception exception) { Fail(exception); }
                },
                (failed, exception) =>
                {
                    if (callbackGeneration != _callbackGeneration
                        || _lifecycle != FlowFieldSessionLifecycle.Building)
                        return;
                    try { OnBfsFailed(failed, exception); }
                    catch (Exception callbackException) { Fail(callbackException); }
                },
                allowManagedFallback: _backendPolicy != FlowFieldBfsBackendPolicy.RequireGpu);
            if (!accepted)
            {
                // A RequireGpu backend may synchronously invoke the failure
                // callback before returning false. In that case the callback
                // already transitioned the Session to Faulted; avoid
                // replacing its diagnostic with a second generic exception.
                if (_lifecycle == FlowFieldSessionLifecycle.Faulted)
                    return false;
                throw new InvalidOperationException("FlowField BFS session could not be started.");
            }
            return true;
        }

        private void OnBfsCompleted(FlowFieldBfsRequest request)
        {
            if (_lifecycle != FlowFieldSessionLifecycle.Building)
                return;
            if (_buildPipeline is FlowFieldBuildPipeline productionPipeline
                && productionPipeline.GpuDisabled)
                _managedBackendForced = true;
            bool baseStale = _activeBaseVersion != _baseVersion;
            if (baseStale)
            {
                bool obstacleStale = HasStaleObstacleInput();
                RestoreStagingAfterStaleResult(preserveObstacle: !obstacleStale);
                if (obstacleStale)
                {
                    _state.HasObstacleMask = false;
                    _state.DirtyFlags |= FlowFieldDirtyFlags.StaticObstacles
                        | FlowFieldDirtyFlags.DynamicObstacles
                        | FlowFieldDirtyFlags.Escape;
                    _state.ExpandObstacleDirty(FlowFieldCellRect.Full(_state.Grid));
                }
                _state.DirtyFlags |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
                if (_hasPendingRequest)
                    BuildPending();
                else
                    SetLifecycle(FlowFieldSessionLifecycle.Active);
                return;
            }

            // A dynamic obstacle can be registered or moved while the GPU
            // solve is in flight.  Its request is intentionally unversioned
            // until the physics probe compares the effective mask, so the
            // version numbers above can still match even though a new base
            // dirty flag is pending.  Never commit the old BFS result in
            // that case; keep the dirty request for the next FixedUpdate.
            if (IsBaseDirty(_state.DirtyFlags))
            {
                RestoreStagingAfterStaleResult(preserveObstacle: true);
                if (_hasPendingRequest)
                    BuildPending();
                else
                    SetLifecycle(FlowFieldSessionLifecycle.Active);
                return;
            }

            // A collider transform can be observed while the GPU is running
            // before the deferred physics probe has compared its effective
            // mask. Do not publish a field that was computed against the old
            // collider pose; the probe will either clear this flag (same
            // mask) or promote the change to the next BFS request.
            if (_obstacleProbePending)
            {
                RestoreStagingAfterStaleResult(preserveObstacle: true);
                SetLifecycle(FlowFieldSessionLifecycle.Active);
                return;
            }

            bool hasNewRequest = _hasPendingRequest;
            FlowFieldSessionRequest finalRequest = hasNewRequest ? _pendingRequest : _activeRequest;
            FlowFieldDirtyFlags pending = _state.DirtyFlags;
            _hasPendingRequest = false;

            // RequestVersion also advances for Default Direction and Modifier
            // edits. Those changes are final-only and must never invalidate a
            // valid in-flight base solve (a Goal/obstacle result would be
            // lost if we restored the previous committed workspace here).
            // Apply the newest scalar input to the freshly solved base before
            // committing. If the façade has not submitted a newer request
            // yet, the active request is still a valid base result and the
            // next final-only request will recompose it without another BFS.
            Vector3 latestDefaultDirection = _hasLatestDefaultDirection
                ? _latestDefaultDirection
                : finalRequest.DefaultDirection;
            if (FlowFieldGridSpace.IsFinite(latestDefaultDirection)
                && latestDefaultDirection.sqrMagnitude
                    > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
            {
                Vector3 resolvedDefault = FlowFieldVectorUtility.NormalizeDefaultDirection(
                    latestDefaultDirection);
                if ((_state.ResolvedDefaultDirection - resolvedDefault).sqrMagnitude
                    > 0.0001f * 0.0001f)
                    _state.ResolvedDefaultDirection = resolvedDefault;
            }
            if ((pending & FlowFieldDirtyFlags.ModifierArea) != 0)
            {
                RebuildModifierAreaData(finalRequest, out _, out _);
            }
            ComposeFinal(finalRequest, rebuildBase: true);
            Commit(finalRequest, resultChanged: true, includeBase: true, finalRegion: _state.LastComposedRegion);
            // Commit publishes the Building state while the old field remains
            // the sampling source. Transition to Ready only after the field
            // event has returned; editor bake callbacks may dispose this
            // short-lived Session from inside FieldCommitted.
            if (!_disposed && _lifecycle == FlowFieldSessionLifecycle.Building)
                SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        private void OnBfsFailed(FlowFieldBfsRequest request, Exception exception)
        {
            if (_lifecycle != FlowFieldSessionLifecycle.Building)
                return;
            if (_backendPolicy == FlowFieldBfsBackendPolicy.PreferGpu)
                _managedBackendForced = true;
            if (_activeBaseVersion != _baseVersion)
            {
                bool obstacleStale = HasStaleObstacleInput();
                RestoreStagingAfterStaleResult(preserveObstacle: !obstacleStale);
                if (obstacleStale)
                {
                    _state.HasObstacleMask = false;
                    _state.DirtyFlags |= FlowFieldDirtyFlags.StaticObstacles
                        | FlowFieldDirtyFlags.DynamicObstacles
                        | FlowFieldDirtyFlags.Escape;
                    _state.ExpandObstacleDirty(FlowFieldCellRect.Full(_state.Grid));
                }
                _state.DirtyFlags |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
                if (_hasPendingRequest)
                    BuildPending();
                else
                    SetLifecycle(FlowFieldSessionLifecycle.Active);
                return;
            }
            if (IsBaseDirty(_state.DirtyFlags))
            {
                RestoreStagingAfterStaleResult(preserveObstacle: true);
                if (_hasPendingRequest)
                    BuildPending();
                else
                    SetLifecycle(FlowFieldSessionLifecycle.Active);
                return;
            }
            if (_obstacleProbePending)
            {
                RestoreStagingAfterStaleResult(preserveObstacle: true);
                SetLifecycle(FlowFieldSessionLifecycle.Active);
                return;
            }
            // A failed base solve cannot be replaced by a final-only compose:
            // unlike the successful callback there is no freshly solved Goal
            // graph to preserve. Requeue the Goal stage whenever the request
            // changed while the backend was running (including a pending
            // Default Direction/Modifier request), then retry the same base
            // inputs without exposing a partial result.
            if (_activeRequestVersion != _requestVersion || _hasPendingRequest)
            {
                RestoreStagingAfterStaleResult(preserveObstacle: true);
                _state.DirtyFlags |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
                _state.ExpandFinalDirty(FlowFieldCellRect.Full(_state.Grid));
                if (_hasPendingRequest)
                {
                    _state.DirtyFlags |= FlowFieldDirtyFlags.Goal;
                    BuildPending();
                }
                else
                {
                    SetLifecycle(FlowFieldSessionLifecycle.Active);
                }
                return;
            }
            Fail(exception ?? new InvalidOperationException("Managed FlowField BFS failed."));
        }

        private bool HasStaleObstacleInput()
            => (_state.DirtyFlags & (FlowFieldDirtyFlags.Grid
                | FlowFieldDirtyFlags.StaticObstacles
                | FlowFieldDirtyFlags.DynamicObstacles
                | FlowFieldDirtyFlags.Escape)) != 0;

        private void RestoreStagingAfterStaleResult(bool preserveObstacle = false)
        {
            bool sameSignature = _fieldStore.IsValid
                && _state.Workspace.Capacity == _fieldStore.Capacity
                && _fieldStore.Grid.MatchesBounds(_state.Grid)
                && _fieldStore.Surface != null
                && _state.Surface != null
                && SurfacesEqual(_fieldStore.Surface, _state.Surface, _state.Grid);
            if (sameSignature)
            {
                if (preserveObstacle)
                    _fieldStore.CopyGoalAndFieldToWorkspace(_state.Workspace);
                else
                    _fieldStore.CopyToWorkspace(_state.Workspace);
            }
            else if (_state.Workspace.Capacity > 0)
            {
                _state.Workspace.ClearAll();
                _state.DirtyFlags |= FlowFieldDirtyFlags.All;
                _state.DirtyFinalRegion = FlowFieldCellRect.Full(_state.Grid);
                _state.DirtyObstacleRegion = FlowFieldCellRect.Full(_state.Grid);
            }
        }

        private void Commit(
            in FlowFieldSessionRequest request,
            bool resultChanged,
            bool includeBase,
            FlowFieldCellRect finalRegion)
        {
            if (!_state.SurfaceReady || !_state.Grid.IsValid)
                throw new InvalidOperationException("Cannot commit a FlowField without a valid staging surface.");
            bool actualChanged = false;

            if (_committedSurface == null
                || !ReferenceEquals(_committedSourceSurface, _state.Surface)
                || _committedSurfaceRevision != _state.Surface.Revision
                || !SurfacesEqual(_committedSurface, _state.Surface, _state.Grid))
            {
                _committedSurface = _state.Surface;
                _committedSourceSurface = _state.Surface;
                _committedSurfaceRevision = _state.Surface.Revision;
            }

            if (resultChanged)
            {
                _fieldStore.CommitFromWorkspace(
                    _state.Grid,
                    _state.Surface,
                    _state.Workspace,
                    includeBase,
                    finalRegion.IsValid ? finalRegion : FlowFieldCellRect.Full(_state.Grid),
                    out actualChanged);
            }
            _committedGrid = _state.Grid;
            _configurationStale = false;
            PublishRuntimeState();
            if (!_hasPendingRequest)
            {
                _state.DirtyFlags = FlowFieldDirtyFlags.None;
                _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
                _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            }
            if (actualChanged)
            {
                unchecked
                {
                    _revision++;
                }
                FieldCommitted?.Invoke(true);
            }
        }

        private void UpdateBlockedWarning(bool hasWalkableCell)
        {
            if (!hasWalkableCell && !_obstaclePipeline.AllBlockedWarningIssued)
            {
                _obstaclePipeline.AllBlockedWarningIssued = true;
                Debug.LogWarning("[FlowField] 모든 유효한 Surface 셀이 장애물로 막혔습니다.");
            }
            else if (hasWalkableCell)
            {
                _obstaclePipeline.AllBlockedWarningIssued = false;
            }
        }

        private static bool SurfacesEqual(
            FlowFieldSurfaceData left,
            FlowFieldSurfaceData right,
            FlowFieldGridSpace grid)
            => ReferenceEquals(left, right)
                || left != null
                && right != null
                && left.ContentEquals(right);

        private void Fail(Exception exception)
        {
            _fault = exception;
            SetLifecycle(FlowFieldSessionLifecycle.Faulted);
            _state.DirtyFlags = FlowFieldDirtyFlags.None;
            Failed?.Invoke(exception);
        }

        internal void Suspend()
        {
            if (_disposed
                || _lifecycle == FlowFieldSessionLifecycle.Uninitialized
                || _lifecycle == FlowFieldSessionLifecycle.Released
                || _lifecycle == FlowFieldSessionLifecycle.Faulted
                || _lifecycle == FlowFieldSessionLifecycle.Suspended)
                return;
            unchecked
            {
                _callbackGeneration++;
            }
            DisposeBackendForSuspend();
            _buildPipeline = null;
            _runtimeSurface = null;
            _committedSurface = null;
            _committedSourceSurface = null;
            _committedSurfaceRevision = -1;
            _committedGrid = default;
            _loadedStaticBakeSnapshot = null;
            _loadedStaticBakeRevision = -1;
            _fieldStore.Clear();
            _state.SurfaceReady = false;
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
            _deferredGoalVersion = false;
            _state.DirtyFlags = FlowFieldDirtyFlags.All;
            _configurationStale = false;
            SetLifecycle(FlowFieldSessionLifecycle.Suspended);
        }

        internal void Release()
        {
            if (_disposed)
                return;
            unchecked
            {
                _callbackGeneration++;
            }
            DisposeBackendForRelease();
            _modifierPipeline?.Clear();
            _modifierRegistry?.Clear();
            _modifierPipeline = null;
            _modifierRegistry = null;
            _obstaclePipeline.ClearDynamicObstacles();
            _goalTracker.Clear();
            _runtimeSurface = null;
            _committedSurface = null;
            _committedSourceSurface = null;
            _committedSurfaceRevision = -1;
            _committedGrid = default;
            _loadedStaticBakeSnapshot = null;
            _loadedStaticBakeRevision = -1;
            _fieldStore.Clear();
            _state.Release();
            _state.Grid = default;
            _state.Surface = null;
            _state.SurfaceReady = false;
            _state.HasObstacleMask = false;
            _state.BaseComposed = false;
            _state.LastComposedRegion = FlowFieldCellRect.Invalid;
            _state.DirtyFlags = FlowFieldDirtyFlags.All;
            _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
            _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            _state.ResolvedDefaultDirection = Vector3.zero;
            _state.LastSurfaceRevision = -1;
            _state.LastStaticObstacleRevision = -1;
            _lastObservedGrid = default;
            _hasObservedGrid = false;
            _hasPendingRequest = false;
            _hasLastRequest = false;
            _obstacleProbePending = false;
            _deferredObstacleVersion = false;
            _deferredSurfaceVersion = false;
            _deferredGoalVersion = false;
            _versionedDirtyFlags = FlowFieldDirtyFlags.None;
            _managedBackendForced = false;
            _latestDefaultDirection = Vector3.zero;
            _hasLatestDefaultDirection = false;
            _fault = null;
            SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        internal void DisposePermanently()
        {
            if (_disposed)
                return;
            Release();
            // A directly injected backend is deliberately kept alive across
            // Release so the same Session can be initialized again. Permanent
            // disposal is the ownership boundary where it is finally freed.
            if (_injectedBfsBackend != null)
                _injectedBfsBackend.Dispose();
            _disposed = true;
            SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        public void Dispose()
            => DisposePermanently();

        private static bool IsBaseDirty(FlowFieldDirtyFlags flags)
            => (flags & BaseDirtyMask) != 0;

        private const FlowFieldDirtyFlags BaseDirtyMask = FlowFieldDirtyFlags.Grid
                | FlowFieldDirtyFlags.StaticObstacles
                | FlowFieldDirtyFlags.DynamicObstacles
                | FlowFieldDirtyFlags.Escape
                | FlowFieldDirtyFlags.Goal;

        private void DisposeBackendForSuspend()
        {
            if (_buildPipeline == null)
                return;
            if (ReferenceEquals(_buildPipeline, _injectedBfsBackend))
            {
                // Test/custom backends are caller-owned and may be reused by
                // a later Initialize after Suspend. Their callbacks are
                // already invalidated by the generation bump above.
                _buildPipeline = null;
                return;
            }

            _buildPipeline.Dispose();
            _buildPipeline = null;
        }

        private void DisposeBackendForRelease()
        {
            if (_buildPipeline == null)
                return;
            if (ReferenceEquals(_buildPipeline, _injectedBfsBackend))
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

        private void PublishRuntimeState()
        {
            FlowFieldRuntimeState next = RuntimeState;
            if (_publishedRuntimeState == next)
                return;
            _publishedRuntimeState = next;
            StateChanged?.Invoke(next);
        }
    }

    /// <summary>
    /// Managed backing for FlowField rebuild. GPU readback storage is owned by the
    /// compute solver; a Manager owns one staging workspace and one committed
    /// workspace so asynchronous builds cannot expose partially written data.
    /// </summary>
    internal sealed class FlowFieldWorkspace
    {
        public bool[] Blocked { get; private set; }
        public bool[] ObstacleScratch { get; private set; }
        public bool[] StaticBlocked { get; private set; }
        public bool[] DynamicBlocked { get; private set; }
        public Vector3[] EscapeDirections { get; private set; }
        public Vector3[] GoalDirections { get; private set; }
        public FlowFieldGoalFlags[] GoalFlags { get; private set; }
        public Vector3[] FinalDirections { get; private set; }
        public float[] FinalSpeedMultipliers { get; private set; }
        internal Vector3[] BaseDirections { get; private set; }
        internal float[] BaseSpeedMultipliers { get; private set; }
        public bool[] ModifierInfluence { get; private set; }
        internal bool[] InfluenceMask { get; private set; }
        internal int[] Costs { get; private set; }
        internal int[] Queue { get; private set; }
        internal int[] NextCells { get; private set; }
        // Final eight-neighbour topology after Surface, obstacle, influence and
        // diagonal-corner checks. Managed and GPU backends consume this same mask.
        internal byte[] TopologyMasks { get; private set; }
        internal int ResolvedGoalIndex { get; private set; } = -1;
        internal bool HasActiveGoal { get; private set; }

        public int Capacity => Blocked == null ? 0 : Blocked.Length;
        public bool HasBlockedCells { get; private set; }

        public bool Resize(int cellCount)
        {
            if (cellCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellCount), cellCount, "Workspace capacity must be positive.");

            if (Capacity == cellCount)
                return false;

            Release();
            Blocked = new bool[cellCount];
            ObstacleScratch = new bool[cellCount];
            StaticBlocked = new bool[cellCount];
            DynamicBlocked = new bool[cellCount];
            EscapeDirections = new Vector3[cellCount];
            GoalDirections = new Vector3[cellCount];
            GoalFlags = new FlowFieldGoalFlags[cellCount];
            FinalDirections = new Vector3[cellCount];
            FinalSpeedMultipliers = new float[cellCount];
            BaseDirections = new Vector3[cellCount];
            BaseSpeedMultipliers = new float[cellCount];
            ModifierInfluence = new bool[cellCount];
            InfluenceMask = new bool[cellCount];
            Costs = new int[cellCount];
            Queue = new int[cellCount];
            NextCells = new int[cellCount];
            TopologyMasks = new byte[cellCount];
            for (int i = 0; i < cellCount; i++)
                NextCells[i] = -1;
            ResolvedGoalIndex = -1;
            HasActiveGoal = false;
            return true;
        }

        public void Release()
        {
            Blocked = null;
            ObstacleScratch = null;
            StaticBlocked = null;
            DynamicBlocked = null;
            EscapeDirections = null;
            GoalDirections = null;
            GoalFlags = null;
            FinalDirections = null;
            FinalSpeedMultipliers = null;
            BaseDirections = null;
            BaseSpeedMultipliers = null;
            ModifierInfluence = null;
            InfluenceMask = null;
            Costs = null;
            Queue = null;
            NextCells = null;
            TopologyMasks = null;
            ResolvedGoalIndex = -1;
            HasActiveGoal = false;
            HasBlockedCells = false;
        }

        public void CommitObstacleScratch()
            => Array.Copy(ObstacleScratch, Blocked, Blocked.Length);

        internal void CopyFrom(FlowFieldWorkspace source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (Capacity <= 0 || source.Capacity != Capacity)
                throw new ArgumentException("Workspace capacities must match.", nameof(source));

            Array.Copy(source.Blocked, Blocked, Capacity);
            Array.Copy(source.ObstacleScratch, ObstacleScratch, Capacity);
            Array.Copy(source.StaticBlocked, StaticBlocked, Capacity);
            Array.Copy(source.DynamicBlocked, DynamicBlocked, Capacity);
            Array.Copy(source.EscapeDirections, EscapeDirections, Capacity);
            Array.Copy(source.GoalDirections, GoalDirections, Capacity);
            Array.Copy(source.GoalFlags, GoalFlags, Capacity);
            Array.Copy(source.FinalDirections, FinalDirections, Capacity);
            Array.Copy(source.FinalSpeedMultipliers, FinalSpeedMultipliers, Capacity);
            Array.Copy(source.BaseDirections, BaseDirections, Capacity);
            Array.Copy(source.BaseSpeedMultipliers, BaseSpeedMultipliers, Capacity);
            Array.Copy(source.ModifierInfluence, ModifierInfluence, Capacity);
            Array.Copy(source.InfluenceMask, InfluenceMask, Capacity);
            Array.Copy(source.Costs, Costs, Capacity);
            Array.Copy(source.Queue, Queue, Capacity);
            Array.Copy(source.NextCells, NextCells, Capacity);
            Array.Copy(source.TopologyMasks, TopologyMasks, Capacity);
            HasBlockedCells = source.HasBlockedCells;
            ResolvedGoalIndex = source.ResolvedGoalIndex;
            HasActiveGoal = source.HasActiveGoal;
        }

        public void RebuildCombinedBlocked()
        {
            int count = Capacity;
            HasBlockedCells = false;
            for (int i = 0; i < count; i++)
            {
                Blocked[i] = StaticBlocked[i] || DynamicBlocked[i];
                HasBlockedCells |= Blocked[i];
            }
        }

        public void ClearGoal()
        {
            if (GoalDirections == null)
                return;

            Array.Clear(GoalDirections, 0, GoalDirections.Length);
            Array.Clear(GoalFlags, 0, GoalFlags.Length);
            Array.Clear(InfluenceMask, 0, InfluenceMask.Length);
            Array.Clear(Costs, 0, Costs.Length);
            for (int i = 0; i < NextCells.Length; i++)
                NextCells[i] = -1;
            ResolvedGoalIndex = -1;
            HasActiveGoal = false;
        }

        internal void SetResolvedGoal(int index)
        {
            if (index < 0 || index >= Capacity)
                throw new ArgumentOutOfRangeException(nameof(index));
            ResolvedGoalIndex = index;
            HasActiveGoal = true;
        }

        internal void LoadBakedGoal(
            bool hasGoal,
            int resolvedGoalIndex,
            int[] nextCells)
        {
            if (nextCells == null || nextCells.Length != Capacity)
                throw new ArgumentException("Baked Goal NextCell data must match the workspace capacity.", nameof(nextCells));
            if (hasGoal && (resolvedGoalIndex < 0 || resolvedGoalIndex >= Capacity))
                throw new ArgumentOutOfRangeException(nameof(resolvedGoalIndex));

            Array.Clear(GoalFlags, 0, GoalFlags.Length);
            Array.Clear(InfluenceMask, 0, InfluenceMask.Length);
            for (int index = 0; index < Capacity; index++)
            {
                if (!hasGoal)
                    continue;

                int next = nextCells[index];
                if (next >= 0)
                {
                    InfluenceMask[index] = true;
                    GoalFlags[index] = FlowFieldGoalFlags.Directed;
                    if (next == index)
                        GoalFlags[index] |= FlowFieldGoalFlags.Anchor;
                }
                else if (next == -3)
                {
                    InfluenceMask[index] = true;
                    GoalFlags[index] = FlowFieldGoalFlags.Unreachable;
                }
            }

            HasActiveGoal = hasGoal;
            ResolvedGoalIndex = hasGoal ? resolvedGoalIndex : -1;
        }

        public void ClearAll()
        {
            if (Capacity <= 0)
                return;

            Array.Clear(Blocked, 0, Capacity);
            Array.Clear(ObstacleScratch, 0, Capacity);
            Array.Clear(StaticBlocked, 0, Capacity);
            Array.Clear(DynamicBlocked, 0, Capacity);
            HasBlockedCells = false;
            Array.Clear(EscapeDirections, 0, Capacity);
            ClearGoal();
            Array.Clear(TopologyMasks, 0, Capacity);
            Array.Clear(BaseDirections, 0, Capacity);
            Array.Clear(BaseSpeedMultipliers, 0, Capacity);
            Array.Clear(FinalDirections, 0, Capacity);
            Array.Clear(FinalSpeedMultipliers, 0, Capacity);
            Array.Clear(ModifierInfluence, 0, Capacity);
            ResolvedGoalIndex = -1;
            HasActiveGoal = false;
        }
    }
}
