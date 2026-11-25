using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Why a clamp produced a position or not.</summary>
    internal enum FlowFieldClampStatus
    {
        Clamped,
        NotInitialized,
        NonFinite,
        // No request has laid out a grid yet.
        NoGrid,
    }

    /// <summary>What one step of a build left it doing.</summary>
    internal enum FlowFieldBuildStep
    {
        // More work remains and the budget ran out.
        Yield,
        // A worker thread is solving; nothing to do until it finishes.
        Waiting,
        // Newer input made the result useless; nothing is published.
        Rejected,
        // Ready to publish.
        Completed,
    }

    /// <summary>
    /// The FlowField pipeline, the same for both space modes. It owns the
    /// lifecycle, the published state, the fault sequence, command deferral,
    /// the build loop and the generation that retires queued events and late
    /// solve callbacks. What differs between Surface2D and Volume3D lives in
    /// its <see cref="IFlowFieldSpaceBuilder"/>.
    /// </summary>
    internal sealed class FlowFieldSession : IDisposable
    {
        internal readonly FlowFieldExecutionBoundary Boundary;
        private readonly IFlowFieldSpaceBuilder _builder;
        private FlowFieldSafety _safety;
        // The settings the latest request was built from; the uncertain-region
        // margin uses their Obstacle Clearance.
        private FlowFieldManagerConfig _config;
        private bool _hasConfig;
        // Set once Dispose published its last event; nothing publishes after it.
        private bool _publishingClosed;
        private bool _runtimeScheduled;
        private FlowFieldRuntimeState _publishedState = FlowFieldRuntimeState.Uninitialized;
        private readonly FlowFieldObstacleSweep _sweep;

        internal FlowFieldSession(
            FlowFieldExecutionBoundary boundary,
            Func<FlowFieldSession, IFlowFieldSpaceBuilder> createBuilder)
        {
            Boundary = boundary ?? new FlowFieldExecutionBoundary();
            _builder = createBuilder?.Invoke(this)
                ?? throw new ArgumentNullException(nameof(createBuilder));
            _sweep = new FlowFieldObstacleSweep(ApplySweptChange);
        }

        internal static FlowFieldSession CreateSurface(FlowFieldExecutionBoundary boundary = null)
            => new FlowFieldSession(boundary, session => new FlowFieldSurfaceBuilder(session, null));

        internal static FlowFieldSession CreateVolume(FlowFieldExecutionBoundary boundary = null)
            => new FlowFieldSession(boundary, session => new FlowFieldVolumeBuilder(session));

        /// <summary>
        /// An isolated Surface2D bake session on the ground the bake scanned.
        /// The bake reads its export from <paramref name="builder"/>; its
        /// input still goes through the session (<see cref="SubmitPrepared"/>).
        /// </summary>
        internal static FlowFieldSession CreateSurfaceBake(IFlowFieldSurfaceSource ground, out FlowFieldSurfaceBuilder builder)
        {
            FlowFieldSurfaceBuilder created = null;
            var session = new FlowFieldSession(null, owner => created = new FlowFieldSurfaceBuilder(owner, ground));
            builder = created;
            return session;
        }

        /// <summary>An isolated Volume3D bake session; see <see cref="CreateSurfaceBake"/>.</summary>
        internal static FlowFieldSession CreateVolumeBake(out FlowFieldVolumeBuilder builder)
        {
            FlowFieldVolumeBuilder created = null;
            var session = new FlowFieldSession(null, owner => created = new FlowFieldVolumeBuilder(owner));
            builder = created;
            return session;
        }

        private FlowFieldModifierRegistry _modifiers;

        /// <summary>The registered Modifiers, the same in both modes.</summary>
        internal FlowFieldModifierRegistry Modifiers
            => _modifiers ??= new FlowFieldModifierRegistry(_builder.CreateModifierArea);

        /// <summary>The registered dynamic obstacles, the same in both modes.</summary>
        internal FlowFieldObstacleRegistry Obstacles { get; } = new FlowFieldObstacleRegistry();

        // Colliders unregistered while still in the scene, where they were and
        // in which frame (see RecheckReleasedColliders).
        private readonly System.Collections.Generic.List<ReleasedCollider> _releasedInScene
            = new System.Collections.Generic.List<ReleasedCollider>(4);

        private readonly struct ReleasedCollider
        {
            internal readonly Collider Collider;
            internal readonly Bounds Bounds;
            internal readonly int Frame;

            internal ReleasedCollider(Collider collider, Bounds bounds, int frame)
            {
                Collider = collider;
                Bounds = bounds;
                Frame = frame;
            }
        }

        /// <summary>
        /// Whether the unregistered obstacle sweep runs in this session: the
        /// settings enable it, the runtime scheduler pumps the session and too
        /// many Colliders have not stopped it. A bake and the Editor preview
        /// pump their sessions themselves, so they never sweep.
        /// </summary>
        internal bool SweepsObstacles => _runtimeScheduled && _sweep.IsRunning;

        /// <summary>Regions an obstacle change made uncertain. Surface2D compares them in XZ.</summary>
        internal FlowFieldSafety Safety
            => _safety ??= new FlowFieldSafety(planar: _builder.SpaceMode == FlowFieldSpaceMode.Surface2D);

        /// <summary>
        /// Passes the settings to the builder, which turns them into its request.
        /// A session that has not started or has faulted ignores them; false then.
        /// </summary>
        internal bool SubmitConfig(in FlowFieldManagerConfig config, FlowFieldConfigChange change)
        {
            if (!IsInitialized || IsFaulted)
                return false;
            _config = config;
            _hasConfig = true;
            // SetGoal submits too; only new sweep settings forget what it saw.
            _sweep.Configure(
                config.Obstacles.SweepUnregistered && BakeMode != FlowFieldBakeMode.StaticBaked,
                FlowFieldSpaceStrategy.For(SpaceMode).ObstacleSweepBounds(in config),
                config.Obstacles.Layer,
                config.Obstacles.SweepInterval);
            Bounds? invalidated = _builder.SubmitConfig(in config, change);
            if (invalidated.HasValue)
                Safety.InvalidateRegion(invalidated.Value);
            EnterBuildingIfOwed();
            // A new Grid can hide the published field without a lifecycle change.
            PublishRuntimeState();
            return true;
        }

        /// <summary>
        /// A bake submits the request it prepared itself (RuntimeDynamic on a
        /// fixed ground scan, every cell) through the same input rules as
        /// <see cref="SubmitConfig"/>: the uncertain region, Building and the
        /// runtime state. A bake runs no sweep, so there is nothing to configure.
        /// </summary>
        internal void SubmitPrepared(Func<Bounds?> submitToBuilder)
        {
            ThrowIfInputUnavailable();
            Bounds? invalidated = submitToBuilder();
            if (invalidated.HasValue)
                Safety.InvalidateRegion(invalidated.Value);
            EnterBuildingIfOwed();
            PublishRuntimeState();
        }

        // Accepted input is owed work: from that moment the session is Building,
        // the same in both modes, until a build publishes it. A stopped or
        // faulted session is not Active and keeps its state.
        private void EnterBuildingIfOwed()
        {
            if (Lifecycle == FlowFieldSessionLifecycle.Active
                && (_builder.HasPendingRequest || _builder.HasOwedInput))
                SetLifecycle(FlowFieldSessionLifecycle.Building);
        }

        // Building with nothing left to build and a field to read is Ready again.
        private void LeaveBuildingIfIdle()
        {
            if (Lifecycle == FlowFieldSessionLifecycle.Building
                && !_builder.HasActiveBuild
                && !_builder.HasOwedInput
                && _builder.PublishedField != null)
                SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        internal event Action FieldCommitted;
        internal event Action<Exception> Failed;
        internal event Action<FlowFieldRuntimeState> StateChanged;
        internal event Action<Exception> EventFailed;

        internal FlowFieldSpaceMode SpaceMode => _builder.SpaceMode;
        internal FlowFieldBakeMode BakeMode { get; private set; }
        internal FlowFieldSessionLifecycle Lifecycle { get; private set; }

        internal FlowFieldRuntimeState State => Lifecycle switch
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

        internal bool IsInitialized => Lifecycle == FlowFieldSessionLifecycle.Active
            || Lifecycle == FlowFieldSessionLifecycle.Building
            || Lifecycle == FlowFieldSessionLifecycle.Suspended;
        internal bool IsFaulted => Lifecycle == FlowFieldSessionLifecycle.Faulted;
        internal bool IsRebuilding => Lifecycle == FlowFieldSessionLifecycle.Building;
        internal bool CanPump => !Disposed
            && (Lifecycle == FlowFieldSessionLifecycle.Active || Lifecycle == FlowFieldSessionLifecycle.Building);
        // TrySample can succeed inside the grid.
        internal bool IsReady => CanPump && _builder.PublishedField != null;
        internal bool HasPendingRequest => _builder.HasPendingRequest;
        internal Exception Fault { get; private set; }
        internal string LastError => Fault?.Message;
        internal int Revision { get; private set; }
        internal bool Disposed { get; private set; }

        /// <summary>
        /// Counts on from the session this one replaces after a Space Mode
        /// change, so a Manager never shows the same Revision for two fields.
        /// </summary>
        internal void ContinueRevision(int revision) => Revision = revision;
        // Advances when the session stops being what earlier work was for:
        // Stop, Restart, Release and a fault, which must also drop a late
        // solve callback. Queued events and late solve callbacks
        // from before that point are dropped.
        internal int LifetimeGeneration { get; private set; }
        // The read both modes share; it never throws.
        private FlowFieldSampleStatus TrySampleStatus(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsReady)
                return FlowFieldSampleStatus.NotReady;
            FlowFieldField field = _builder.PublishedField;
            FlowFieldSampleStatus status = FlowFieldSampler.Sample(field, worldPosition, out sample, out int index);
            if (status == FlowFieldSampleStatus.Sampled
                && FlowFieldSampleSafetyRule.Apply(field, index, worldPosition, in sample, Safety, _builder.LatestCells, out FlowFieldSample safe))
                sample = safe;
            return status;
        }

        internal bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
            => TrySampleStatus(worldPosition, out sample) == FlowFieldSampleStatus.Sampled;

        internal FlowFieldSample Sample(Vector3 worldPosition)
        {
            FlowFieldSampleStatus status = TrySampleStatus(worldPosition, out FlowFieldSample sample);
            if (status != FlowFieldSampleStatus.Sampled)
                throw FlowFieldFailures.ForSample(status, nameof(worldPosition));
            return sample;
        }

        // The clamp both modes share; it never throws. It uses the latest
        // grid: Surface2D grids clamp X and Z, Volume3D grids all three axes.
        private FlowFieldClampStatus TryClampStatus(Vector3 worldPosition, out FlowFieldClampResult result)
        {
            result = default;
            if (!IsInitialized)
                return FlowFieldClampStatus.NotInitialized;
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                return FlowFieldClampStatus.NonFinite;
            FlowFieldGridSpace grid = _builder.LatestGrid;
            if (!grid.IsValid)
                return FlowFieldClampStatus.NoGrid;
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            Vector3 clamped = volume ? grid.ClampWorldXYZ(worldPosition) : grid.ClampWorldXZ(worldPosition);
            result = new FlowFieldClampResult(
                clamped,
                !Mathf.Approximately(worldPosition.x, clamped.x),
                volume && !Mathf.Approximately(worldPosition.y, clamped.y),
                !Mathf.Approximately(worldPosition.z, clamped.z));
            return FlowFieldClampStatus.Clamped;
        }

        internal bool TryClampPositionToGrid(Vector3 worldPosition, out FlowFieldClampResult result)
            => TryClampStatus(worldPosition, out result) == FlowFieldClampStatus.Clamped;

        /// <summary>The nearest point inside the latest grid.</summary>
        internal FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            FlowFieldClampStatus status = TryClampStatus(worldPosition, out FlowFieldClampResult result);
            if (status != FlowFieldClampStatus.Clamped)
                throw FlowFieldFailures.ForClamp(status, nameof(worldPosition));
            return result;
        }

        // Only a session that can sample describes its field.
        internal bool TryGetFieldInfo(out FlowFieldFieldInfo info)
        {
            info = default;
            return IsReady && _builder.TryGetFieldInfo(out info);
        }

        /// <summary>
        /// The published field, for display and bakes. It is valid until the
        /// next pump, after which its arrays may be reused.
        /// </summary>
        internal bool TryGetPublishedField(out FlowFieldField field)
        {
            field = IsReady ? _builder.PublishedField : null;
            return field != null;
        }

        // Commands issued from Modifier code wait until that operation has
        // finished. The Now methods are the same in both modes; the builder
        // only rebuilds what a change touched.
        internal bool RegisterModifierCommand(IFlowFieldVectorModifier modifier)
            => Boundary.IsInUserCode ? DeferRegisterModifier(modifier) : RegisterModifierNow(modifier);

        internal bool UnregisterModifierCommand(IFlowFieldVectorModifier modifier)
            => Boundary.IsInUserCode ? DeferUnregisterModifier(modifier) : UnregisterModifierNow(modifier);

        internal void NotifyModifierChangedCommand(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
        {
            if (Boundary.IsInUserCode)
                DeferNotifyModifierChanged(modifier, change);
            else
                NotifyModifierChangedNow(modifier, change);
        }

        internal bool RegisterObstacleCommand(Collider collider)
            => Boundary.IsInUserCode ? DeferRegisterObstacle(collider) : RegisterObstacleNow(collider);

        internal bool UnregisterObstacleCommand(Collider collider)
            => Boundary.IsInUserCode ? DeferUnregisterObstacle(collider) : UnregisterObstacleNow(collider);


        // Kept apart so only a deferred call allocates the closure.
        private bool DeferRegisterModifier(IFlowFieldVectorModifier modifier)
            => Defer(() => RegisterModifierNow(modifier), "Modifier");

        private bool DeferUnregisterModifier(IFlowFieldVectorModifier modifier)
            => Defer(() => UnregisterModifierNow(modifier), removal: true);

        private void DeferNotifyModifierChanged(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
            => Defer(() =>
            {
                NotifyModifierChangedNow(modifier, change);
                return true;
            });

        private bool DeferRegisterObstacle(Collider collider)
            => Defer(() => RegisterObstacleNow(collider), "obstacle");

        private bool DeferUnregisterObstacle(Collider collider)
            => Defer(() => UnregisterObstacleNow(collider), removal: true);

        private bool Defer(Func<bool> command, string registration = null, bool removal = false)
        {
            Boundary.Defer(() => RunDeferred(command, registration, removal));
            return true;
        }

        // The session may have stopped by the time a deferred command runs. Stopping
        // already cleared what a removal would have changed, but a registration
        // that was reported as accepted is lost, so it leaves a warning. A removal
        // still runs when the operation faulted, as it would have outside it.
        private void RunDeferred(Func<bool> command, string registration, bool removal)
        {
            if (IsInitialized || removal && IsFaulted)
                command();
            else if (registration != null)
                Debug.LogWarning($"[FlowField] The session stopped, so a deferred {registration} registration was dropped.");
        }

        // The Manager has checked the bounds are finite and refuses this, like
        // a move, inside Modifier code.
        internal void NotifyObstacleRegionDirtyCommand(Bounds worldBounds)
        {
            ThrowIfInputUnavailable();
            ApplyObstacleChange(new FlowFieldObstacleChange(
                FlowFieldObstacleChangeKind.RegionDirty, null, false, default, true, worldBounds));
        }

        private bool RegisterModifierNow(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputUnavailable();
            bool registered;
            using (Boundary.EnterUserCode())
                registered = Modifiers.Register(modifier);
            if (!registered)
                return false;
            _builder.ApplyModifierChange(new FlowFieldModifierUpdate(
                FlowFieldModifierUpdateKind.Registered, Modifiers.Get(modifier)));
            EnterBuildingIfOwed();
            return true;
        }

        // A faulted session still takes removals; its retry builds everything
        // again. A destroyed Modifier is found by its reference, so a caller
        // that unregisters it late still removes it.
        private bool UnregisterModifierNow(IFlowFieldVectorModifier modifier)
        {
            if (!IsInitialized && !IsFaulted)
                throw FlowFieldFailures.NotInitialized();
            if (ReferenceEquals(modifier, null))
                return false;
            bool removed = Modifiers.Unregister(modifier, entry =>
            {
                if (!IsFaulted)
                    _builder.ApplyModifierChange(new FlowFieldModifierUpdate(
                        FlowFieldModifierUpdateKind.Unregistered, entry));
            });
            if (removed)
                EnterBuildingIfOwed();
            return removed;
        }

        // Nothing polls a Modifier, so its notification is where an invalid
        // configuration is found; it faults the session.
        private void NotifyModifierChangedNow(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
        {
            ThrowIfInputUnavailable();
            if (!Modifiers.Contains(modifier))
                throw FlowFieldFailures.ModifierNotRegistered();
            FlowFieldModifierChangeResult result;
            try
            {
                using (Boundary.EnterUserCode())
                    result = Modifiers.ApplyChange(modifier, change);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
            {
                ReportFault(exception);
                return;
            }
            _builder.ApplyModifierChange(new FlowFieldModifierUpdate(
                FlowFieldModifierUpdateKind.Changed, Modifiers.Get(modifier), change, result));
            EnterBuildingIfOwed();
        }

        private bool RegisterObstacleNow(Collider collider)
        {
            ThrowIfInputUnavailable();
            if (!Obstacles.Register(collider, out FlowFieldObstacleChange change))
                return false;
            ApplyObstacleChange(in change);
            return true;
        }

        // A faulted session still takes removals (see ApplyObstacleChange).
        private bool UnregisterObstacleNow(Collider collider)
        {
            if (!IsInitialized && !IsFaulted)
                throw FlowFieldFailures.NotInitialized();
            if (!Obstacles.Unregister(collider, out FlowFieldObstacleChange change))
                return false;
            ApplyObstacleChange(in change);
            // A faulted or stopped session may retry or start again in this
            // frame, before the Destroy, so it is rechecked as well.
            if (change.HasCurrent)
                RememberReleased(new ReleasedCollider(collider, change.Current, Time.frameCount));
            return true;
        }

        private void RememberReleased(ReleasedCollider released)
        {
            for (int index = 0; index < _releasedInScene.Count; index++)
            {
                if (ReferenceEquals(_releasedInScene[index].Collider, released.Collider))
                {
                    _releasedInScene[index] = released;
                    return;
                }
            }
            _releasedInScene.Add(released);
        }

        // A Collider unregistered while still in the scene can count as a
        // static obstacle, and the build that starts next scans it so. Destroy
        // removes it only at the end of a frame, after that scan, and nothing
        // would report its spot again. So for the next two frames its spot is
        // reported once more as soon as it has left the scene.
        private void RecheckReleasedColliders()
        {
            for (int index = _releasedInScene.Count - 1; index >= 0; index--)
            {
                ReleasedCollider released = _releasedInScene[index];
                int age = Time.frameCount - released.Frame;
                if (age <= 0)
                    continue;
                Collider collider = released.Collider;
                bool inScene = collider != null && collider.enabled && collider.gameObject.activeInHierarchy;
                if (inScene && age < 2)
                    continue;
                _releasedInScene.RemoveAt(index);
                if (!inScene)
                    ApplyObstacleChange(new FlowFieldObstacleChange(
                        FlowFieldObstacleChangeKind.RegionDirty, null, true, released.Bounds, false, default));
            }
        }

        // The Manager refuses it inside Modifier code, so it never waits.
        internal bool NotifyObstacleMovedCommand(Collider collider)
        {
            ThrowIfInputUnavailable();
            if (!Obstacles.NotifyMoved(collider, out FlowFieldObstacleChange change, out bool changed))
                return false;
            if (changed)
                ApplyObstacleChange(in change);
            return true;
        }

        // Cells a change can reach stop from this frame until a build that
        // queried them is published; the builder then rebuilds them. A change
        // that reaches no cell of the latest grid is left out: the registry
        // already holds its bounds, and a later move into the grid reports
        // both sides. A field on an older grid never shows again (a new grid
        // drops it), so it cannot miss the change either.
        private void ApplyObstacleChange(in FlowFieldObstacleChange change)
        {
            FlowFieldGridSpace grid = _builder.LatestGrid;
            float reach = grid.IsValid ? ObstacleReach(grid) : 0f;
            if (grid.IsValid
                && (change.HasPrevious || change.HasCurrent)
                && !Reaches(grid, change.HasPrevious, change.Previous, reach)
                && !Reaches(grid, change.HasCurrent, change.Current, reach))
                return;
            // A faulted session builds nothing, and its retry starts from
            // nothing. A BakedBase retry still reloads the bake and queries
            // only what changed since Initialize, so the builder records
            // where the change reached.
            if (IsFaulted)
            {
                _builder.ApplyObstacleChange(in change);
                return;
            }
            if (BakeMode != FlowFieldBakeMode.StaticBaked && grid.IsValid)
            {
                if (change.HasPrevious)
                    Safety.MarkDirty(change.Previous, reach);
                if (change.HasCurrent)
                    Safety.MarkDirty(change.Current, reach);
            }
            _builder.ApplyObstacleChange(in change);
            EnterBuildingIfOwed();
        }

        // A change reaches the cell size and the Obstacle Clearance beyond its bounds.
        private float ObstacleReach(FlowFieldGridSpace grid)
            => FlowFieldObstacleReach.Margin(grid.CellSize, _hasConfig ? _config.Obstacles.Clearance : 0f);

        // Bounds that are not finite count as reaching, so marking them reports the fault.
        private static bool Reaches(FlowFieldGridSpace grid, bool has, Bounds bounds, float reach)
        {
            if (!has)
                return false;
            if (!FlowFieldGridSpace.IsFinite(bounds.center) || !FlowFieldGridSpace.IsFinite(bounds.size))
                return true;
            return FlowFieldCellRect.FromBounds(grid, FlowFieldObstacleReach.Expand(bounds, reach)).IsValid;
        }

        private void ThrowIfInputUnavailable()
        {
            if (IsFaulted)
                throw FlowFieldFailures.Faulted(Fault);
            if (!IsInitialized)
                throw FlowFieldFailures.NotInitialized();
        }

        internal bool IsWaitingForExternalCompletion => _builder.IsWaitingForExternalCompletion;
        internal bool IsBuilding => CanPump && (IsRebuilding || HasPendingRequest);

        // ----- The build pipeline both space modes run -----------------------
        // A build begins, takes steps until it is published or rejected, and
        // the session moves between Building and Ready around it and records
        // which uncertain regions it resolves. The builder only supplies the
        // steps and says what each build queries.

        // The scheduler's pump: it shares the frame budget, never waits for
        // the worker thread and runs the unregistered-obstacle sweep.
        internal void PumpScheduled(in FlowFieldBuildBudget budget)
            => RunPipeline(in budget, waitForWorker: false, sweep: true);

        /// <summary>
        /// A direct owner (a bake, the Editor preview) runs its own session and
        /// waits for the worker thread; the scheduler pumps with a frame budget
        /// and never waits. With no budget the pending build runs to its end.
        /// </summary>
        internal void Pump() => RunPipeline(FlowFieldBuildBudget.Unlimited, waitForWorker: true, sweep: false);

        internal void Pump(double budgetMilliseconds)
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            var budget = new FlowFieldBuildBudget(budgetMilliseconds, () => timer.Elapsed.TotalMilliseconds);
            RunPipeline(in budget, waitForWorker: true, sweep: false);
        }

        private void RunPipeline(in FlowFieldBuildBudget budget, bool waitForWorker, bool sweep)
        {
            if (!CanPump)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            try
            {
                // Inside the try, so a failing sweep faults this session only.
                if (_releasedInScene.Count > 0)
                    RecheckReleasedColliders();
                if (sweep)
                    RunObstacleSweep();
                int begun = 0;
                while (CanPump)
                {
                    if (!_builder.HasActiveBuild)
                    {
                        FlowFieldBuildPolicy policy = _builder.BuildPolicy;
                        if (begun >= policy.MaxBuildsBegunPerPump
                            || !policy.BeginsAfterBudget && budget.IsExpired)
                            return;
                        if (_builder.HasPendingRequest || _builder.HasOwedInput)
                        {
                            PruneDestroyedObstacles();
                            PruneDestroyedModifiers();
                        }
                        bool started = _builder.TryBeginBuild(out FlowFieldBuildStart start);
                        if (start.InvalidatedRegion.HasValue)
                            Safety.InvalidateRegion(start.InvalidatedRegion.Value);
                        if (!started)
                        {
                            LeaveBuildingIfIdle();
                            return;
                        }
                        if (start.ResolvesUncertainRegions)
                            Safety.BeginBuild();
                        begun++;
                    }

                    FlowFieldBuildStep step = _builder.StepBuild(in budget, waitForWorker);
                    if (!CanPump)
                        return;
                    switch (step)
                    {
                        case FlowFieldBuildStep.Completed:
                            // Only a field that reads differently is a new Revision.
                            bool changed = _builder.PublishBuild();
                            Safety.BuildPublished();
                            if (changed)
                            {
                                unchecked { Revision++; }
                                NotifyFieldCommitted();
                            }
                            PublishRuntimeState();
                            PruneDestroyedModifiers();
                            if (!_builder.HasOwedInput)
                                SetLifecycle(FlowFieldSessionLifecycle.Active);
                            continue;
                        case FlowFieldBuildStep.Rejected:
                            PruneDestroyedModifiers();
                            // Owed input builds next without leaving Building.
                            if (!_builder.HasOwedInput)
                                SetLifecycle(FlowFieldSessionLifecycle.Active);
                            continue;
                        case FlowFieldBuildStep.Waiting when waitForWorker:
                            SetLifecycle(FlowFieldSessionLifecycle.Building);
                            continue;
                        default:
                            SetLifecycle(FlowFieldSessionLifecycle.Building);
                            return;
                    }
                }
            }
            catch (Exception exception)
            {
                ReportFault(exception);
            }
        }

        // A registered Collider destroyed without being unregistered leaves the
        // registry here, before the build that is owed begins, as a move away
        // from where it was last reported: its cells are uncertain until that
        // build publishes. Builders read the registry and never change it.
        // Nothing is polled while no build is owed.
        private void PruneDestroyedObstacles()
        {
            for (int index = Obstacles.Count - 1; index >= 0; index--)
            {
                Collider collider = Obstacles.GetCollider(index);
                if (collider != null)
                    continue;
                if (Obstacles.NotifyMoved(collider, out FlowFieldObstacleChange change, out bool changed) && changed)
                    ApplyObstacleChange(in change);
            }
        }

        // One sweep step a frame; the scheduler may pump a session more than once.
        private void RunObstacleSweep()
        {
            if (!SweepsObstacles)
                return;
            if (!_sweep.Step(Time.frameCount, Time.unscaledTimeAsDouble, Obstacles))
            {
                // A stopped sweep no longer watches the bounds, so the masks
                // built with it are rebuilt without it.
                ApplyObstacleChange(new FlowFieldObstacleChange(
                    FlowFieldObstacleChangeKind.RegionDirty, null, false, default, true, _sweep.SweepBounds));
            }
        }

        // An unregistered Collider appeared, moved or disappeared.
        private void ApplySweptChange(bool hasPrevious, Bounds previous, bool hasCurrent, Bounds current)
            => ApplyObstacleChange(new FlowFieldObstacleChange(
                FlowFieldObstacleChangeKind.RegionDirty, null, hasPrevious, previous, hasCurrent, current));

        // The scheduler holds sessions that build or wait for a retry. A
        // stopped session leaves it (Stop) and joins again when its restart
        // submits; a faulted one stays, idle, until its retry, Release or
        // Dispose. One that never runs, such as Init on an inactive Manager
        // whose OnDestroy Unity never calls, is never held.
        internal void AttachRuntimeScheduler()
        {
            if (Disposed)
                throw new ObjectDisposedException(GetType().Name);
            if (_runtimeScheduled || !CanPump)
                return;
            FlowFieldBuildScheduler.Register(this);
            _runtimeScheduled = true;
        }

        internal void DetachRuntimeScheduler()
        {
            if (!_runtimeScheduled)
                return;
            FlowFieldBuildScheduler.Unregister(this);
            _runtimeScheduled = false;
        }

        // ----- Lifecycle -----------------------------------------------------
        // The same transitions in both modes. The builder only cancels its
        // work, drops what it computed and frees what it holds.

        /// <summary>Starts a new or released session with no field yet.</summary>
        internal void Initialize(FlowFieldBakeMode bakeMode)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (IsFaulted)
                throw new InvalidOperationException(
                    "FlowField session is faulted. Retry the pending request or Release before Initialize.", Fault);
            if (IsInitialized)
                throw new InvalidOperationException("FlowField session is already initialized.");
            BakeMode = bakeMode;
            Fault = null;
            Safety.ClearRegions();
            _builder.Initialize();
            SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        /// <summary>A running session drops its work and its field and waits.</summary>
        internal void Stop()
        {
            if (!CanPump)
                return;
            AbandonWork();
            DetachRuntimeScheduler();
            _builder.DropComputedState();
            // The restart reads the whole scene again wherever the sweep runs
            // (FlowFieldObstacleSweep), so the sweep starts over too.
            _sweep.Clear();
            SetLifecycle(FlowFieldSessionLifecycle.Suspended);
        }

        /// <summary>
        /// A stopped or faulted session starts again from nothing. Registered
        /// obstacles and Modifiers stay; the latest request builds again.
        /// </summary>
        internal void Restart()
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (Lifecycle != FlowFieldSessionLifecycle.Suspended && !IsFaulted)
                return;
            AbandonWork();
            _builder.DropComputedState();
            Fault = null;
            SetLifecycle(FlowFieldSessionLifecycle.Building);
        }

        /// <summary>
        /// After a restart, reads every registered Modifier again: Priority
        /// edits made while the session was faulted or stopped never arrived.
        /// Throws for an invalid Modifier; the caller reports the fault.
        /// </summary>
        internal void ObserveModifiers()
        {
            PruneDestroyedModifiers();
            using (Boundary.EnterUserCode())
                Modifiers.ObserveAll();
        }

        // A registered Modifier destroyed without being unregistered leaves the
        // registry like a destroyed obstacle: before the build that is owed
        // begins, when a build ends (it may have been destroyed while the build
        // ran), and before a restart reads every Modifier again. Its influence
        // leaves with it. Nothing is polled while no build runs or is owed.
        private void PruneDestroyedModifiers()
        {
            if (_modifiers == null)
                return;
            for (int index = _modifiers.Count - 1; index >= 0; index--)
            {
                IFlowFieldVectorModifier modifier = _modifiers.Entries[index].Modifier;
                if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                    UnregisterModifierNow(modifier);
            }
        }

        /// <summary>Clears everything, registrations included. Initialize may follow.</summary>
        internal void Release()
        {
            if (Disposed)
                return;
            ReleaseState();
            SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        internal void ReportFault(Exception exception)
        {
            if (IsInitialized)
                EnterFault(exception ?? new InvalidOperationException("FlowField session failed."));
        }

        private void EnterFault(Exception exception)
        {
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            AbandonWork();
            Fault = exception;
            SetLifecycle(FlowFieldSessionLifecycle.Faulted);
            Publish(FlowFieldExecutionBoundary.EventKind.Failed, exception: exception);
        }

        /// <summary>Released for good: nothing can Initialize this session again.</summary>
        public void Dispose()
        {
            if (Disposed)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            // Disposal is terminal before Released subscribers run, so they
            // cannot recreate resources or recursively dispose this owner.
            Disposed = true;
            ReleaseState();
            SetLifecycle(FlowFieldSessionLifecycle.Released);
            ClosePublishing();
        }

        // Work begun before this point must not publish, and its queued
        // events and late solve callbacks are dropped.
        private void AbandonWork()
        {
            unchecked { LifetimeGeneration++; }
            _builder.CancelWork();
            Safety.BuildAbandoned();
        }

        private void ReleaseState()
        {
            AbandonWork();
            DetachRuntimeScheduler();
            Obstacles.Clear();
            _releasedInScene.Clear();
            _modifiers?.Clear();
            _builder.ReleaseResources();
            Safety.ClearRegions();
            _sweep.Reset();
            Fault = null;
        }

        private void SetLifecycle(FlowFieldSessionLifecycle next)
        {
            if (Lifecycle == next)
                return;
            Lifecycle = next;
            PublishRuntimeState();
        }

        private void PublishRuntimeState()
        {
            FlowFieldRuntimeState state = State;
            if (_publishedState == state)
                return;
            // A session that never started has nothing for a subscriber to end.
            bool neverStarted = _publishedState == FlowFieldRuntimeState.Uninitialized
                && state == FlowFieldRuntimeState.Released;
            _publishedState = state;
            if (!neverStarted)
                Publish(FlowFieldExecutionBoundary.EventKind.StateChanged, state);
        }

        private void NotifyFieldCommitted() => Publish(FlowFieldExecutionBoundary.EventKind.FieldCommitted);

        private void Publish(
            FlowFieldExecutionBoundary.EventKind kind,
            FlowFieldRuntimeState state = default,
            Exception exception = null)
        {
            if (!_publishingClosed)
                Boundary.Publish(new FlowFieldExecutionBoundary.QueuedEvent(
                    this,
                    kind,
                    LifetimeGeneration,
                    state,
                    exception));
        }

        private void ClosePublishing()
        {
            if (_publishingClosed)
                return;
            Publish(FlowFieldExecutionBoundary.EventKind.Disposed);
            _publishingClosed = true;
        }

        // A handler sees an event only while the session is still in the lifetime
        // that raised it. Released ends a lifetime, so it is always delivered.
        internal void Deliver(in FlowFieldExecutionBoundary.QueuedEvent queuedEvent)
        {
            if (queuedEvent.Kind == FlowFieldExecutionBoundary.EventKind.Disposed)
            {
                FieldCommitted = null;
                Failed = null;
                StateChanged = null;
                EventFailed = null;
                return;
            }

            bool released = queuedEvent.Kind == FlowFieldExecutionBoundary.EventKind.StateChanged
                && queuedEvent.State == FlowFieldRuntimeState.Released;
            if (!released && queuedEvent.Generation != LifetimeGeneration)
                return;

            try
            {
                switch (queuedEvent.Kind)
                {
                    case FlowFieldExecutionBoundary.EventKind.StateChanged:
                        StateChanged?.Invoke(queuedEvent.State);
                        break;
                    case FlowFieldExecutionBoundary.EventKind.FieldCommitted:
                        FieldCommitted?.Invoke();
                        break;
                    case FlowFieldExecutionBoundary.EventKind.Failed:
                        Failed?.Invoke(queuedEvent.Exception);
                        break;
                }
            }
            catch (Exception exception)
            {
                ReportEventFailure(exception);
            }
        }

        private void ReportEventFailure(Exception exception)
        {
            try
            {
                EventFailed?.Invoke(exception);
            }
            catch (Exception reportingException)
            {
                Debug.LogException(reportingException);
            }
        }
    }
}
