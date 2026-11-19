using System;
using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>
    /// Defines what a physics-free playback object may contain. The editor strips a recording copy by
    /// <see cref="IsStripped"/>, and playback rejects anything <see cref="Validate"/> does not accept.
    /// </summary>
    public static class PhysicsPlaybackPolicy
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

        /// <summary>
        /// Accepts only a stripped hierarchy whose root, and only the root, has an Animation playing a finite
        /// Legacy clip, with no particle physics.
        /// </summary>
        public static void Validate(GameObject root)
        {
            Animation animation = root.GetComponent<Animation>();
            if (animation == null || animation.clip == null || !animation.clip.legacy
                || !PhysicsPoseMath.IsFinite(animation.clip.length))
                throw new InvalidOperationException("The saved recording must play a Legacy clip from an Animation on its root.");
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
    }
}
