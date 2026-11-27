namespace Common.TransformPath
{
    /// <summary>Values shared by every part of TransformPath. Define them nowhere else.</summary>
    internal static class PathConstants
    {
        public const float EPSILON = 0.00001f;
        public const int MIN_PATH_POINTS = 2;
        public const int DEFAULT_SEGMENT_COUNT = 500;

        /// <summary>Lowest Speed or Duration a movement accepts.</summary>
        public const float MIN_MOVE_VALUE = 0.001f;
        /// <summary>Highest Speed or Duration a movement accepts.</summary>
        public const float MAX_MOVE_VALUE = 9999f;

        /// <summary>Highest authored event position. An event at 1 would race completion.</summary>
        public const float MAX_EVENT_POSITION = 0.995f;

        /// <summary>A Tick that crosses more segment ends carries the rest to the next frame.</summary>
        public const int MAX_SEGMENT_TRANSITIONS_PER_TICK = 65;

        public const float DEFAULT_QUEUE_SPACING = 1.5f;
        /// <summary>Distance beyond the spacing over which a queue member slows down.</summary>
        public const float DEFAULT_SLOWDOWN_RANGE = 1.5f;
        public const float DEFAULT_MIN_SPEED_MULTIPLIER = 0.1f;
        /// <summary>Lowest speed multiplier of a queue member still short of its spacing, so it reaches the gap.</summary>
        public const float MIN_APPROACH_SPEED_MULTIPLIER = 0.01f;
        public const float DEFAULT_RESUME_DELAY = 0.1f;
        public const float DEFAULT_UNBLOCK_HYSTERESIS = 0.3f;
        public const float DEFAULT_SPEED_SMOOTH_RATE = 5f;
    }
}
