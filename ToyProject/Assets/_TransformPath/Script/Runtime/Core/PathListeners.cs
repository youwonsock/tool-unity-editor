using System;

namespace Common.TransformPath
{
    /// <summary>
    /// The listeners of one event, kept as an array that is replaced rather than
    /// changed. A notification can walk it while a listener unsubscribes, and
    /// neither walking nor having no listeners allocates.
    /// </summary>
    internal sealed class PathListeners
    {
        private Delegate _combined;

        public Delegate[] Invocations { get; private set; }

        public void Add(Delegate listener)
        {
            _combined = Delegate.Combine(_combined, listener);
            Invocations = _combined?.GetInvocationList();
        }

        public void Remove(Delegate listener)
        {
            _combined = Delegate.Remove(_combined, listener);
            Invocations = _combined?.GetInvocationList();
        }

        /// <summary>Whether a listener of an earlier array is still subscribed.</summary>
        public bool StillHas(Delegate[] earlier, Delegate listener)
        {
            return ReferenceEquals(earlier, Invocations)
                || (Invocations != null && Array.IndexOf(Invocations, listener) >= 0);
        }
    }
}
