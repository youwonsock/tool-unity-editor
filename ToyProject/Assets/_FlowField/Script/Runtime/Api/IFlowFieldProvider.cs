using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The contract movement systems use to read the current FlowField.
    /// It never pushes samples; callers pull them in their own FixedUpdate.
    /// </summary>
    public interface IFlowFieldProvider
    {
        /// <summary>
        /// Whether a session is running or stopped: true from Init until Release, also while
        /// <see cref="State"/> is Suspended. False before Init, after Release and while faulted.
        /// </summary>
        bool IsInitialized { get; }
        /// <summary>The lifecycle state; Uninitialized while there is no session.</summary>
        FlowFieldRuntimeState State { get; }
        /// <summary>
        /// Whether a published field answers samples, so <see cref="TrySample"/> can succeed inside
        /// its grid. It stays true while <see cref="IsRebuilding"/>: the published field answers
        /// until a newer one replaces it. False while the session is stopped or faulted.
        /// </summary>
        bool IsReady { get; }
        /// <summary>Whether accepted input is waiting for a build to publish it.</summary>
        bool IsRebuilding { get; }
        /// <summary>
        /// Whether a build, a settings check or a Modifier failed. <see cref="Sample"/> and
        /// <see cref="ClampPositionToGrid"/> then throw with the fault as InnerException, and
        /// <see cref="TrySample"/> and <see cref="TryClampPositionToGrid"/> return false.
        /// </summary>
        bool IsFaulted { get; }
        /// <summary>The message of the fault while <see cref="IsFaulted"/>; otherwise null.</summary>
        string LastError { get; }
        /// <summary>
        /// Advances each time a build publishes a field that reads differently, of any Goal and any
        /// agent type, together with <see cref="FieldChanged"/>. It never goes back, also across
        /// Release and a Space Mode change; 0 before the first field.
        /// </summary>
        int Revision { get; }
        /// <summary>
        /// The space mode of the session while it is initialized or faulted; otherwise the one the
        /// next Init uses.
        /// </summary>
        FlowFieldSpaceMode SpaceMode { get; }
        /// <summary>
        /// The bake mode of the session while it is initialized or faulted; otherwise the one the
        /// next Init uses.
        /// </summary>
        FlowFieldBakeMode BakeMode { get; }
        /// <summary>
        /// Raised when a build publishes a field that reads differently, of any Goal and any agent
        /// type, as <see cref="Revision"/> advances. Handlers run after the FlowField operation that raised the event has returned,
        /// one at a time and in order, and may call any command. An exception from a handler is
        /// logged, not thrown. An event still queued when the session stops, restarts, faults or is
        /// released is dropped.
        /// </summary>
        event Action FieldChanged;
        /// <summary>
        /// Raised with the new <see cref="State"/> when it changes, delivered as
        /// <see cref="FieldChanged"/> is. A queued Released is never dropped.
        /// </summary>
        event Action<FlowFieldRuntimeState> StateChanged;

        /// <summary>
        /// Reads the published field at a world position. It never throws. Where the field lags
        /// behind notified obstacle changes the sample stops, and inside a cell that is now
        /// blocked it points out of the cell.
        /// </summary>
        /// <returns>
        /// True with the sample when <see cref="IsReady"/> and the position is finite and inside
        /// the published grid; otherwise false with <see cref="FlowFieldSample.Stopped"/>.
        /// </returns>
        bool TrySample(Vector3 worldPosition, out FlowFieldSample sample);
        /// <summary><see cref="TrySample"/> that throws where it returns false.</summary>
        /// <exception cref="InvalidOperationException">
        /// Not initialized, faulted (InnerException is the fault) or no field is published yet.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="worldPosition"/> is not finite or is outside the grid.
        /// </exception>
        FlowFieldSample Sample(Vector3 worldPosition);

        /// <summary>
        /// The nearest position inside the latest grid, the one the latest request laid out, also
        /// before a build has published on it. Surface2D clamps X and Z and keeps Y; Volume3D
        /// clamps all three axes.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Not initialized, faulted (InnerException is the fault) or no grid is laid out yet.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="worldPosition"/> is not finite.
        /// </exception>
        FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition);
        /// <summary>
        /// <see cref="ClampPositionToGrid"/> without exceptions: false where ClampPositionToGrid
        /// throws, faulted included; it never throws.
        /// </summary>
        bool TryClampPositionToGrid(Vector3 worldPosition, out FlowFieldClampResult result);
        /// <summary>Describes the published field.</summary>
        /// <returns>True while <see cref="IsReady"/>; otherwise false with a default info.</returns>
        bool TryGetFieldInfo(out FlowFieldFieldInfo info);

        /// <summary>
        /// Whether the field of an additional Goal answers samples. It is false until its first
        /// field is built, after the default Goal's field, and again after a new grid, a stop or a
        /// restart until it is built again. Its field keeps answering while a newer one builds.
        /// </summary>
        bool IsGoalReady(FlowFieldGoalHandle goal);
        /// <summary>
        /// Reads the field of an additional Goal at a world position, as <see cref="TrySample(Vector3, out FlowFieldSample)"/>
        /// reads the default one: the same safety rule applies. It never throws.
        /// </summary>
        /// <returns>
        /// True with the sample when <see cref="IsGoalReady"/> and the position is finite and inside
        /// the grid; otherwise false with <see cref="FlowFieldSample.Stopped"/>, also for a handle
        /// that names no Goal of this Manager.
        /// </returns>
        bool TrySample(Vector3 worldPosition, FlowFieldGoalHandle goal, out FlowFieldSample sample);
        /// <summary>
        /// Reads the field of the shared Goal fewest cell steps away from the position, the earliest
        /// added on a tie. Where no shared Goal reaches the position's cell (it is blocked or outside
        /// every Goal's influence), the earliest added ready shared Goal answers, as its own sample
        /// would. Group Goals and the default Goal are not considered. It never throws.
        /// </summary>
        /// <returns>
        /// True with the sample and the Goal that answered; false with
        /// <see cref="FlowFieldSample.Stopped"/> and the default handle when no ready shared Goal's
        /// grid holds the position.
        /// </returns>
        bool TrySampleNearestShared(Vector3 worldPosition, out FlowFieldSample sample, out FlowFieldGoalHandle goal);

        /// <summary>
        /// 1 for <see cref="FlowFieldAgentType.Default"/> plus the Additional Agent Types: the
        /// session's from Init to Release, otherwise the serialized ones.
        /// </summary>
        int AgentTypeCount { get; }
        /// <summary>
        /// Finds an agent type by name: <see cref="FlowFieldAgentType.DefaultName"/> at any time, or
        /// the name of an Additional Agent Type of the session from Init to Release.
        /// </summary>
        /// <returns>False with <see cref="FlowFieldAgentType.Default"/> when no type has the name.</returns>
        bool TryGetAgentType(string name, out FlowFieldAgentType type);
        /// <summary>
        /// Whether the agent type's field answers samples, as <see cref="IsReady"/> says for
        /// <see cref="FlowFieldAgentType.Default"/>. An Additional Agent Type builds beside the default
        /// one and may become ready later. False for a type this Manager does not have.
        /// </summary>
        bool IsAgentTypeReady(FlowFieldAgentType type);
        /// <summary>
        /// <see cref="TrySample(Vector3, out FlowFieldSample)"/> on the agent type's field. It never
        /// throws, and is false for a type this Manager does not have.
        /// </summary>
        bool TrySample(Vector3 worldPosition, FlowFieldAgentType type, out FlowFieldSample sample);
        /// <summary>Whether the additional Goal's field of the agent type answers samples.</summary>
        bool IsGoalReady(FlowFieldGoalHandle goal, FlowFieldAgentType type);
        /// <summary>
        /// <see cref="TrySample(Vector3, FlowFieldGoalHandle, out FlowFieldSample)"/> on the agent
        /// type's field of the additional Goal. It never throws.
        /// </summary>
        bool TrySample(Vector3 worldPosition, FlowFieldGoalHandle goal, FlowFieldAgentType type, out FlowFieldSample sample);
        /// <summary>
        /// <see cref="TrySampleNearestShared(Vector3, out FlowFieldSample, out FlowFieldGoalHandle)"/>
        /// over the agent type's fields of the shared Goals, whose steps follow that type's cells.
        /// It never throws.
        /// </summary>
        bool TrySampleNearestShared(
            Vector3 worldPosition,
            FlowFieldAgentType type,
            out FlowFieldSample sample,
            out FlowFieldGoalHandle goal);
    }

}
