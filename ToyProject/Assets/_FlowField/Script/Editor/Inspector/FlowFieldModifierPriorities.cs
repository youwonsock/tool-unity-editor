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
        // Edit Mode results per Manager, dropped on any scene change
        // (FlowFieldEditorLifecycle). In Play Mode a script can change a
        // Priority without an editor event, so nothing is kept there.
        private static readonly Dictionary<FlowFieldManager, List<KeyValuePair<int, List<FlowFieldVectorModifierVolume>>>> Cache
            = new Dictionary<FlowFieldManager, List<KeyValuePair<int, List<FlowFieldVectorModifierVolume>>>>();

        internal static void ClearCache() => Cache.Clear();

        /// <summary>
        /// Each Priority that more than one Modifier Volume of this Manager
        /// uses, inactive ones included, with those volumes. Only the
        /// Manager's own scene is searched. The caller must not change the list.
        /// </summary>
        internal static List<KeyValuePair<int, List<FlowFieldVectorModifierVolume>>> FindDuplicatePriorities(FlowFieldManager manager)
        {
            if (manager == null || Application.isPlaying)
            {
                Cache.Clear();
                return Find(manager);
            }
            if (!Cache.TryGetValue(manager, out List<KeyValuePair<int, List<FlowFieldVectorModifierVolume>>> duplicates))
                Cache.Add(manager, duplicates = Find(manager));
            return duplicates;
        }

        private static List<KeyValuePair<int, List<FlowFieldVectorModifierVolume>>> Find(FlowFieldManager manager)
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
