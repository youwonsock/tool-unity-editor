using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>What a command deferred from Modifier code does if the session stopped before it ran.</summary>
    internal enum FlowFieldDeferredKind
    {
        // Runs only while the session runs.
        Command,
        // A registration reported as accepted: dropping it leaves a warning.
        ModifierRegistration,
        ObstacleRegistration,
        // Also runs while the session is faulted, as it would outside the operation.
        Removal,
    }

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
            // Every input but the default Goal is a new base for the
            // additional Goals, and a new grid or source drops their fields
            // with the published one.
            if (change != FlowFieldConfigChange.Goal)
                Goals.TouchBase();
            if (_builder.PublishedField == null)
                Goals.DropFields();
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
            => Defer(() => RegisterModifierNow(modifier), FlowFieldDeferredKind.ModifierRegistration);

        private bool DeferUnregisterModifier(IFlowFieldVectorModifier modifier)
            => Defer(() => UnregisterModifierNow(modifier), FlowFieldDeferredKind.Removal);

        private void DeferNotifyModifierChanged(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
            => Defer(() =>
            {
                NotifyModifierChangedNow(modifier, change);
                return true;
            }, FlowFieldDeferredKind.Command);

        private bool DeferRegisterObstacle(Collider collider)
            => Defer(() => RegisterObstacleNow(collider), FlowFieldDeferredKind.ObstacleRegistration);

        private bool DeferUnregisterObstacle(Collider collider)
            => Defer(() => UnregisterObstacleNow(collider), FlowFieldDeferredKind.Removal);

        private bool Defer(Func<bool> command, FlowFieldDeferredKind kind)
        {
            Boundary.Defer(() => RunDeferred(command, kind));
            return true;
        }

        // The session may have stopped by the time a deferred command runs. Stopping
        // already cleared what a removal would have changed, but a registration
        // that was reported as accepted is lost, so it leaves a warning. A removal
        // still runs when the operation faulted, as it would have outside it.
        private void RunDeferred(Func<bool> command, FlowFieldDeferredKind kind)
        {
            if (IsInitialized || kind == FlowFieldDeferredKind.Removal && IsFaulted)
                command();
            else if (kind == FlowFieldDeferredKind.ModifierRegistration)
                Debug.LogWarning("[FlowField] The session stopped, so a deferred Modifier registration was dropped.");
            else if (kind == FlowFieldDeferredKind.ObstacleRegistration)
                Debug.LogWarning("[FlowField] The session stopped, so a deferred obstacle registration was dropped.");
        }

        // The Manager has checked the bounds are finite and refuses this, like
        // a move, inside Modifier code.
        internal void NotifyObstacleRegionDirtyCommand(Bounds worldBounds)
        {
            ThrowIfInputUnavailable();
            ApplyObstacleChange(FlowFieldObstacleChange.RegionDirty(null, worldBounds));
        }

        private bool RegisterModifierNow(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputUnavailable();
            bool registered;
            using (Boundary.EnterUserCode())
                registered = Modifiers.Register(modifier);
            if (!registered)
                return false;
            Goals.TouchBase();
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
                Goals.TouchBase();
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
            Goals.TouchBase();
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
            if (change.Current.HasValue)
                _releasedInScene.Remember(collider, change.Current.Value, Time.frameCount);
            return true;
        }

        // A Collider released while still in the scene has left it: its spot
        // is scanned again (FlowFieldReleasedColliders).
        private void ReportReleasedGone(Bounds bounds)
            => ApplyObstacleChange(FlowFieldObstacleChange.RegionDirty(bounds, null));

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
                && (change.Previous.HasValue || change.Current.HasValue)
                && !Reaches(grid, change.Previous, reach)
                && !Reaches(grid, change.Current, reach))
                return;
            // A faulted session builds nothing, and its retry starts from
            // nothing. A BakedBase retry still reloads the bake and queries
            // only what changed since Initialize, so the builder records
            // where the change reached.
            Goals.TouchBase();
            if (IsFaulted)
            {
                _builder.ApplyObstacleChange(in change);
                return;
            }
            if (BakeMode != FlowFieldBakeMode.StaticBaked && grid.IsValid)
            {
                if (change.Previous.HasValue)
                    Safety.MarkDirty(change.Previous.Value, reach);
                if (change.Current.HasValue)
                    Safety.MarkDirty(change.Current.Value, reach);
            }
            _builder.ApplyObstacleChange(in change);
            EnterBuildingIfOwed();
        }

        // A change reaches the cell size and the Obstacle Clearance beyond its bounds.
        private float ObstacleReach(FlowFieldGridSpace grid)
            => FlowFieldObstacleReach.Margin(grid.CellSize, _hasConfig ? _config.Obstacles.Clearance : 0f);

        // Bounds that are not finite count as reaching, so marking them reports the fault.
        private static bool Reaches(FlowFieldGridSpace grid, Bounds? region, float reach)
        {
            if (!region.HasValue)
                return false;
            Bounds bounds = region.Value;
            if (!FlowFieldGridSpace.IsFinite(bounds.center) || !FlowFieldGridSpace.IsFinite(bounds.size))
                return true;
            return FlowFieldCellRect.FromBounds(grid, FlowFieldObstacleReach.Expand(bounds, reach)).IsValid;
        }

        // ----- Additional Goals ---------------------------------------------
        // The Manager has checked the request, the kind and the Bake Mode. A
        // Goal's field builds once the default Goal's pipeline has nothing owed.

        // An agent type's session adds the Goal under the primary session's id.
        internal int AddGoalCommand(FlowFieldGoalKind kind, in FlowFieldGoalRequest request, int id = 0)
        {
            ThrowIfInputUnavailable();
            id = Goals.Add(kind, request, id).Id;
            EnterBuildingIfOwed();
            return id;
        }

        /// <summary>False when the Goal is not in the set.</summary>
        internal bool SetGoalCommand(int id, in FlowFieldGoalRequest request)
        {
            ThrowIfInputUnavailable();
            if (!Goals.TrySetRequest(id, request))
                return false;
            EnterBuildingIfOwed();
            return true;
        }

        /// <summary>A world Goal's seeds in this area changed. False when the Goal is not in the set.</summary>
        internal bool SetGoalSeedsCommand(int id, FlowFieldGoalSeeds seeds)
        {
            ThrowIfInputUnavailable();
            if (!Goals.TrySetSeeds(id, seeds))
                return false;
            EnterBuildingIfOwed();
            return true;
        }

        // A faulted session takes removals too, as with obstacles and Modifiers.
        internal bool RemoveGoalCommand(int id)
        {
            if (!IsInitialized && !IsFaulted)
                throw FlowFieldFailures.NotInitialized();
            if (!Goals.Remove(id))
                return false;
            LeaveBuildingIfIdle();
            return true;
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
