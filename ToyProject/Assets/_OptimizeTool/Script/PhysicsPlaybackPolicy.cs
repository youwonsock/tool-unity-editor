using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool
{
    /// <summary>The simulation components removed on export and rejected on playback.</summary>
    internal static class PhysicsPlaybackPolicy
    {
        // Dependents precede the bodies they require so the writer can remove them safely.
        internal static readonly IReadOnlyList<Type> SimulationTypes = Array.AsReadOnly(new[]
        {
            typeof(Joint), typeof(Joint2D), typeof(ConstantForce), typeof(ConstantForce2D),
            typeof(Cloth), typeof(Animator), typeof(Collider), typeof(Collider2D),
            typeof(Rigidbody), typeof(Rigidbody2D), typeof(ArticulationBody)
        });

        internal static void Validate(GameObject root)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is MonoBehaviour)
                    throw new InvalidOperationException("The saved recording must not contain gameplay or missing scripts.");
                foreach (Type type in SimulationTypes)
                    if (type.IsInstanceOfType(component))
                        throw new InvalidOperationException("The saved recording must have no physics or Animator components.");
                if (component is Animation && component.gameObject != root)
                    throw new InvalidOperationException("The saved recording must have only one root Animation.");
                if (component is ParticleSystem particles && (particles.collision.enabled || particles.trigger.enabled))
                    throw new InvalidOperationException("The saved recording must have no particle physics modules.");
            }
        }
    }
}
