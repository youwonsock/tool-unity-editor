using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>Shows, hides and frames the lanes, which are children of the controller found by name.</summary>
    internal sealed class TransformPathLanePresentation
    {
        private const int SHAPE_LINE_POINTS = 129;
        private const float LINE_WIDTH = 0.08f;
        private static readonly int BASE_COLOR_ID = Shader.PropertyToID("_BaseColor");
        private static readonly Color SEGMENT_LINE_COLOR_A = new Color(0.8f, 0.35f, 1f);
        private static readonly Color SEGMENT_LINE_COLOR_B = new Color(1f, 0.35f, 0.55f);
        private static readonly Color SHAPE_LINE_COLOR = new Color(0.3f, 0.95f, 0.55f);

        private readonly Transform _root;
        private readonly Material _lineMaterial;
        private readonly List<LineRenderer> _routeLines = new List<LineRenderer>();
        private MaterialPropertyBlock _lineColor;

        /// <param name="lineMaterial">An unlit material whose _BaseColor each line overrides.</param>
        public TransformPathLanePresentation(Transform root, Material lineMaterial)
        {
            _root = root;
            _lineMaterial = lineMaterial;
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
                LineRenderer line = CreateLine(parent, $"Multi Path Segment {i + 1} Line",
                    i % 2 == 0 ? SEGMENT_LINE_COLOR_A : SEGMENT_LINE_COLOR_B, path.SamplePointCount);
                for (int point = 0; point < line.positionCount; point++)
                    line.SetPosition(point, path.GetSamplePoint(point));
            }
        }

        /// <summary>Draws the shape lane's shape from its public samples.</summary>
        public void CreateShapeLine(PathShapeProvider shape)
        {
            if (!shape.IsReady)
                return;
            Transform parent = _root.Find(GetLaneName(ETransformPathShowcaseLane.Shape));
            if (parent == null)
                parent = _root;
            LineRenderer line = CreateLine(parent, "Shape Line", SHAPE_LINE_COLOR, SHAPE_LINE_POINTS);
            for (int point = 0; point < SHAPE_LINE_POINTS; point++)
                line.SetPosition(point, shape.Sample(point / (SHAPE_LINE_POINTS - 1f)));
        }

        // The shared material is unlit and ignores vertex colors, so each line
        // sets its own _BaseColor through a property block.
        private LineRenderer CreateLine(Transform parent, string name, Color color, int pointCount)
        {
            GameObject lineObject = new GameObject(name);
            lineObject.transform.SetParent(parent, false);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.widthMultiplier = LINE_WIDTH;
            line.sharedMaterial = _lineMaterial;
            _lineColor ??= new MaterialPropertyBlock();
            _lineColor.SetColor(BASE_COLOR_ID, color);
            line.SetPropertyBlock(_lineColor);
            line.positionCount = pointCount;
            _routeLines.Add(line);
            return line;
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
                case ETransformPathShowcaseLane.Shape:
                    return "Shape Path Lane";
                default:
                    return "Normal Path Lane";
            }
        }
    }
}
