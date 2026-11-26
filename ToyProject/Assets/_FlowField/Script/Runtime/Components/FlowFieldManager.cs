using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Unity component of a FlowField. It holds the serialized settings
    /// and the Unity callbacks, and forwards the Provider and Controller API
    /// to one <see cref="FlowFieldSession"/>, which owns all calculation state.
    /// Files: this one (settings, lifecycle, events), .Api (public calls) and
    /// .Config (the settings snapshot the builders check and build from).
    /// Nothing here polls the scene: every change arrives as a call, and only
    /// the obstacle sweep the settings can enable looks for unregistered ones.
    /// </summary>
    [DefaultExecutionOrder(-300)]
    public partial class FlowFieldManager : MonoBehaviour, IFlowFieldController
    {

        // Serialized as bundles; FlowFieldManagerSettings.cs holds the
        // defaults and PropertyNames below the paths Editor code uses.
        [SerializeField] private FlowFieldGridSettings _grid = FlowFieldGridSettings.Default;
        [SerializeField] private FlowFieldSurfaceSettings _surface = FlowFieldSurfaceSettings.Default;
        [SerializeField] private FlowFieldObstacleSettings _obstacles = FlowFieldObstacleSettings.Default;
        [SerializeField] private FlowFieldFlowSettings _flow = FlowFieldFlowSettings.Default;
        [SerializeField, Tooltip("The Goal position Init and Bake read once. Report later Goal moves with SetGoal or FlowFieldGoalTarget.")]
        private Transform _goalTransform;

        [Header("Mode")]
        [SerializeField, Tooltip("Surface2D raycasts down onto the ground; Volume3D fills the whole Bounds with cubic cells. Changing it after Init requires Release.")]
        private FlowFieldSpaceMode _spaceMode = FlowFieldSpaceMode.Surface2D;
        [SerializeField, Tooltip("RuntimeDynamic computes the Surface, obstacles and Goal at runtime; StaticBaked loads the whole field baked in the Editor; BakedBase loads the baked Surface and obstacles and changes the Goal and obstacles at runtime.")]
        private FlowFieldBakeMode _bakeMode = FlowFieldBakeMode.RuntimeDynamic;
        [SerializeField] private FlowFieldBakeAssets _bakeAssets;

        /// <summary>Serialized property paths, for SerializedObject access.</summary>
        internal static class PropertyNames
        {
            internal const string Grid = nameof(_grid);
            internal const string BakeBoundsLocal = Grid + "." + nameof(FlowFieldGridSettings.BakeBoundsLocal);
            internal const string CellSize = Grid + "." + nameof(FlowFieldGridSettings.CellSize);
            internal const string Surface = nameof(_surface);
            internal const string GroundLayer = Surface + "." + nameof(FlowFieldSurfaceSettings.GroundLayer);
            internal const string Obstacles = nameof(_obstacles);
            internal const string ObstacleLayer = Obstacles + "." + nameof(FlowFieldObstacleSettings.Layer);
            internal const string Flow = nameof(_flow);
            internal const string AgentTypes = nameof(_agentTypes);
            internal const string GoalTransform = nameof(_goalTransform);
            internal const string SpaceMode = nameof(_spaceMode);
            internal const string BakeMode = nameof(_bakeMode);
            internal const string BakeAssets = nameof(_bakeAssets);
            internal const string SurfaceBakeData = BakeAssets + "." + nameof(FlowFieldBakeAssets.Surface);
            internal const string VolumeBakeData = BakeAssets + "." + nameof(FlowFieldBakeAssets.Volume);
        }

        // The boundary outlives each session, so events of a replaced session
        // and its successor keep their order and Modifiers of either mode see
        // the same command rules.
        private readonly FlowFieldExecutionBoundary _boundary = new FlowFieldExecutionBoundary();
        // One session at a time, created by Init for the configured mode. It
        // stays after Release so its final state remains readable, and is
        // replaced only when Init runs in the other mode.
        private FlowFieldSession _session;
        // The current session, active or not, for the Editor-only tests.
        internal FlowFieldSession Session => _session;
        private bool _hasExplicitGoal;
        private Vector3 _explicitGoalWorld;
        // SetGoal's Influence Radius belongs to its Goal. The serialized Goal
        // Influence Radius stays as authored: the Goal Transform uses it, at
        // every Init and Bake, and so does an Inspector edit of it in Play.
        private float? _explicitGoalRadius;
        // Advances each time Release drops the Goal, so FlowFieldGoalTarget
        // can tell its Goal is gone even when Release and Init run in one frame.
        internal int GoalLifetime { get; private set; }
        // Advances each time the Goal is set or dropped (SetGoal, also with an
        // equal Goal; the Goal Transform at Init; Release; a clear while
        // faulted), so FlowFieldGoalTarget clears only a Goal it set itself,
        // not one a script or another target set after it.
        internal int GoalVersion { get; private set; }
        [NonSerialized] private FlowFieldModifierAttachment _modifiers;

        // The session the façade answers from. The accepted session keeps
        // answering until Release even if the serialized mode is edited; a
        // stopped session of the other mode is no longer the Manager's.
        private FlowFieldSession ActiveSession
            => _session != null
                && (_session.IsInitialized || _session.IsFaulted || _session.SpaceMode == _spaceMode)
                    ? _session
                    : null;

        private FlowFieldModifierAttachment Modifiers => _modifiers ??= new FlowFieldModifierAttachment(this);

        /// <inheritdoc/>
        public bool IsInitialized => ActiveSession?.IsInitialized ?? false;
        /// <inheritdoc/>
        public FlowFieldRuntimeState State => ActiveSession?.State ?? FlowFieldRuntimeState.Uninitialized;
        /// <inheritdoc/>
        public bool IsFaulted => ActiveSession?.IsFaulted ?? false;
        /// <inheritdoc/>
        public bool IsReady => ActiveSession?.IsReady ?? false;
        /// <inheritdoc/>
        public bool IsRebuilding => ActiveSession?.IsRebuilding ?? false;
        /// <inheritdoc/>
        public string LastError => ActiveSession?.LastError;
        /// <summary>
        /// The Bake Mode the session accepted while it is initialized or faulted; otherwise the
        /// serialized one. Editing the serialized Bake Mode while a session runs faults it.
        /// </summary>
        public FlowFieldBakeMode BakeMode => IsInitialized || IsFaulted
            ? ActiveSession.BakeMode
            : _bakeMode;
        /// <summary>
        /// The Space Mode the session accepted while it is initialized or faulted; otherwise the
        /// serialized one. Inspector edits made while initialized are authoring input only: they
        /// wait for Release, and Provider queries keep describing the session's space.
        /// </summary>
        public FlowFieldSpaceMode SpaceMode => IsInitialized || IsFaulted
            ? ActiveSession.SpaceMode
            : _spaceMode;
        /// <summary>
        /// Advances each time a build publishes a field that reads differently. The count goes on
        /// across sessions, so it never goes back, even while an edited Space Mode leaves no active
        /// session; 0 before the first field.
        /// </summary>
        public int Revision => _session?.Revision ?? 0;
        /// <inheritdoc/>
        public event Action FieldChanged;
        /// <summary>
        /// Raised with the new <see cref="State"/> when it changes, delivered as
        /// <see cref="FieldChanged"/> is; a queued Released is never dropped. Before Faulted reaches the
        /// handlers, the Manager logs <see cref="LastError"/> as an error.
        /// </summary>
        public event Action<FlowFieldRuntimeState> StateChanged;

        /// <inheritdoc/>
        public bool TryGetFieldInfo(out FlowFieldFieldInfo info)
        {
            info = default;
            return ActiveSession?.TryGetFieldInfo(out info) ?? false;
        }

        /// <summary>
        /// The published field of the active session, for display. It is valid
        /// until the next pump, after which its arrays may be reused.
        /// </summary>
        internal bool TryGetPublishedField(out FlowFieldField field)
        {
            field = null;
            return ActiveSession?.TryGetPublishedField(out field) ?? false;
        }

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void OnEnable()
        {
            if (!Application.isPlaying || _boundary.TryDefer(ResumeAfterEnable))
                return;
            ResumeAfterEnable();
        }

        // Disabling stopped the session, so enabling starts it again.
        private void ResumeAfterEnable()
        {
            if (State != FlowFieldRuntimeState.Suspended)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            RestartSession(_session);
        }

        private void OnValidate()
        {
            if (Application.isPlaying)
                OnSettingsChanged();
        }

        /// <summary>
        /// A serialized setting changed while a session runs. Valid settings
        /// rebuild what they change; invalid ones fault the session, the same
        /// in both modes. While a Space Mode edit waits for Release, the other
        /// settings still apply in the session's mode.
        /// </summary>
        private void OnSettingsChanged()
        {
            if (!IsInitialized)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            SubmitCheckedConfig(FlowFieldConfigChange.Settings);
        }

        /// <summary>
        /// An Inspector edit, SetGoal and NotifyCellsDirty submit the live
        /// settings, which can stop being valid without an edit: a moved
        /// BakedBase Manager no longer matches its bake. Invalid settings fault
        /// the session the same way for all three instead of throwing to the
        /// caller. A Space Mode edit waits for Release, so the settings are
        /// checked in the session's mode.
        /// </summary>
        private void SubmitCheckedConfig(FlowFieldConfigChange change)
        {
            try
            {
                ValidateSessionConfiguration();
                SubmitConfig(change);
            }
            catch (Exception exception)
            {
                ActiveSession.ReportFault(exception);
            }
        }

        private void OnDisable()
        {
            if (!Application.isPlaying || _boundary.TryDefer(SuspendAfterDisable))
                return;
            SuspendAfterDisable();
        }

        private void SuspendAfterDisable()
        {
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            if (ActiveSession == null)
                return;
            ActiveSession.Stop();
            StopAgentSessions();
        }

        private void OnDestroy()
        {
            if (!_boundary.TryDefer(DisposeSessions))
                DisposeSessions();
        }

        private void DisposeSessions()
        {
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            _session?.Dispose();
            DisposeAgentSessions();
        }

        // Init reuses the session of the configured mode. A session of the
        // other mode is disposed; its handlers stay until its Disposed event,
        // so a Released still queued before it reaches subscribers.
        private void EnsureSessionForConfiguredMode()
        {
            if (_session != null && _session.SpaceMode == _spaceMode)
                return;
            int revision = _session?.Revision ?? 0;
            _session?.Dispose();
            _session = FlowFieldSpaceStrategy.For(_spaceMode).CreateSession(_boundary);
            _session.ContinueRevision(revision);
            ForwardSessionEvents(_session);
        }

        /// <summary>
        /// Starts a session in the serialized Space Mode and Bake Mode. Awake calls it in Play Mode;
        /// after Release, call it again. It reads the Goal Transform when no Goal is set. A disabled
        /// Manager submits its request at once, so ClampPositionToGrid and obstacle input work, and
        /// builds when it is enabled. Does nothing when already initialized. Builds run in the
        /// frame pump afterwards, so no field is ready when Init returns.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Called in Edit Mode or from Modifier code, while faulted, while initialized with an
        /// edited Space Mode, or with invalid settings (the message names the first).
        /// </exception>
        public void Init()
        {
            _boundary.ThrowIfInUserCode(nameof(Init));
            // Edit Mode does not call OnDestroy for this component, so a
            // session started there would stay in the runtime scheduler after
            // the Manager is gone and build in every later Play session.
            if (!Application.isPlaying)
                throw FlowFieldFailures.InitOutsidePlayMode();
            if (IsInitialized && _spaceMode != SpaceMode)
                throw FlowFieldFailures.SpaceModeLocked();
            if (IsFaulted)
                throw FlowFieldFailures.Faulted(ActiveSession.Fault);
            if (IsInitialized)
                return;
            ValidateConfiguration();
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            AdoptGoalTransform();
            EnsureSessionForConfiguredMode();
            EnsureAgentSessions();
            _session.Initialize(_bakeMode);
            InitializeAgentSessions();
            // A disabled Manager starts building when it is enabled. It still
            // holds the request, so its grid answers ClampPositionToGrid and
            // takes obstacle input the same way in both modes.
            if (!isActiveAndEnabled)
            {
                _session.Stop();
                StopAgentSessions();
                SubmitConfig(FlowFieldConfigChange.Rebuild);
                return;
            }
            StartBuilding();
        }

        /// <inheritdoc/>
        public void RequestRebuild()
        {
            _boundary.ThrowIfInUserCode(nameof(RequestRebuild));
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            FlowFieldSession owner = ActiveSession;
            if (owner != null && owner.IsFaulted)
            {
                // A retry keeps the session's Space Mode, as a running session
                // does: an edit waits for Release, so it is refused here before
                // anything changes and the fault keeps its cause.
                if (_spaceMode != owner.SpaceMode)
                    throw FlowFieldFailures.SpaceModeLocked();
                // A retry starts again from nothing. A disabled Manager only
                // leaves the fault, and starts when it is enabled.
                if (isActiveAndEnabled)
                {
                    RestartSession(owner);
                }
                else
                {
                    owner.Restart();
                    RestartAgentSessions();
                    owner.Stop();
                    StopAgentSessions();
                }
                return;
            }

            ThrowIfLifecycleUnavailable();
            // Static navigation is immutable for the lifetime of a Session.
            // Init validates the baked Goal signature; a rebuild validates
            // only the loaded snapshot's settings, as the baked Goal stays.
            // A Space Mode edit waits for Release, so the rebuild keeps the
            // session's mode like every other input. Invalid settings still
            // throw here, before anything changes.
            ValidateSessionConfiguration();
            if (State == FlowFieldRuntimeState.Suspended)
                return;
            SubmitConfig(FlowFieldConfigChange.Rebuild);
        }

        /// <summary>
        /// Enabling and a fault retry start the session again from nothing,
        /// keeping registered obstacles and Modifiers, whose settings are read
        /// again. A failure faults it, so it keeps a LastError to diagnose.
        /// A Space Mode edit waits for Release, as it does while the session
        /// runs: enabling keeps the session's mode and, like a submit, checks
        /// the settings in that mode. A fault retry refuses the edit before it
        /// gets here.
        /// </summary>
        private void RestartSession(FlowFieldSession owner)
        {
            owner.Restart();
            RestartAgentSessions();
            try
            {
                owner.ObserveModifiers();
                ObserveAgentModifiers();
                ValidateSessionConfiguration();
                StartBuilding();
            }
            catch (Exception exception)
            {
                owner.ReportFault(exception);
                throw;
            }
        }

        // The Goal Transform is an initial Goal: Init reads it once, and from
        // then on only SetGoal changes the Goal. Release drops that Goal, so
        // the next Init reads the Goal Transform again.
        private void AdoptGoalTransform()
        {
            if (_goalTransform == null || _hasExplicitGoal)
                return;
            ChangeExplicitGoal(hasGoal: true, _goalTransform.position);
        }

        // A Goal without its own radius (the Goal Transform's) uses the
        // serialized one.
        private void ChangeExplicitGoal(bool hasGoal, Vector3 worldPosition, float? radius = null)
        {
            _hasExplicitGoal = hasGoal;
            _explicitGoalWorld = worldPosition;
            _explicitGoalRadius = hasGoal ? radius : null;
            unchecked { GoalVersion++; }
        }

        // The builder turns the current settings into its request. A full
        // request is what the runtime scheduler builds from, so it starts here.
        // The session's Space Mode: a Space Mode edit waits for Release.
        private void SubmitConfig(FlowFieldConfigChange change)
        {
            FlowFieldManagerConfig config = CaptureConfig(SpaceMode, _bakeMode);
            if (_session.SubmitConfig(in config, change) && change == FlowFieldConfigChange.Rebuild)
                _session.AttachRuntimeScheduler();
            SubmitAgentConfigs(change);
        }

        // Modifiers enabled while the session could not take them join
        // before the request, so its first build includes them.
        private void StartBuilding()
        {
            Modifiers.Reattach();
            SubmitConfig(FlowFieldConfigChange.Rebuild);
        }

        /// <summary>
        /// Ends the session, a faulted one too, and clears its input: registered obstacles and
        /// Modifiers and the Goal, so the next Init reads the Goal Transform again. The State
        /// becomes Released. Enabled Modifier Volumes, FlowFieldDynamicObstacle and
        /// FlowFieldGoalTarget components report themselves again after the next Init. Does
        /// nothing when no session is initialized or faulted.
        /// </summary>
        /// <exception cref="InvalidOperationException">Called from Modifier code.</exception>
        public void Release()
        {
            _boundary.ThrowIfInUserCode(nameof(Release));
            FlowFieldSession owner = ActiveSession;
            if (owner == null || !owner.IsInitialized && !owner.IsFaulted)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            Modifiers.Detach();
            owner.Release();
            ReleaseAgentSessions();
            // The Goal is input like the registrations Release clears.
            ChangeExplicitGoal(hasGoal: false, default);
            unchecked { GoalLifetime++; }
        }

        // Subscribed for the Manager's whole life: a queued Released must still
        // arrive after the Manager has moved on from that session.
        private void ForwardSessionEvents(FlowFieldSession session)
        {
            session.FieldCommitted += OnSessionFieldCommitted;
            session.StateChanged += OnSessionStateChanged;
            session.EventFailed += OnSessionEventFailed;
        }

        private void OnSessionFieldCommitted()
            => FieldChanged?.Invoke();

        // Logged before forwarding: a handler may retry at once, which retires
        // the session's Failed event along with the rest of that lifetime. A
        // faulted Manager stops its agent types until the retry.
        private void OnSessionStateChanged(FlowFieldRuntimeState state)
        {
            if (state == FlowFieldRuntimeState.Faulted)
            {
                if (!string.IsNullOrEmpty(LastError))
                    Debug.LogError($"[FlowField] Build failed: {LastError}", this);
                using (_boundary.EnterOperation())
                    StopAgentSessions();
            }
            StateChanged?.Invoke(state);
        }

        private void OnSessionEventFailed(Exception exception)
        {
            if (exception != null)
                Debug.LogException(exception, this);
        }

    }
}
