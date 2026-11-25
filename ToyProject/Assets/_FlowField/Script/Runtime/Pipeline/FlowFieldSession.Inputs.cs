using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The input both space modes share: settings, obstacle and Modifier
    /// commands, and the uncertain regions they mark.
    /// </summary>
    internal sealed partial class FlowFieldSession
    {
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
}
}
