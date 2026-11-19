using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    /// <summary>A straight provider along +X, without Unity objects.</summary>
    internal sealed class LineProvider : IPathProvider
    {
        public LineProvider(float length) => PathLength = length;

        public bool IsReady { get; set; } = true;
        public int Revision { get; set; } = 1;
        public int DefinitionRevision { get; set; } = 1;
        public float PathLength { get; set; }
        public event Action PathChanged;

        public Vector3 Sample(float progress) => Vector3.right * (Mathf.Clamp01(progress) * PathLength);

        /// <summary>Itself as the one segment of its current revision.</summary>
        public PathRoute GetRoute() => PathRoute.FromProvider(this,
            new[] { new PathRoute.Segment(this, 0f, PathLength, PathMovementSettings.Speed(1f), false, null) }, false);

        public PathRuntimeEvent[] WholeRouteEvents => Array.Empty<PathRuntimeEvent>();

        public void Raise() => PathChanged?.Invoke();
    }

    /// <summary>Creates path objects in the Editor scene and destroys them after the test.</summary>
    internal sealed class PathTestScene : IDisposable
    {
        private readonly List<Object> _objects = new List<Object>();

        public PathData CreatePath(PathMovementSettings movement, EPathCurveType curve, params Vector3[] points)
        {
            var host = new GameObject("Path");
            _objects.Add(host);
            var transforms = new Transform[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                var point = new GameObject($"Point {i}").transform;
                point.SetParent(host.transform, false);
                point.position = points[i];
                transforms[i] = point;
            }
            PathData path = host.AddComponent<PathData>();
            path.SegmentCount = 200;
            path.CurveType = curve;
            path.MovementSettings = movement;
            path.SetPathPoints(transforms);
            return path;
        }

        public PathEventSettingSO CreateEvent(string name)
        {
            var setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
            setting.EventName = name;
            _objects.Add(setting);
            return setting;
        }

        public void SetEvents(PathData path, params (float at, PathEventSettingSO setting)[] events)
        {
            var entries = new List<PathEventEntry>();
            foreach (var (at, setting) in events)
                entries.Add(new PathEventEntry { NormalizedTime = at, EventSetting = setting });
            path.SetPathEvents(entries);
        }

        public void Dispose()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null)
                    Object.DestroyImmediate(_objects[i]);
        }
    }

    /// <summary>A playback engine that records what it notified.</summary>
    internal sealed class RecordingPlayback
    {
        public readonly PathPlaybackListeners Listeners = new PathPlaybackListeners();
        public readonly PathPlayback Playback;
        public readonly List<string> Log = new List<string>();
        private int _frame;

        public RecordingPlayback()
        {
            Listeners.StateChanged.Add(new Action<EPathFollowerState>(state => Log.Add($"state {state}")));
            Listeners.SegmentChanged.Add(new Action<int>(segment => Log.Add($"segment {segment}")));
            Listeners.Completed.Add(new Action(() => Log.Add("completed")));
            Listeners.EventReached.Add(new Action<string>(name => Log.Add($"event {name}")));
            Playback = new PathPlayback(Listeners);
            Playback.Initialize();
        }

        /// <summary>One driver frame: effects, then movement.</summary>
        public void Frame(float deltaTime = 1f / 60f)
        {
            _frame++;
            Playback.TickEffects(deltaTime, _frame);
            Playback.TickMovement(deltaTime);
        }

        public void Frames(int count, float deltaTime = 1f / 60f)
        {
            for (int i = 0; i < count; i++)
                Frame(deltaTime);
        }
    }
}
