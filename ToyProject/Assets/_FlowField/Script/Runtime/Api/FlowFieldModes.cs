namespace Common.FlowField
{
    /// <summary>Selects the space a Manager builds its field in.</summary>
    public enum FlowFieldSpaceMode
    {
        /// <summary>
        /// Raycasts down onto the ground and lays one layer of cells over it on the XZ plane.
        /// </summary>
        Surface2D = 0,
        /// <summary>Fills the whole Bake Bounds with cubic cells.</summary>
        Volume3D = 1,
    }

    /// <summary>
    /// Selects where the navigation base field comes from.
    /// </summary>
    public enum FlowFieldBakeMode
    {
        /// <summary>The surface, obstacles and Goal are computed at runtime.</summary>
        RuntimeDynamic = 0,
        /// <summary>The whole baked field is loaded; the Goal and obstacles stay as baked.</summary>
        StaticBaked = 1,
        /// <summary>
        /// The baked surface and obstacles are loaded; the Goal and obstacles
        /// change at runtime on top of them.
        /// </summary>
        BakedBase = 2,
    }

    /// <summary>What each Bake Mode needs, so callers never list the modes.</summary>
    internal static class FlowFieldBakeModes
    {
        /// <summary>Whether the mode loads a bake asset.</summary>
        internal static bool UsesBakeAsset(FlowFieldBakeMode mode)
            => mode == FlowFieldBakeMode.StaticBaked || mode == FlowFieldBakeMode.BakedBase;
    }

    /// <summary>
    /// Observable lifecycle of a flow-field provider.  Values are explicit so
    /// serialized diagnostics and external integrations remain stable.
    /// </summary>
    public enum FlowFieldRuntimeState
    {
        /// <summary>No session has started, or the space mode changed after Release.</summary>
        Uninitialized = 0,
        /// <summary>Initialized, with input that no published field includes yet.</summary>
        Building = 1,
        /// <summary>Initialized, with a published field and nothing left to build.</summary>
        Ready = 2,
        /// <summary>
        /// Stopped while its Manager is disabled; it has no field and starts again when enabled.
        /// </summary>
        Suspended = 3,
        /// <summary>A build, a settings check or a Modifier failed; RequestRebuild retries.</summary>
        Faulted = 4,
        /// <summary>Release or destruction ended the session and cleared its input.</summary>
        Released = 5,
    }
}
