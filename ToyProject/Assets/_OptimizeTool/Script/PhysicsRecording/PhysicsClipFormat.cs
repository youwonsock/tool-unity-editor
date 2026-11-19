namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>What a take is saved as.</summary>
    public enum PhysicsClipFormat
    {
        /// <summary>A Legacy clip and a physics-free prefab whose root Animation plays it.</summary>
        LegacyPrefab,
        /// <summary>A Generic clip for an Animator Controller. No prefab is saved.</summary>
        Generic,
    }
}
