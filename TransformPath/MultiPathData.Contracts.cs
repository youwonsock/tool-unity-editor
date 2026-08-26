using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class MultiPathData : IPathSequenceProvider, IPathController
    {
        #region Member Variables

        private int _revision = 0;
        private bool _sequenceDirty = false;
        private int[] _pathRevisionSnapshot = Array.Empty<int>();
        private readonly List<PathData> _subscribedPathData = new List<PathData>();

        #endregion


        #region Properties

        public bool IsReady => _isInitialized && _pathLengths != null && _pathLengths.Length > 0;
        public int Revision => _revision;
        public int SegmentCount => IsReady ? _pathLengths.Length : 0;

        public event Action PathChanged;

        #endregion


        #region Public Methods

        private void OnEnable()
        {
            SubscribeToChildren();
        }

        private void OnDisable()
        {
            UnsubscribeFromChildren();
        }

        public bool TrySample(float normalizedTime, out Vector3 position)
        {
            position = Vector3.zero;

            if (!IsFinite(normalizedTime))
                return false;

            EnsureCurrent();
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

            EnsureCurrent();
            if (!IsReady || _totalPathLength <= 0f)
                return false;

            position = GetPointAtDistance(Mathf.Clamp(distance, 0f, _totalPathLength));
            return true;
        }

        public bool TryGetSegment(int index, out PathSegmentDescriptor descriptor)
        {
            descriptor = default;
            EnsureCurrent();

            if (!IsReady || index < 0 || index >= _pathDataConfigs.Count)
                return false;

            PathDataConfig config = _pathDataConfigs[index];
            if (config == null || config.PathData == null || !config.PathData.IsReady)
                return false;

            descriptor = new PathSegmentDescriptor(
                config.PathData,
                PathTypeConversion.ToPublic(config.MoveType),
                config.Value,
                config.TimeCurve);
            return true;
        }

        public bool TryRebuild(bool forceRebuild = false)
        {
            Init(forceRebuild);
            return IsReady;
        }

        internal void MarkSequenceDirty()
        {
            _sequenceDirty = true;
        }

        internal void NotifySequenceBuild(bool resultChanged)
        {
            if (!resultChanged)
                return;

            _revision++;
            PathChanged?.Invoke();
        }

        #endregion


        #region Private Methods

        private void SubscribeToChildren()
        {
            UnsubscribeFromChildren();

            if (_pathDataConfigs == null)
                return;

            for (int i = 0; i < _pathDataConfigs.Count; i++)
            {
                PathData pathData = _pathDataConfigs[i]?.PathData;
                if (pathData == null || _subscribedPathData.Contains(pathData))
                    continue;

                pathData.PathChanged += MarkSequenceDirty;
                _subscribedPathData.Add(pathData);
            }
        }

        private void UnsubscribeFromChildren()
        {
            for (int i = 0; i < _subscribedPathData.Count; i++)
            {
                PathData pathData = _subscribedPathData[i];
                if (pathData != null)
                    pathData.PathChanged -= MarkSequenceDirty;
            }

            _subscribedPathData.Clear();
        }

        private void CaptureChildRevisions()
        {
            int count = _pathDataConfigs?.Count ?? 0;
            if (_pathRevisionSnapshot.Length != count)
                _pathRevisionSnapshot = new int[count];

            for (int i = 0; i < count; i++)
            {
                PathDataConfig config = _pathDataConfigs[i];
                _pathRevisionSnapshot[i] = config?.PathData != null ? config.PathData.Revision : -1;
            }

            _sequenceDirty = false;
            SubscribeToChildren();
        }

        private void EnsureCurrent()
        {
            if (!_isInitialized || _sequenceDirty || HasChildRevisionChanged())
                Init(forceReinit: _isInitialized);
        }

        private bool HasChildRevisionChanged()
        {
            if (_pathDataConfigs == null || _pathRevisionSnapshot.Length != _pathDataConfigs.Count)
                return true;

            for (int i = 0; i < _pathDataConfigs.Count; i++)
            {
                PathDataConfig config = _pathDataConfigs[i];
                int revision = config?.PathData != null ? config.PathData.Revision : -1;
                if (_pathRevisionSnapshot[i] != revision)
                    return true;
            }

            return false;
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        #endregion
    }
}
