using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.PhysicsRecording.Editor
{
    /// <summary>Removes what <see cref="PhysicsPlaybackPolicy"/> does not allow from a copy being saved as playback.</summary>
    internal static class PhysicsPlaybackStripper
    {
        /// <summary>
        /// Removes every stripped component, dependents first, so no RequireComponent blocks a removal.
        /// The caller must own <paramref name="root"/>; it is an inactive copy, never the source hierarchy.
        /// </summary>
        public static void Strip(GameObject root)
        {
            foreach (Transform tf in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(tf.gameObject);
            var pending = new List<Component>();
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
                if (PhysicsPlaybackPolicy.IsStripped(component)) pending.Add(component);
            while (pending.Count > 0)
            {
                bool removed = false;
                // Later components usually depend on earlier ones, so walk backwards.
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (IsRequiredByAnother(pending[i])) continue;
                    Object.DestroyImmediate(pending[i]);
                    pending.RemoveAt(i);
                    removed = true;
                }
                if (!removed)
                    throw new InvalidOperationException("Cannot strip components whose RequireComponent dependencies form a cycle or are kept.");
            }
            foreach (ParticleSystem particles in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var collision = particles.collision;
                collision.enabled = false;
                var trigger = particles.trigger;
                trigger.enabled = false;
            }
        }

        private static bool IsRequiredByAnother(Component component)
        {
            Type type = component.GetType();
            foreach (Component other in component.GetComponents<Component>())
            {
                if (other == null || other == component) continue;
                foreach (RequireComponent required in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                    if (required.m_Type0?.IsAssignableFrom(type) == true || required.m_Type1?.IsAssignableFrom(type) == true
                        || required.m_Type2?.IsAssignableFrom(type) == true)
                        return true;
            }
            return false;
        }
    }
}
