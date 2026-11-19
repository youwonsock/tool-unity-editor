using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.TransformPath.Editor
{
    /// <summary>Editor-only cache that reuses the runtime geometry builder.</summary>
    internal static class PathDataScenePreviewCache
    {
        #region Member Variables

        private static readonly Dictionary<int, PathDataScenePreview> CACHES =
            new Dictionary<int, PathDataScenePreview>();
        private static readonly List<int> REMOVED_IDS = new List<int>();

        static PathDataScenePreviewCache()
        {
            PathEditorChangeMonitor.StructureChanged += RemoveDestroyedPaths;
            PathEditorChangeMonitor.Updated += RemoveDestroyedPaths;
            PathEditorChangeMonitor.BeforeReload += BeforeReload;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }

        private static void RemoveDestroyedPaths()
        {
            REMOVED_IDS.Clear();
            foreach (int id in CACHES.Keys)
                if (!(EditorUtility.InstanceIDToObject(id) is PathData))
                    REMOVED_IDS.Add(id);
            for (int i = 0; i < REMOVED_IDS.Count; i++)
                CACHES.Remove(REMOVED_IDS[i]);
            REMOVED_IDS.Clear();
        }

        private static void PlayModeChanged(PlayModeStateChange state) => CACHES.Clear();

        private static void BeforeReload()
        {
            CACHES.Clear();
            REMOVED_IDS.Clear();
            EditorApplication.playModeStateChanged -= PlayModeChanged;
        }

        #endregion


        #region Public Methods

        public static bool TryGet(PathData pathData, out PathDataScenePreview preview)
        {
            preview = null;
            if (pathData == null)
                return false;

            int id = pathData.GetInstanceID();
            if (!CACHES.TryGetValue(id, out preview))
            {
                preview = new PathDataScenePreview();
                CACHES.Add(id, preview);
            }

            return preview.Update(pathData);
        }

        #endregion
    }

    /// <summary>
    /// The geometry a PathData would build from its current control points,
    /// rebuilt only when a point, the curve or the segment count changed.
    /// </summary>
    internal sealed class PathDataScenePreview
    {
        private readonly List<Vector3> _controlPoints = new List<Vector3>();
        private readonly List<Vector3> _lastControlPoints = new List<Vector3>();
        private readonly PathGeometryBuildBuffer _buildBuffer = new PathGeometryBuildBuffer();
        private PathPolyline _polyline;
        private EPathCurveType _curveType;
        private int _segmentCount;

        public bool IsValid => _polyline != null;
        public Vector3[] SampledPoints => _polyline?.Points;

        public bool Update(PathData pathData)
        {
            if (pathData == null
                || !pathData.CopyControlPoints(_controlPoints)
                || pathData.SegmentCount < PathConstants.MIN_PATH_POINTS)
            {
                Invalidate();
                return false;
            }

            EPathCurveType curveType = pathData.CurveType;
            int segmentCount = pathData.SegmentCount;
            if (_polyline != null
                && _curveType == curveType
                && _segmentCount == segmentCount
                && PathGeometryUtility.AreSamePoints(_controlPoints, _lastControlPoints))
                return true;

            if (!PathGeometryUtility.TryBuild(_controlPoints, curveType, segmentCount, _buildBuffer,
                    out _polyline, out _))
            {
                Invalidate();
                return false;
            }
            _curveType = curveType;
            _segmentCount = segmentCount;
            _lastControlPoints.Clear();
            _lastControlPoints.AddRange(_controlPoints);
            return true;
        }

        public Vector3 Sample(float normalizedTime) => _polyline == null ? Vector3.zero : _polyline.Sample(normalizedTime);

        private void Invalidate()
        {
            _polyline = null;
            _lastControlPoints.Clear();
        }
    }
}
