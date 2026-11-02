using System;

namespace Common.TransformPath
{
    /// <summary>
    /// The listeners of one event, kept as an array that is replaced rather than
    /// changed. A notification can walk it while a listener unsubscribes, and
    /// neither walking nor having no listeners allocates.
    /// </summary>
    internal struct PathListeners<T> where T : Delegate
    {
        private T _combined;

        public Delegate[] Invocations { get; private set; }

        public void Add(T listener)
        {
            _combined = (T)Delegate.Combine(_combined, listener);
            Invocations = _combined?.GetInvocationList();
        }

        public void Remove(T listener)
        {
            _combined = (T)Delegate.Remove(_combined, listener);
            Invocations = _combined?.GetInvocationList();
        }
    }
}
