using System;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class PathData : IPathProvider, IPathController, IPathEventSource
    {
        #region Member Variables

        private int _revision = 0;

        #endregion


        #region Properties

        public bool IsReady => _isInitialized && _cachedPathPoints != null && _cachedPathPoints.Length > 0;
        public int Revision => _revision;

        public event Action PathChanged;

        #endregion


        #region Public Methods

        public bool TrySample(float normalizedTime, out Vector3 position)
        {
            position = Vector3.zero;

            if (!IsFinite(normalizedTime))
                return false;

            if (!IsReady)
                Init();

            if (!IsReady)
                return false;

            position = GetPointOnPath(Mathf.Clamp01(normalizedTime));
            return true;
        }

        public bool TrySampleDistance(float distance, out Vector3 position)
        {
            position = Vector3.zero;

            if (!IsFinite(distance))
                return false;

            if (!IsReady)
                Init();

            if (!IsReady || _cachedPathLength <= 0f)
                return false;

            position = GetPointAtDistance(Mathf.Clamp(distance, 0f, _cachedPathLength));
            return true;
        }

        public bool TryRebuild(bool forceRebuild = false)
        {
            Init(forceRebuild);
            return IsReady;
        }

        internal void NotifyPathBuild(bool resultChanged)
        {
            if (!resultChanged)
                return;

            _revision++;
            PathChanged?.Invoke();
        }

        #endregion


        #region Private Methods

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        #endregion
    }
}
