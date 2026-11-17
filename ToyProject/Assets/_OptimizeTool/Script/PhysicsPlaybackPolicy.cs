using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using Object = UnityEngine.Object;
#endif

namespace Common.OptimizeTool
{
    /// <summary>
    /// Defines what a physics-free playback object may contain. The editor strips a recording copy with
    /// <see cref="Strip"/>, and playback rejects anything <see cref="Validate"/> does not accept.
    /// </summary>
    internal static class PhysicsPlaybackPolicy
    {
        private static readonly Type[] SimulationTypes =
        {
            typeof(Rigidbody), typeof(Rigidbody2D), typeof(ArticulationBody), typeof(Collider), typeof(Collider2D),
            typeof(Joint), typeof(Joint2D), typeof(ConstantForce), typeof(ConstantForce2D), typeof(Cloth), typeof(Animator)
        };

        /// <summary>Gameplay scripts, existing animation and every simulation component are removed on export.</summary>
        internal static bool IsStripped(Component component)
        {
            if (component is MonoBehaviour || component is Animation) return true;
            foreach (Type type in SimulationTypes)
                if (type.IsInstanceOfType(component)) return true;
            return false;
        }

        /// <summary>Accepts only a stripped hierarchy with a single root Animation and no particle physics.</summary>
        internal static void Validate(GameObject root)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    throw new InvalidOperationException("The saved recording must not contain missing scripts.");
                if (component is Animation)
                {
                    if (component.gameObject != root)
                        throw new InvalidOperationException("The saved recording must have only one root Animation.");
                }
                else if (IsStripped(component))
                    throw new InvalidOperationException("The saved recording must have no gameplay scripts, physics or Animator components.");
                if (component is ParticleSystem particles && (particles.collision.enabled || particles.trigger.enabled))
                    throw new InvalidOperationException("The saved recording must have no particle physics modules.");
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Removes every stripped component, dependents first, so no RequireComponent blocks a removal.
        /// The caller must own <paramref name="root"/>; it is an inactive copy, never the source hierarchy.
        /// </summary>
        internal static void Strip(GameObject root)
        {
            foreach (Transform tf in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(tf.gameObject);
            var pending = new List<Component>();
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
                if (IsStripped(component)) pending.Add(component);
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
#endif
    }
}
