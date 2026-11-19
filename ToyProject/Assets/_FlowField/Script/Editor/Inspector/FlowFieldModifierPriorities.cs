using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// The Modifier Volumes of a Manager that share a Priority. Registration
    /// rejects a duplicate at runtime, so the Inspector names them first.
    /// </summary>
    internal static class FlowFieldModifierPriorities
    {
        /// <summary>
        /// Each Priority that more than one Modifier Volume of this Manager
        /// uses, inactive ones included, with those volumes. Only the
        /// Manager's own scene is searched.
        /// </summary>
        internal static List<KeyValuePair<int, List<FlowFieldVectorModifierVolume>>> FindDuplicatePriorities(FlowFieldManager manager)
        {
            var duplicates = new List<KeyValuePair<int, List<FlowFieldVectorModifierVolume>>>();
            if (manager == null || !manager.gameObject.scene.IsValid())
                return duplicates;
            var byPriority = new SortedDictionary<int, List<FlowFieldVectorModifierVolume>>();
            foreach (GameObject root in manager.gameObject.scene.GetRootGameObjects())
            {
                foreach (FlowFieldVectorModifierVolume volume in root.GetComponentsInChildren<FlowFieldVectorModifierVolume>(true))
                {
                    if (volume.FlowFieldManager != manager)
                        continue;
                    if (!byPriority.TryGetValue(volume.Priority, out List<FlowFieldVectorModifierVolume> volumes))
                        byPriority.Add(volume.Priority, volumes = new List<FlowFieldVectorModifierVolume>(2));
                    volumes.Add(volume);
                }
            }
            foreach (KeyValuePair<int, List<FlowFieldVectorModifierVolume>> group in byPriority)
            {
                if (group.Value.Count > 1)
                    duplicates.Add(group);
            }
            return duplicates;
        }

        /// <summary>The runtime rejection message followed by the volumes that share the Priority.</summary>
        internal static string Describe(int priority, List<FlowFieldVectorModifierVolume> volumes)
        {
            var names = new string[volumes.Count];
            for (int i = 0; i < volumes.Count; i++)
                names[i] = $"{volumes[i].name} ({volumes[i].GetType().Name})";
            return $"{FlowFieldModifierRegistry.DuplicatePriorityMessage(priority)} {string.Join(", ", names)}";
        }
    }
}
