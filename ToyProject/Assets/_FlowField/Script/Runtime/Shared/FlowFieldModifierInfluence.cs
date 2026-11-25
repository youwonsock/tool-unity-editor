using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Influence Collider rule every Modifier shares in both space modes:
    /// the registry, both builders and the Editor check it the same way.
    /// </summary>
    internal static class FlowFieldModifierInfluence
    {
        /// <summary>
        /// The one rule for an Influence Collider: present, a trigger, and
        /// convex when it is a MeshCollider. False with the first broken part.
        /// </summary>
        public static bool TryValidateInfluenceCollider(Collider collider, out string reason)
        {
            reason = string.Empty;
            if (collider == null)
                reason = "Influence Collider is required.";
            else if (!collider.isTrigger)
                reason = "Influence Collider must be a trigger.";
            else if (collider is MeshCollider meshCollider && !meshCollider.convex)
                reason = "A trigger MeshCollider must be convex.";
            return reason.Length == 0;
        }

        // A valid Influence Collider that is enabled in the scene.
        public static bool IsUsableTrigger(Collider influenceCollider)
            => TryValidateInfluenceCollider(influenceCollider, out _)
                && influenceCollider.enabled
                && influenceCollider.gameObject.activeInHierarchy;
    }
}
