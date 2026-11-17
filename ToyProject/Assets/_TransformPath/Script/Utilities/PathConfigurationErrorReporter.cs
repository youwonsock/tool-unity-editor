using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Logs a configuration error once until the next successful build, so a
    /// broken path that rebuilds every frame does not flood the Console.
    /// </summary>
    internal struct PathConfigurationErrorReporter
    {
        private bool _reported;

        public void Report(Object context, string message)
        {
            if (_reported)
                return;
            Debug.LogError(message, context);
            _reported = true;
        }

        public void Reset() => _reported = false;
    }
}
