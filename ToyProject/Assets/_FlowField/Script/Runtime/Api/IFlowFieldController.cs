using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The contract other systems use to change the FlowField inputs.
    /// FlowField does not poll the scene for them; only the obstacle sweep the settings can
    /// enable looks for unregistered obstacles. From an obstacle change on, samples in the cells
    /// it reaches stop until a build that includes it is published.
    /// </summary>
    public interface IFlowFieldController : IFlowFieldProvider
    {
        /// <summary>
        /// Starts a session. Play Mode only; Edit Mode uses Bake and the preview. Does nothing when
        /// already initialized. Builds run in the frame pump afterwards, so no field is ready when
        /// Init returns.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">
        /// Called in Edit Mode or from Modifier code, while faulted, while initialized with an
        /// edited Space Mode, or with invalid settings (the message names the first).
        /// </exception>
        void Init();
        /// <summary>
        /// Builds again from the current settings, querying every obstacle again. A faulted session
        /// retries: it starts again from nothing and keeps its registered obstacles and Modifiers,
        /// which are read again; one that cannot run now only leaves the fault. A stopped session
        /// only checks the settings and builds when it starts.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized, called from Modifier code, or the settings are invalid; these are checked
        /// before anything changes. A retry also throws when the Space Mode was edited during the
        /// fault, before anything changes, and when its checks fail, which faults it again.
        /// </exception>
        void RequestRebuild();
        /// <summary>
        /// Ends the session, a faulted one too, and clears its input: registered obstacles and
        /// Modifiers and the Goal. The State becomes Released and Init may follow.
        /// Does nothing when no session is initialized or faulted.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">Called from Modifier code.</exception>
        void Release();
        /// <summary>Selects the space mode the next Init uses.</summary>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// <paramref name="mode"/> is not a defined value.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// A session is initialized or faulted (Release first), or called from Modifier code.
        /// </exception>
        void SetSpaceMode(FlowFieldSpaceMode mode);
        /// <summary>
        /// Sets the runtime Goal, or clears it with <see cref="FlowFieldGoalRequest.None"/>, and rebuilds
        /// the Goal directions. A Goal within 0.0001 world units with the same Influence Radius, or a
        /// clear without a Goal, rebuilds nothing. Invalid current settings fault the session instead
        /// of throwing; the new Goal stays for the retry.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized, faulted, in StaticBaked mode, or called from Modifier code.
        /// </exception>
        void SetGoal(in FlowFieldGoalRequest request);
        /// <summary>
        /// Adds a Goal besides the default one. It gets its own field on the same ground,
        /// obstacles and Modifiers, built after the default Goal's field and again whenever
        /// anything but the default Goal changes. Read it with
        /// <see cref="IFlowFieldProvider.TrySample(UnityEngine.Vector3, FlowFieldGoalHandle, out FlowFieldSample)"/>.
        /// Release removes every additional Goal.
        /// </summary>
        /// <returns>The handle that names the new Goal.</returns>
        /// <exception cref="System.ArgumentException"><paramref name="request"/> has no Goal.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// <paramref name="kind"/> is not a defined value.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized, faulted, in StaticBaked mode, or called from Modifier code.
        /// </exception>
        FlowFieldGoalHandle AddGoal(FlowFieldGoalKind kind, in FlowFieldGoalRequest request);
        /// <summary>
        /// Moves an additional Goal. A position within 0.0001 world units with the same Influence
        /// Radius rebuilds nothing. Its field keeps answering until the new one is built.
        /// </summary>
        /// <returns>False when the handle names no Goal of this Manager.</returns>
        /// <exception cref="System.ArgumentException"><paramref name="request"/> has no Goal.</exception>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized, faulted, or called from Modifier code.
        /// </exception>
        bool SetGoal(FlowFieldGoalHandle goal, in FlowFieldGoalRequest request);
        /// <summary>
        /// Removes an additional Goal and its field; the handle names no Goal afterwards. A faulted
        /// session takes it as well.
        /// </summary>
        /// <returns>False when the handle names no Goal of this Manager.</returns>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized and not faulted, or called from Modifier code.
        /// </exception>
        bool RemoveGoal(FlowFieldGoalHandle goal);
        /// <summary>
        /// Rebuilds every cell from the scene: Surface2D scans the ground and every obstacle again,
        /// Volume3D queries every obstacle again. Invalid current settings fault the session
        /// instead of throwing.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized, faulted, in StaticBaked or BakedBase mode, or called from Modifier code.
        /// </exception>
        void NotifyCellsDirty();

        /// <summary>
        /// Registers a Collider as a dynamic obstacle and rebuilds the cells it reaches. Its moves
        /// must be reported with <see cref="NotifyDynamicObstacleMoved"/>. From Modifier code it
        /// runs after that operation and returns true.
        /// </summary>
        /// <returns>False when it is already registered.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="collider"/> is null.</exception>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized, faulted, the Collider is destroyed, or in StaticBaked mode.
        /// </exception>
        bool RegisterDynamicObstacle(Collider collider);
        /// <summary>
        /// Removes a registered obstacle, a destroyed one too, and rebuilds the cells it blocked.
        /// A faulted session takes it as well, so its retry leaves the obstacle out. From Modifier
        /// code it runs after that operation and returns true.
        /// </summary>
        /// <returns>False when it is not registered, and always in StaticBaked mode.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="collider"/> is null.</exception>
        /// <exception cref="System.InvalidOperationException">Not initialized and not faulted.</exception>
        bool UnregisterDynamicObstacle(Collider collider);
        /// <summary>
        /// Reports that a registered obstacle moved.
        /// FlowField never polls the scene: a registered obstacle that moves
        /// must say so, or its cells keep their old state. The cells of its last reported bounds
        /// and of its bounds now are rebuilt; unchanged bounds rebuild nothing. A destroyed
        /// Collider stops being registered.
        /// </summary>
        /// <returns>False when the Collider is not registered.</returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="collider"/> is null.</exception>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized, faulted, in StaticBaked mode, or called from Modifier code.
        /// </exception>
        bool NotifyDynamicObstacleMoved(Collider collider);
        /// <summary>
        /// Reports that unregistered obstacles changed inside <paramref name="worldBounds"/>: the
        /// cells the region reaches are queried again. A region that reaches no cell rebuilds nothing.
        /// </summary>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// <paramref name="worldBounds"/> is not finite.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized, faulted, in StaticBaked mode, or called from Modifier code.
        /// </exception>
        void NotifyObstacleRegionDirty(Bounds worldBounds);

        /// <summary>
        /// Registers a Modifier, reads it with <see cref="IFlowFieldVectorModifier.Observe"/> and
        /// composes it from the next build. Modifiers compose in ascending Priority. From Modifier
        /// code it runs after that operation and returns true.
        /// </summary>
        /// <returns>False when it is already registered.</returns>
        /// <exception cref="System.ArgumentNullException">
        /// <paramref name="modifier"/> is null or destroyed.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized, faulted, it has no Influence Collider, or a registered Modifier has its Priority.
        /// </exception>
        /// <exception cref="System.ArgumentException">
        /// The Influence Collider is not a trigger, or is a non-convex MeshCollider.
        /// </exception>
        bool RegisterVectorModifier(IFlowFieldVectorModifier modifier);
        /// <summary>
        /// Removes a Modifier, a destroyed one too; the next build composes without it. A faulted
        /// session takes it as well, so its retry leaves it out. From Modifier code it runs after
        /// that operation and returns true.
        /// </summary>
        /// <returns>False when <paramref name="modifier"/> is null or not registered.</returns>
        /// <exception cref="System.InvalidOperationException">Not initialized and not faulted.</exception>
        bool UnregisterVectorModifier(IFlowFieldVectorModifier modifier);
        /// <summary>
        /// Reports that a registered Modifier changed: the session reads it again and rebuilds what
        /// changed. A different <see cref="FlowFieldModifierObservation.Revision"/> counts as a value
        /// change. <see cref="FlowFieldModifierChange.None"/> returns before any other check. An
        /// invalid Influence Collider or a Priority another Modifier has faults the session instead
        /// of throwing. From Modifier code it runs after that operation, and a failure is logged.
        /// </summary>
        /// <exception cref="System.ArgumentNullException"><paramref name="modifier"/> is null.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// <paramref name="change"/> has undefined flags.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        /// Not initialized, faulted, or the Modifier is not registered.
        /// </exception>
        void NotifyModifierChanged(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change);
    }
}
