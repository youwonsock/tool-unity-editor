using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Additional Agent Types: one more session per type, in the primary
    /// session's Space Mode, built from the settings with that type's
    /// clearance, slope and step height. They follow the primary session's
    /// lifecycle and take every input it takes. Their published fields count
    /// in the one Revision, and a fault of one faults the Manager, which stops
    /// them until its retry. The primary session answers every call without
    /// an agent type and decides State.
    /// </summary>
    public partial class FlowFieldManager
    {
        internal const int MaxAdditionalAgentTypes = 2;

        [SerializeField, Tooltip("Agent types besides the settings above, each with its own fields: another obstacle clearance, and in Surface2D another slope and step height. Up to two; not available in StaticBaked. The number and names can change only after Release.")]
        private FlowFieldAgentTypeSettings[] _agentTypes = Array.Empty<FlowFieldAgentTypeSettings>();

        // One session per additional type, in order, and the names and Space
        // Mode they were made for. Init makes them again when either changed.
        private FlowFieldSession[] _agentSessions = Array.Empty<FlowFieldSession>();
        private string[] _agentSessionNames = Array.Empty<string>();
        private FlowFieldSpaceMode _agentSessionMode;

        private FlowFieldAgentTypeSettings[] AgentTypeSettings => _agentTypes ?? Array.Empty<FlowFieldAgentTypeSettings>();

        // The session of an additional type, for the Editor-only tests.
        internal FlowFieldSession AgentSession(int index) => _agentSessions[index];

        // A session holds its agent types from Init to Release.
        private bool SessionHoldsAgentTypes => IsInitialized || IsFaulted;

        /// <inheritdoc/>
        public int AgentTypeCount => 1 + (SessionHoldsAgentTypes ? _agentSessionNames.Length : AgentTypeSettings.Length);

        /// <inheritdoc/>
        public bool TryGetAgentType(string name, out FlowFieldAgentType type)
        {
            type = default;
            if (name == null)
                return false;
            if (name == FlowFieldAgentType.DefaultName)
                return true;
            if (SessionHoldsAgentTypes)
            {
                for (int i = 0; i < _agentSessionNames.Length; i++)
                {
                    if (string.Equals(_agentSessionNames[i], name, StringComparison.Ordinal))
                    {
                        type = new FlowFieldAgentType(i + 1);
                        return true;
                    }
                }
                return false;
            }
            FlowFieldAgentTypeSettings[] settings = AgentTypeSettings;
            for (int i = 0; i < settings.Length; i++)
            {
                if (string.Equals(settings[i].Name, name, StringComparison.Ordinal))
                {
                    type = new FlowFieldAgentType(i + 1);
                    return true;
                }
            }
            return false;
        }

        /// <inheritdoc/>
        public bool IsAgentTypeReady(FlowFieldAgentType type) => SessionFor(type)?.IsReady ?? false;

        /// <inheritdoc/>
        public bool TrySample(Vector3 worldPosition, FlowFieldAgentType type, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            return SessionFor(type)?.TrySample(worldPosition, out sample) ?? false;
        }

        /// <inheritdoc/>
        public bool IsGoalReady(FlowFieldGoalHandle goal, FlowFieldAgentType type)
            => goal.IsValid && (SessionFor(type)?.IsGoalReady(goal.Id) ?? false);

        /// <inheritdoc/>
        public bool TrySample(Vector3 worldPosition, FlowFieldGoalHandle goal, FlowFieldAgentType type, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            return goal.IsValid && (SessionFor(type)?.TrySampleGoal(goal.Id, worldPosition, out sample) ?? false);
        }

        /// <inheritdoc/>
        public bool TrySampleNearestShared(
            Vector3 worldPosition,
            FlowFieldAgentType type,
            out FlowFieldSample sample,
            out FlowFieldGoalHandle goal)
        {
            sample = FlowFieldSample.Stopped;
            goal = default;
            FlowFieldSession session = SessionFor(type);
            if (session == null || !session.TrySampleNearestShared(worldPosition, out sample, out int id))
                return false;
            goal = new FlowFieldGoalHandle(id);
            return true;
        }

        // The agent sessions answer only beside the active primary session.
        private FlowFieldSession SessionFor(FlowFieldAgentType type)
        {
            FlowFieldSession primary = ActiveSession;
            if (primary == null || type.IsDefault)
                return primary;
            int index = type.Index - 1;
            return index >= 0 && index < _agentSessions.Length ? _agentSessions[index] : null;
        }

        // ----- Settings -------------------------------------------------------

        /// <summary>
        /// The settings of an additional type: the Manager's, with the type's
        /// clearance, slope and step height. A type builds from the scene in
        /// both modes that take one (RuntimeDynamic, BakedBase), since a bake
        /// holds the Manager's own type only.
        /// </summary>
        private FlowFieldManagerConfig CaptureAgentConfig(FlowFieldSpaceMode spaceMode, int index)
        {
            FlowFieldAgentTypeSettings type = AgentTypeSettings[index];
            FlowFieldSurfaceSettings surface = _surface;
            surface.MaxSlope = type.MaxSlope;
            surface.MaxStepHeight = type.MaxStepHeight;
            FlowFieldObstacleSettings obstacles = _obstacles;
            obstacles.Clearance = type.Clearance;
            FlowFieldFlowSettings flow = _flow;
            flow.GoalInfluenceRadius = ConfiguredGoalRadius;
            FlowFieldBakeAssets noBake = default;
            return new FlowFieldManagerConfig(
                spaceMode,
                FlowFieldBakeMode.RuntimeDynamic,
                in _grid,
                in surface,
                in obstacles,
                in flow,
                in noBake,
                transform,
                HasConfiguredGoal,
                ConfiguredGoalWorld);
        }

        /// <summary>
        /// The checks of the additional types. A running session holds its
        /// types, so a changed number or name faults it, as a Bake Mode edit
        /// does; their values apply at once.
        /// </summary>
        private void ValidateAgentTypes(FlowFieldSpaceMode spaceMode, bool sessionHoldsTypes)
        {
            FlowFieldAgentTypeSettings[] types = AgentTypeSettings;
            if (sessionHoldsTypes && !MatchesAgentSessions(types))
                throw new InvalidOperationException(
                    "The number and names of Additional Agent Types can change only after Release.");
            if (types.Length == 0)
                return;
            if (types.Length > MaxAdditionalAgentTypes)
                throw new InvalidOperationException(
                    $"At most {MaxAdditionalAgentTypes} Additional Agent Types are supported.");
            if (_bakeMode == FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException(
                    "StaticBaked loads the Default agent type only; remove the Additional Agent Types.");
            for (int i = 0; i < types.Length; i++)
            {
                string name = types[i].Name;
                if (string.IsNullOrEmpty(name) || name == FlowFieldAgentType.DefaultName)
                    throw new InvalidOperationException(
                        $"Additional Agent Type {i + 1} needs a name other than \"{FlowFieldAgentType.DefaultName}\".");
                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(types[j].Name, name, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Agent type name \"{name}\" is used twice.");
                }
                FlowFieldManagerConfig config = CaptureAgentConfig(spaceMode, i);
                if (!FlowFieldSpaceStrategy.For(spaceMode).TryValidate(
                        in config, bakeModeLocked: false, validateStaticGoal: false, out string reason))
                    throw new InvalidOperationException($"Agent type \"{name}\": {reason}");
            }
        }

        private bool MatchesAgentSessions(FlowFieldAgentTypeSettings[] types)
        {
            if (types.Length != _agentSessionNames.Length)
                return false;
            for (int i = 0; i < types.Length; i++)
            {
                if (!string.Equals(types[i].Name, _agentSessionNames[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        // ----- Lifecycle ------------------------------------------------------

        // Init, before the sessions start: a session per type in the primary
        // session's mode, made again when the types or the mode changed.
        private void EnsureAgentSessions()
        {
            FlowFieldAgentTypeSettings[] types = AgentTypeSettings;
            if (_agentSessionMode == _spaceMode && MatchesAgentSessions(types))
                return;
            DisposeAgentSessions();
            var sessions = new FlowFieldSession[types.Length];
            var names = new string[types.Length];
            for (int i = 0; i < types.Length; i++)
            {
                FlowFieldSession agent = FlowFieldSpaceStrategy.For(_spaceMode).CreateSession(_boundary);
                string name = types[i].Name;
                agent.FieldCommitted += OnAgentFieldCommitted;
                agent.StateChanged += state => OnAgentStateChanged(agent, name, state);
                agent.EventFailed += OnSessionEventFailed;
                sessions[i] = agent;
                names[i] = name;
            }
            _agentSessions = sessions;
            _agentSessionNames = names;
            _agentSessionMode = _spaceMode;
        }

        private void DisposeAgentSessions()
        {
            for (int i = 0; i < _agentSessions.Length; i++)
                _agentSessions[i].Dispose();
            _agentSessions = Array.Empty<FlowFieldSession>();
            _agentSessionNames = Array.Empty<string>();
        }

        private void InitializeAgentSessions()
        {
            for (int i = 0; i < _agentSessions.Length; i++)
                _agentSessions[i].Initialize(FlowFieldBakeMode.RuntimeDynamic);
        }

        private void SubmitAgentConfigs(FlowFieldConfigChange change)
        {
            FlowFieldSpaceMode spaceMode = SpaceMode;
            int count = Mathf.Min(_agentSessions.Length, AgentTypeSettings.Length);
            for (int i = 0; i < count; i++)
            {
                FlowFieldManagerConfig config = CaptureAgentConfig(spaceMode, i);
                if (_agentSessions[i].SubmitConfig(in config, change) && change == FlowFieldConfigChange.Rebuild)
                    _agentSessions[i].AttachRuntimeScheduler();
            }
        }

        private void StopAgentSessions()
        {
            for (int i = 0; i < _agentSessions.Length; i++)
                _agentSessions[i].Stop();
        }

        // Starts each type again from nothing, whether it runs, stopped or
        // faulted, as the primary session's retry and enabling do. A type
        // that faulted on its own skipped the Goal input since; it takes the
        // primary session's Goals again.
        private void RestartAgentSessions()
        {
            for (int i = 0; i < _agentSessions.Length; i++)
            {
                FlowFieldSession agent = _agentSessions[i];
                agent.Stop();
                agent.Restart();
                SyncAgentGoals(agent);
            }
        }

        private void SyncAgentGoals(FlowFieldSession agent)
        {
            FlowFieldGoalSet primary = _session.Goals;
            FlowFieldGoalSet goals = agent.Goals;
            for (int i = goals.Count - 1; i >= 0; i--)
            {
                int id = goals.Entries[i].Id;
                if (primary.Find(id) == null)
                    goals.Remove(id);
            }
            for (int i = 0; i < primary.Count; i++)
            {
                FlowFieldGoalSet.Entry entry = primary.Entries[i];
                if (goals.Find(entry.Id) == null)
                    goals.Add(entry.Kind, entry.Request, entry.Id);
                else
                    goals.TrySetRequest(entry.Id, entry.Request);
            }
        }

        private void ObserveAgentModifiers()
        {
            for (int i = 0; i < _agentSessions.Length; i++)
                _agentSessions[i].ObserveModifiers();
        }

        private void ReleaseAgentSessions()
        {
            for (int i = 0; i < _agentSessions.Length; i++)
                _agentSessions[i].Release();
        }

        // ----- Events ---------------------------------------------------------

        // A type's new field is a new Revision of the Manager.
        private void OnAgentFieldCommitted() => _session?.AdvanceRevision();

        // A type that faults faults the Manager, with the type's cause.
        private void OnAgentStateChanged(FlowFieldSession agent, string name, FlowFieldRuntimeState state)
        {
            if (state != FlowFieldRuntimeState.Faulted || _session == null || _session.IsFaulted || !_session.IsInitialized)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            _session.ReportFault(new InvalidOperationException(
                $"Agent type \"{name}\" failed: {agent.LastError}", agent.Fault));
        }
    }
}
