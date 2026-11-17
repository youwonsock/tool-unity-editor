using System;
using UnityEngine;

namespace Common.TransformPath
{
    internal static class PathProviderUtility
    {
        /// <summary>
        /// A route that follows the path listens here too. One failing listener
        /// must not keep it from rebuilding, or fail the Rebuild that raised this.
        /// </summary>
        public static void RaisePathChanged(Action listeners)
        {
            if (listeners == null)
                return;
            Delegate[] invocations = listeners.GetInvocationList();
            for (int i = 0; i < invocations.Length; i++)
            {
                try
                {
                    ((Action)invocations[i])();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        public static void ThrowIfNotReady(IPathProvider provider, string parameterName)
        {
            if (provider == null)
                throw new ArgumentNullException(parameterName);
            if (!provider.IsReady)
                throw new InvalidOperationException("Path provider must be built and ready.");
        }
    }
}
