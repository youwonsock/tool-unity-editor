using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>How a follower advances through a path.</summary>
    public enum EPathMoveType
    {
        TimeBased = 0,
        SpeedBased = 1,
    }

    public enum EPathFollowerState
    {
        Uninitialized = 0,
        Ready = 1,
        Moving = 2,
        Paused = 3,
        Completed = 4,
    }

    /// <summary>
    /// The euler axes <see cref="PathFollower.AlignRotationToPathDirection"/> takes
    /// from the path direction. The other axes keep their current rotation.
    /// </summary>
    [Flags]
    public enum EPathAlignRotationAxes
    {
        None = 0,
        X = 1 << 0,
        Y = 1 << 1,
        Z = 1 << 2,
        All = X | Y | Z,
    }

    [Serializable]
    public enum EPathCurveType
    {
        Linear = 0,
        SplineApproximating = 1,
        SplineInterpolating = 2,
    }

    /// <summary>
    /// A path geometry that playback can sample. <see cref="Revision"/> rises on
    /// every published change, <see cref="DefinitionRevision"/> only when the
    /// movement settings, the events or the segment structure change. A playback
    /// keeps going through a geometry change and stops at a definition change.
    /// </summary>
    internal interface IPathProvider
    {
        bool IsReady { get; }
        int Revision { get; }
        int DefinitionRevision { get; }
        float PathLength { get; }
        event Action PathChanged;

        /// <summary>The point at a progress of 0..1 along the path's length.</summary>
        Vector3 Sample(float progress);

        /// <summary>The playback route of the current revision, with the provider's own segments and movement.</summary>
        PathRoute GetRoute();
    }

    /// <summary>Hears the name of every event a follower reaches, after the event's effects applied.</summary>
    public interface IPathEventReceiver
    {
        void OnPathEvent(string eventName, PathFollower follower);
    }
}
