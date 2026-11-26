using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The Provider and Controller API and the checks every call makes first.</summary>
    public partial class FlowFieldManager
    {
        // The one availability check of the contract: a call needs a session
        // that is initialized (running or stopped) and not faulted.
        private void ThrowIfLifecycleUnavailable()
        {
            if (IsFaulted)
                throw FlowFieldFailures.Faulted(ActiveSession.Fault);
            if (!IsInitialized)
                throw FlowFieldFailures.NotInitialized();
        }

        private void MarkGoalDirty()
            => SubmitCheckedConfig(FlowFieldConfigChange.Goal);

        /// <inheritdoc/>
        public FlowFieldSample Sample(Vector3 worldPosition)
        {
            ThrowIfLifecycleUnavailable();
            return ActiveSession.Sample(worldPosition);
        }

        /// <inheritdoc/>
        public bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            return ActiveSession?.TrySample(worldPosition, out sample) ?? false;
        }

        /// <inheritdoc/>
        public FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            ThrowIfLifecycleUnavailable();
            return ActiveSession.ClampPositionToGrid(worldPosition);
        }

        /// <inheritdoc/>
        public bool TryClampPositionToGrid(Vector3 worldPosition, out FlowFieldClampResult result)
        {
            result = default;
            return ActiveSession?.TryClampPositionToGrid(worldPosition, out result) ?? false;
        }

        /// <inheritdoc/>
        public void SetSpaceMode(FlowFieldSpaceMode mode)
        {
            _boundary.ThrowIfInUserCode(nameof(SetSpaceMode));
            if (!Enum.IsDefined(typeof(FlowFieldSpaceMode), mode))
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (IsInitialized || IsFaulted)
                throw FlowFieldFailures.SpaceModeLocked();
            _spaceMode = mode;
        }

        /// <inheritdoc/>
        public bool RegisterDynamicObstacle(Collider collider)
        {
            ThrowIfLifecycleUnavailable();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (collider == null)
                throw FlowFieldFailures.DestroyedCollider();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw FlowFieldFailures.NotAllowedIn("Dynamic obstacle registration", FlowFieldBakeMode.StaticBaked);
            using (EnterFanOut())
            {
                bool registered = ActiveSession.RegisterObstacleCommand(collider);
                for (int i = 0; i < _agentSessions.Length; i++)
                {
                    if (!_agentSessions[i].IsFaulted)
                        _agentSessions[i].RegisterObstacleCommand(collider);
                }
                return registered;
            }
        }

        /// <inheritdoc/>
        public bool UnregisterDynamicObstacle(Collider collider)
        {
            // Removal needs no valid configuration, as with Modifiers. A faulted
            // Manager still takes it, or a retry would keep the obstacle.
            if (!IsFaulted)
                ThrowIfLifecycleUnavailable();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            using (EnterFanOut())
            {
                bool unregistered = ActiveSession.UnregisterObstacleCommand(collider);
                for (int i = 0; i < _agentSessions.Length; i++)
                    _agentSessions[i].UnregisterObstacleCommand(collider);
                return unregistered;
            }
        }

        // Whether the running session holds this Collider; a Release clears
        // every registration, so FlowFieldDynamicObstacle registers again.
        internal bool IsDynamicObstacleRegistered(Collider collider)
            => ActiveSession != null && ActiveSession.Obstacles.Contains(collider);

        /// <summary>
        /// A registered obstacle moved. The cells of its last reported bounds and of
        /// its bounds now are rebuilt; a destroyed one stops being registered.
        /// False when the Collider is not registered.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="collider"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Not initialized, faulted, in StaticBaked mode, or called from Modifier code.
        /// </exception>
        public bool NotifyDynamicObstacleMoved(Collider collider)
        {
            _boundary.ThrowIfInUserCode(nameof(NotifyDynamicObstacleMoved));
            ThrowIfLifecycleUnavailable();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw FlowFieldFailures.NotAllowedIn("Obstacle changes", FlowFieldBakeMode.StaticBaked);
            using (EnterFanOut())
            {
                bool moved = ActiveSession.NotifyObstacleMovedCommand(collider);
                for (int i = 0; i < _agentSessions.Length; i++)
                {
                    if (!_agentSessions[i].IsFaulted)
                        _agentSessions[i].NotifyObstacleMovedCommand(collider);
                }
                return moved;
            }
        }

        /// <inheritdoc/>
        public void NotifyObstacleRegionDirty(Bounds worldBounds)
        {
            _boundary.ThrowIfInUserCode(nameof(NotifyObstacleRegionDirty));
            ThrowIfLifecycleUnavailable();
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center)
                || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                throw FlowFieldFailures.BoundsNotFinite(nameof(worldBounds));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw FlowFieldFailures.NotAllowedIn("Obstacle changes", FlowFieldBakeMode.StaticBaked);
            using (EnterFanOut())
            {
                ActiveSession.NotifyObstacleRegionDirtyCommand(worldBounds);
                for (int i = 0; i < _agentSessions.Length; i++)
                {
                    if (!_agentSessions[i].IsFaulted)
                        _agentSessions[i].NotifyObstacleRegionDirtyCommand(worldBounds);
                }
            }
        }

        /// <inheritdoc/>
        public void SetGoal(in FlowFieldGoalRequest request)
        {
            int heldVersion = 0;
            SetGoal(in request, ref heldVersion);
        }

        /// <summary>
        /// SetGoal for FlowFieldGoalTarget: writes the <see cref="GoalVersion"/>
        /// this request leaves into <paramref name="heldVersion"/> before the
        /// submit. Handlers run during the submit, so one that disables the
        /// target already finds it holding this Goal, and one that changes the
        /// Goal moves the version on, so the target does not take that Goal for
        /// its own.
        /// </summary>
        internal void SetGoal(in FlowFieldGoalRequest request, ref int heldVersion)
        {
            _boundary.ThrowIfInUserCode(nameof(SetGoal));
            ThrowIfLifecycleUnavailable();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw FlowFieldFailures.NotAllowedIn("Runtime Goal changes", FlowFieldBakeMode.StaticBaked);

            if (!request.HasGoal)
            {
                if (!_hasExplicitGoal)
                {
                    heldVersion = GoalVersion;
                    return;
                }
                ChangeExplicitGoal(hasGoal: false, default);
            }
            else
            {
                if (_hasExplicitGoal
                    && FlowFieldGridSpace.Approximately(
                        _explicitGoalWorld,
                        request.WorldPosition,
                        0.00000001d)
                    && Mathf.Abs(ConfiguredGoalRadius - request.InfluenceRadius) <= FlowFieldManagerConfig.ValueEpsilon)
                {
                    // Nothing to build, but the caller now holds this Goal:
                    // a target that set it earlier no longer clears it.
                    _explicitGoalRadius = request.InfluenceRadius;
                    unchecked { GoalVersion++; }
                    heldVersion = GoalVersion;
                    return;
                }
                ChangeExplicitGoal(hasGoal: true, request.WorldPosition, request.InfluenceRadius);
            }

            heldVersion = GoalVersion;
            MarkGoalDirty();
        }

        // FlowFieldGoalTarget clears the Goal it set while the Manager is
        // faulted, as Unregister calls are taken then, so a retry leaves it out.
        internal void ClearGoalWhileFaulted()
        {
            if (!IsFaulted || !_hasExplicitGoal)
                return;
            ChangeExplicitGoal(hasGoal: false, default);
        }

        /// <inheritdoc/>
        public FlowFieldGoalHandle AddGoal(FlowFieldGoalKind kind, in FlowFieldGoalRequest request)
        {
            _boundary.ThrowIfInUserCode(nameof(AddGoal));
            ThrowIfLifecycleUnavailable();
            if (!Enum.IsDefined(typeof(FlowFieldGoalKind), kind))
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (!request.HasGoal)
                throw MissingGoal(nameof(request));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw FlowFieldFailures.NotAllowedIn("Adding a Goal", FlowFieldBakeMode.StaticBaked);
            // Every agent type builds the Goal under the same id.
            using (EnterFanOut())
            {
                int id = ActiveSession.AddGoalCommand(kind, request);
                for (int i = 0; i < _agentSessions.Length; i++)
                {
                    if (!_agentSessions[i].IsFaulted)
                        _agentSessions[i].AddGoalCommand(kind, request, id);
                }
                return new FlowFieldGoalHandle(id);
            }
        }

        /// <inheritdoc/>
        public bool SetGoal(FlowFieldGoalHandle goal, in FlowFieldGoalRequest request)
        {
            _boundary.ThrowIfInUserCode(nameof(SetGoal));
            ThrowIfLifecycleUnavailable();
            if (!request.HasGoal)
                throw MissingGoal(nameof(request));
            if (!goal.IsValid)
                return false;
            using (EnterFanOut())
            {
                if (!ActiveSession.SetGoalCommand(goal.Id, request))
                    return false;
                for (int i = 0; i < _agentSessions.Length; i++)
                {
                    if (!_agentSessions[i].IsFaulted)
                        _agentSessions[i].SetGoalCommand(goal.Id, request);
                }
                return true;
            }
        }

        /// <inheritdoc/>
        public bool RemoveGoal(FlowFieldGoalHandle goal)
        {
            _boundary.ThrowIfInUserCode(nameof(RemoveGoal));
            if (!IsFaulted)
                ThrowIfLifecycleUnavailable();
            if (!goal.IsValid)
                return false;
            using (EnterFanOut())
            {
                if (!ActiveSession.RemoveGoalCommand(goal.Id))
                    return false;
                for (int i = 0; i < _agentSessions.Length; i++)
                    _agentSessions[i].RemoveGoalCommand(goal.Id);
                return true;
            }
        }

        /// <inheritdoc/>
        public bool IsGoalReady(FlowFieldGoalHandle goal)
            => goal.IsValid && ActiveSession != null && ActiveSession.IsGoalReady(goal.Id);

        /// <inheritdoc/>
        public bool TrySample(Vector3 worldPosition, FlowFieldGoalHandle goal, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            return goal.IsValid && ActiveSession != null && ActiveSession.TrySampleGoal(goal.Id, worldPosition, out sample);
        }

        /// <inheritdoc/>
        public bool TrySampleNearestShared(Vector3 worldPosition, out FlowFieldSample sample, out FlowFieldGoalHandle goal)
        {
            sample = FlowFieldSample.Stopped;
            goal = default;
            if (ActiveSession == null || !ActiveSession.TrySampleNearestShared(worldPosition, out sample, out int id))
                return false;
            goal = new FlowFieldGoalHandle(id);
            return true;
        }

        private static ArgumentException MissingGoal(string parameter)
            => new ArgumentException(
                "An additional Goal needs a position; FlowFieldGoalRequest.None names no Goal.", parameter);

        /// <inheritdoc/>
        public void NotifyCellsDirty()
        {
            _boundary.ThrowIfInUserCode(nameof(NotifyCellsDirty));
            ThrowIfLifecycleUnavailable();
            // Both modes that load a bake keep its cells.
            if (FlowFieldBakeModes.UsesBakeAsset(BakeMode))
                throw FlowFieldFailures.NotAllowedIn("Cell regeneration", BakeMode);
            SubmitCheckedConfig(FlowFieldConfigChange.Cells);
        }

        /// <inheritdoc/>
        public bool RegisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfLifecycleUnavailable();
            using (EnterFanOut())
            {
                bool registered = ActiveSession.RegisterModifierCommand(modifier);
                for (int i = 0; i < _agentSessions.Length; i++)
                {
                    if (!_agentSessions[i].IsFaulted)
                        _agentSessions[i].RegisterModifierCommand(modifier);
                }
                return registered;
            }
        }

        /// <inheritdoc/>
        public bool UnregisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                return false;
            // A component must be able to detach while the Manager is faulted,
            // or a retry would apply it again.
            if (!IsFaulted)
                ThrowIfLifecycleUnavailable();
            using (EnterFanOut())
            {
                bool unregistered = ActiveSession.UnregisterModifierCommand(modifier);
                for (int i = 0; i < _agentSessions.Length; i++)
                    _agentSessions[i].UnregisterModifierCommand(modifier);
                return unregistered;
            }
        }

        /// <inheritdoc/>
        public void NotifyModifierChanged(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            if ((change & ~(FlowFieldModifierChange.Value
                | FlowFieldModifierChange.Area
                | FlowFieldModifierChange.Priority)) != 0)
                throw new ArgumentOutOfRangeException(nameof(change));
            if (change == FlowFieldModifierChange.None)
                return;
            ThrowIfLifecycleUnavailable();
            using (EnterFanOut())
            {
                ActiveSession.NotifyModifierChangedCommand(modifier, change);
                // A type that missed the registration waits for the next retry.
                for (int i = 0; i < _agentSessions.Length; i++)
                {
                    FlowFieldSession agent = _agentSessions[i];
                    if (!agent.IsFaulted && agent.Modifiers.Contains(modifier))
                        agent.NotifyModifierChangedCommand(modifier, change);
                }
            }
        }
    }
}
