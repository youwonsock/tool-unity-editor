using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Shared lifecycle of the path components. A provider builds into a
    /// temporary result, publishes it whole, and raises <see cref="PathChanged"/>
    /// once. Deciding what changed happens here and nowhere else: consumers only
    /// compare <see cref="Revision"/> and the definition revision.
    /// Only <see cref="PathData"/> and <see cref="MultiPathData"/> derive from it.
    /// </summary>
    public abstract class PathProviderBehaviour : MonoBehaviour, IPathProvider
    {
        private protected PathProviderBehaviour() { }

        private int _revision;
        private int _definitionRevision;
        private bool _wasReady;
        private bool _buildRequested;
        private bool _destroyed;
        private PathRoute _route;
        private PathConfigurationErrorReporter _errors;

        public abstract bool IsReady { get; }
        public int Revision => _revision;
        /// <summary>Rises only when the movement, the events or the segment structure change.</summary>
        internal int DefinitionRevision => _definitionRevision;
        int IPathProvider.DefinitionRevision => _definitionRevision;
        public abstract float PathLength { get; }

        public event Action PathChanged;

        public abstract Vector3 Sample(float progress);

        /// <summary>Builds from the current authoring data and publishes the result if it changed.</summary>
        public void Rebuild()
        {
            if (_destroyed)
                return;
            _buildRequested = true;
            if (TryBuild(out bool geometryChanged, out bool definitionChanged, out string error))
            {
                _errors.Reset();
                bool recovered = !_wasReady;
                _wasReady = true;
                if (geometryChanged || definitionChanged)
                    Publish(definitionChanged);
                else if (recovered)
                    // Same content as before a failed build: a sequence that
                    // failed with this path must hear it is back.
                    PathProviderUtility.RaisePathChanged(PathChanged);
                return;
            }

            MarkFailed();
            _errors.Report(this, $"{GetType().Name} '{name}' could not build: {error}");
            NotifyUnavailable();
        }

        /// <summary>The playback route of this provider's current revision, shared by every follower.</summary>
        internal PathRoute GetRoute()
        {
            ThrowIfNotReady();
            if (_route == null || _route.Revision != _revision)
                _route = CreateRoute();
            return _route;
        }

        /// <summary>Builds once if nothing asked for a build yet, such as a sequence waking first.</summary>
        internal void EnsureBuilt()
        {
            if (!_buildRequested)
                Rebuild();
        }

        /// <summary>Builds into fields of the subclass only when every check passed.</summary>
        protected abstract bool TryBuild(out bool geometryChanged, out bool definitionChanged, out string error);

        /// <summary>Makes the last publication unavailable for playback until the next good build.</summary>
        protected abstract void MarkFailed();

        private protected abstract PathRoute CreateRoute();

        /// <summary>Rebuilds after a setting changed, unless the provider was never asked to build.</summary>
        protected void RebuildIfRequested()
        {
            if (_buildRequested)
                Rebuild();
        }

        protected void ThrowIfNotReady()
        {
            if (!IsReady)
                throw new InvalidOperationException($"{GetType().Name} '{name}' is not built and ready.");
        }

        /// <summary>A destroyed provider stops every playback and queue that uses it.</summary>
        protected virtual void OnDestroy()
        {
            _destroyed = true;
            MarkUnavailable();
        }

        /// <summary>
        /// Takes the publication out of use without logging, because a provider
        /// this one depends on already reported why.
        /// </summary>
        protected void MarkUnavailable()
        {
            MarkFailed();
            NotifyUnavailable();
        }

        // A follower or a sequence that uses this path must hear it went away.
        private void NotifyUnavailable()
        {
            if (!_wasReady)
                return;
            _wasReady = false;
            PathProviderUtility.RaisePathChanged(PathChanged);
        }

        private void Publish(bool definitionChanged)
        {
            _revision++;
            if (definitionChanged)
                _definitionRevision++;
            _route = null;
            PathProviderUtility.RaisePathChanged(PathChanged);
        }
    }
}
