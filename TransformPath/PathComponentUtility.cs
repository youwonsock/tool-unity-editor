using UnityEngine;

namespace Supercent.Common.TransformPath
{
    internal static class PathComponentUtility
    {
        public static T EnsureComponent<T>(GameObject gameObject) where T : Component
        {
            if (!gameObject.TryGetComponent(out T component))
                component = gameObject.AddComponent<T>();

            return component;
        }
    }
}
