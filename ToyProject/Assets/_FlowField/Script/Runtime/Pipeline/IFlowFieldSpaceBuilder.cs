using UnityEngine;

namespace Common.FlowField
{
    /// <summary>What changed in the Manager's configuration, so a builder knows what to rebuild.</summary>
    internal enum FlowFieldConfigChange
    {
        // Init, RequestRebuild or a restart: build from these settings and
        // query every obstacle again. The ground is kept unless its settings
        // changed; BakedBase starts again from the bake.
        Rebuild,
        // A serialized setting was edited: rebuild what differs from the last request.
        Settings,
        // SetGoal changed the Goal.
        Goal,
        // NotifyCellsDirty: regenerate every cell, the ground included.
        Cells,
    }

    /// <summary>How a mode's builds may begin within one pump.</summary>
    internal readonly struct FlowFieldBuildPolicy
    {
        // A build may begin after the frame budget ran out (Surface2D takes its
        // Physics snapshot in one frame whatever the budget says).
        internal readonly bool BeginsAfterBudget;
        internal readonly int MaxBuildsBegunPerPump;

        internal FlowFieldBuildPolicy(bool beginsAfterBudget, int maxBuildsBegunPerPump)
        {
            BeginsAfterBudget = beginsAfterBudget;
            MaxBuildsBegunPerPump = maxBuildsBegunPerPump;
        }
    }

    /// <summary>What a build that begins tells the session about the uncertain regions.</summary>
    internal readonly struct FlowFieldBuildStart
    {
        // The build queries the obstacles of every uncertain region, so its
        // publication resolves them.
        internal readonly bool ResolvesUncertainRegions;
        // New obstacle settings the build took before it began made this
        // region uncertain.
        internal readonly Bounds? InvalidatedRegion;

        internal FlowFieldBuildStart(bool resolvesUncertainRegions, Bounds? invalidatedRegion)
        {
            ResolvesUncertainRegions = resolvesUncertainRegions;
            InvalidatedRegion = invalidatedRegion;
        }
    }

    /// <summary>
    /// One build a space builder began. The session steps it until it
    /// completes and is published, or is rejected. A build the builder drops
    /// (CancelWork, or newer input it cannot build on) is no longer live, and
    /// the session begins the next one.
    /// </summary>
    internal interface IFlowFieldBuild
    {
        // False once the build was published, rejected or dropped.
        bool IsLive { get; }
        // A solve is running on a worker thread.
        bool IsWaitingForWorker { get; }
        // It builds additional Goals on the published layers: it queries no
        // obstacles and leaves the default Goal's field as it is.
        bool BuildsAdditionalGoals { get; }
        FlowFieldBuildStep Step(in FlowFieldBuildBudget budget, bool waitForWorker);
        // Publishes the completed build; true when the field now reads differently.
        bool Publish();
    }

    /// <summary>
    /// What one space mode does inside the common <see cref="FlowFieldSession"/>:
    /// how it builds a field, reads it, and takes obstacle and Modifier input.
    /// The session owns the lifecycle, the events and the build loop and calls
    /// these members; a builder reads the session only through the
    /// <see cref="IFlowFieldBuildHost"/> it was created with.
    /// </summary>
    internal interface IFlowFieldSpaceBuilder
    {
        FlowFieldSpaceMode SpaceMode { get; }
        // Whether the scheduler has work here: an accepted request not built
        // yet (Volume3D also counts a running build and a Modifier recompose).
        bool HasPendingRequest { get; }

        // ----- Builds the session's pipeline runs -----------------------------
        // A builder never changes the session's state or raises its events: a
        // build reports each step's result, and a failure as an exception, and
        // the session does the rest the same way in both modes.
        // One build runs at a time: the session begins the next one only when
        // the last is no longer live. False, with no build, when nothing is
        // owed; start is filled either way.
        bool TryBeginBuild(out FlowFieldBuildStart start, out IFlowFieldBuild build);
        FlowFieldBuildPolicy BuildPolicy { get; }
        // Input no running build includes: it is built next, and the session
        // stays Building until nothing is owed (Surface2D also owes settings
        // it held back during a solve, Volume3D a Modifier recompose).
        bool HasOwedInput { get; }

        // ----- Reads ---------------------------------------------------------
        // The field reads answer from, or null while none does (no build yet,
        // stopped, or a newer grid or source replaced it). The session reads,
        // samples and clamps through it.
        FlowFieldField PublishedField { get; }
        // What the mode knows about its cells beyond the published field, for
        // the shared sample safety rule; null when it knows nothing newer.
        IFlowFieldLatestCells LatestCells { get; }
        // The latest request's grid: ClampPositionToGrid uses it, before and
        // after the build that publishes it. Valid once a request exists.
        FlowFieldGridSpace LatestGrid { get; }
        // The session owns the Revision the info reports.
        bool TryGetFieldInfo(int revision, out FlowFieldFieldInfo info);

        // ----- Configuration -------------------------------------------------
        // The builder turns the Manager's settings into its own request; every
        // change arrives as a notification, never by polling the scene. The
        // session refuses input while it is not running, so a builder never
        // checks the lifecycle itself.
        // Initialize prepares a builder that has no request yet for the bake
        // mode the session starts with; the builder keeps it until the next
        // Initialize. SubmitConfig returns the region new obstacle settings
        // made uncertain, if any.
        void Initialize(FlowFieldBakeMode bakeMode);
        Bounds? SubmitConfig(in FlowFieldManagerConfig config, FlowFieldConfigChange change);

        // ----- Input ----------------------------------------------------------
        // The session keeps the Modifiers; the builder keeps what each touches
        // (Surface2D cell masks) and composes again after a change.
        IFlowFieldModifierArea CreateModifierArea();
        void ApplyModifierChange(in FlowFieldModifierUpdate update);
        // The session keeps the registered obstacles and the uncertain regions
        // a change touched; the builder only rebuilds them.
        void ApplyObstacleChange(in FlowFieldObstacleChange change);

        // ----- Lifecycle -----------------------------------------------------
        // The session runs every transition (Initialize, Stop, Restart,
        // Release, a fault) and calls these for the mode's own state.
        // CancelWork: drop the build in progress (it is no longer live), its
        // callbacks and owed input.
        // DropComputedState: drop the published field and what was computed;
        // the next build is complete and starts from the latest request.
        // ReleaseResources: forget the requests and free what the builder
        // holds; nothing outlives it.
        void CancelWork();
        void DropComputedState();
        void ReleaseResources();
    }
}
