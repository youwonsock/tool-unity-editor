using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>What changed in the Manager's configuration, so a builder knows what to rebuild.</summary>
    internal enum FlowFieldConfigChange
    {
        // Init, RequestRebuild or a restart: build everything from these settings.
        Rebuild,
        // A serialized setting was edited: rebuild what differs from the last request.
        Settings,
        // SetGoal changed the Goal.
        Goal,
        // NotifyCellsDirty: regenerate every cell.
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
    /// What one space mode does inside the common <see cref="FlowFieldSession"/>:
    /// how it builds a field, reads it, and takes obstacle and Modifier input.
    /// The session owns the lifecycle, the events and the build loop and calls
    /// these members; a builder reaches the session through the reference it
    /// was created with.
    /// </summary>
    internal interface IFlowFieldSpaceBuilder : IDisposable
    {
        FlowFieldSpaceMode SpaceMode { get; }
        bool HasPendingRequest { get; }
        bool IsWaitingForExternalCompletion { get; }

        // ----- Build steps the session's pipeline runs ------------------------
        // A builder never changes the session's state or raises its events: it
        // reports each step's result, and a failure as an exception, and the
        // session does the rest the same way in both modes.
        bool HasActiveBuild { get; }
        // False when nothing is owed; start is filled either way.
        bool TryBeginBuild(out FlowFieldBuildStart start);
        FlowFieldBuildStep StepBuild(in FlowFieldBuildBudget budget, bool waitForWorker);
        // Publishes the completed build; true when the field now reads differently.
        bool PublishBuild();
        FlowFieldBuildPolicy BuildPolicy { get; }
        // Input that arrived during a build is built next; the session stays
        // Building until nothing is owed.
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
        bool TryGetFieldInfo(out FlowFieldFieldInfo info);

        // ----- Configuration -------------------------------------------------
        // The builder turns the Manager's settings into its own request; every
        // change arrives as a notification, never by polling the scene.
        // Initialize prepares a builder that has no request yet. SubmitConfig
        // returns the region new obstacle settings made uncertain, if any.
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
        // CancelWork: drop the build in progress, its callbacks and owed input.
        // DropComputedState: drop the published field and what was computed;
        // the next build is complete and starts from the latest request.
        // ReleaseResources: forget the requests and free what the builder holds.
        // Dispose (after ReleaseResources): free what outlives a Release.
        void CancelWork();
        void DropComputedState();
        void ReleaseResources();
    }
}
