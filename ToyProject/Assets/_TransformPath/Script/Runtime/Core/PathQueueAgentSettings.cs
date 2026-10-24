namespace Common.TransformPath
{
    public readonly struct PathQueueAgentSettings
    {
        public float ActorSpacing { get; }
        public bool UseManagerSpacing { get; }
        public bool EnableGradualSlowdown { get; }
        public bool EnableOvertakeProtection { get; }

        public PathQueueAgentSettings(
            float actorSpacing,
            bool useManagerSpacing,
            bool enableGradualSlowdown,
            bool enableOvertakeProtection)
        {
            ActorSpacing = actorSpacing;
            UseManagerSpacing = useManagerSpacing;
            EnableGradualSlowdown = enableGradualSlowdown;
            EnableOvertakeProtection = enableOvertakeProtection;
        }
    }

}
