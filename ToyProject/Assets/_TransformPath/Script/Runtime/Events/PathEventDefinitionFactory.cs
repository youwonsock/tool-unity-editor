using System.Collections.Generic;

namespace Common.TransformPath
{
    /// <summary>Converts authoring event assets into immutable runtime data.</summary>
    internal static class PathEventDefinitionFactory
    {
        public static PathEventDefinition Create(PathEventSettingSO setting)
        {
            return PathEventDefinition.FromAuthoring(setting);
        }

        public static PathRuntimeEvent Create(PathEventEntry entry)
        {
            return new PathRuntimeEvent(
                entry.NormalizedTime,
                Create(entry.EventSetting));
        }

        public static PathRuntimeEvent[] Create(IReadOnlyList<PathEventEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return System.Array.Empty<PathRuntimeEvent>();

            PathRuntimeEvent[] result = new PathRuntimeEvent[entries.Count];
            for (int i = 0; i < entries.Count; i++)
                result[i] = Create(entries[i]);
            return result;
        }
    }
}
