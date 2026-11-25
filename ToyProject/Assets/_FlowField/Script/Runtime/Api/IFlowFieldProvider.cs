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
        /// Advances each time a build publishes a field that reads differently, together with
        /// <see cref="FieldChanged"/>. It never goes back, also across Release and a Space Mode
        /// change; 0 before the first field.
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
        /// Raised when a build publishes a field that reads differently, as <see cref="Revision"/>
        /// advances. Handlers run after the FlowField operation that raised the event has returned,
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
    }

}
