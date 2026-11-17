using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>Shows, hides and frames the lanes, which are children of the controller found by name.</summary>
    internal sealed class TransformPathLanePresentation
    {
        private readonly Transform _root;
        private readonly List<LineRenderer> _routeLines = new List<LineRenderer>();

        public TransformPathLanePresentation(Transform root)
        {
            _root = root;
        }

        public void SetVisible(ETransformPathShowcaseLane lane, bool visible)
        {
            Transform laneRoot = _root.Find(GetLaneName(lane));
            if (laneRoot == null)
                return;
            Renderer[] renderers = laneRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = visible;
            }
        }

        public Bounds GetBounds(ETransformPathShowcaseLane lane)
        {
            Transform laneRoot = _root.Find(GetLaneName(lane));
            if (laneRoot == null)
                return new Bounds(_root.position, Vector3.one * 10f);
            Renderer[] renderers = laneRoot.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = new Bounds(laneRoot.position, Vector3.one * 10f);
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null)
                    continue;
                if (!hasBounds)
                {
                    bounds = renderers[i].bounds;
                    hasBounds = true;
                }
                else
                    bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }

        /// <summary>Draws each segment of the sequence lane in alternating colors.</summary>
        public void CreateRouteLines(MultiPathData sequence)
        {
            if (!sequence.IsReady)
                return;
            Transform parent = _root.Find(GetLaneName(ETransformPathShowcaseLane.MultiPath));
            if (parent == null)
                parent = _root;
            for (int i = 0; i < sequence.SegmentCount; i++)
            {
                PathData path = sequence.GetSegmentPath(i);
                if (path == null || !path.IsReady)
                    continue;
                GameObject lineObject = new GameObject($"Multi Path Segment {i + 1} Line");
                lineObject.transform.SetParent(parent, false);
                LineRenderer line = lineObject.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.widthMultiplier = 0.08f;
                Color color = i % 2 == 0 ? new Color(0.8f, 0.35f, 1f) : new Color(1f, 0.35f, 0.55f);
                line.startColor = color;
                line.endColor = color;
                line.positionCount = path.SamplePointCount;
                for (int point = 0; point < line.positionCount; point++)
                    line.SetPosition(point, path.GetSamplePoint(point));
                _routeLines.Add(line);
            }
        }

        public void DestroyRouteLines()
        {
            for (int i = 0; i < _routeLines.Count; i++)
            {
                if (_routeLines[i] != null)
                    Object.Destroy(_routeLines[i].gameObject);
            }
            _routeLines.Clear();
        }

        private static string GetLaneName(ETransformPathShowcaseLane lane)
        {
            switch (lane)
            {
                case ETransformPathShowcaseLane.MultiPath:
                    return "Multi Path Lane";
                case ETransformPathShowcaseLane.Queue:
                    return "Queued Path Lane";
                default:
                    return "Normal Path Lane";
            }
        }
    }
}
