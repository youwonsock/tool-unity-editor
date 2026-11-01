using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Common.TransformPath.PlayerProbe
{
    /// <summary>
    /// Measures real Player frames through the public TransformPath API: many
    /// followers, long queues, Seek, path changes, Pause/Resume and Loop. It also
    /// records whether a queue keeps its spacing and finishes. Only the build made
    /// by Tools/run-transformpath-probe.sh contains it.
    /// </summary>
    public sealed class TransformPathPlayerProbe : MonoBehaviour
    {
        private const string OutputArgument = "-transformPathProbeOut";
        private const string SampleScene = "TransformPathSample";
        private const int TargetFrameRate = 60;
        private const int SteadyFrames = 120;
        private const float SampleSeconds = 8f;
        private const float DrainTimeoutSeconds = 90f;
        private const float StallSeconds = 300f;
        private const float Spacing = 1.5f;
        private const float SpacingTolerance = 0.01f;
        private static readonly int[] Sizes = { 100, 500, 1000 };
        private static readonly int[] QueueSizes = { 100, 500, 1000 };

        private readonly StringBuilder _rows = new StringBuilder("run,cpu,gpu,unity,scenario,metric,value\n");
        private readonly List<double> _updates = new List<double>(SteadyFrames);
        private readonly List<double> _frames = new List<double>(SteadyFrames);
        private readonly List<Vector3> _points = new List<Vector3>(2);
        private string _run;
        private string _outputPath;
        private int _errors;
        private float _lastProgress;
        private bool _finished;
        private bool _allocationCounterWorks;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            gameObject.AddComponent<TransformPathProbeFrameStart>();
            gameObject.AddComponent<TransformPathProbeFrameEnd>();
            Application.runInBackground = true;
            Application.targetFrameRate = TargetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.logMessageReceived += OnLog;
            _run = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            _outputPath = ReadOutputPath();
            _lastProgress = Time.realtimeSinceStartup;
            long before = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new byte[1024]);
            _allocationCounterWorks = GC.GetAllocatedBytesForCurrentThread() > before;
        }

        private void Update()
        {
            if (_finished || Time.realtimeSinceStartup - _lastProgress < StallSeconds)
                return;
            ReportError($"No progress for {StallSeconds} seconds.");
            Finish();
        }

        private void OnDestroy() => Application.logMessageReceived -= OnLog;

        private IEnumerator Start()
        {
            Record("environment", "allocationCounterWorks", _allocationCounterWorks ? 1 : 0);
            Record("environment", "monoUsedWorks", TransformPathProbeFrameClock.MonoUsedWorks ? 1 : 0);
            yield return Frames("empty", 60);
            foreach (int count in Sizes)
            {
                yield return MeasureFree($"free.single.speed.{count}", count, false, false);
                yield return MeasureFree($"free.sequence.time.{count}", count, true, false);
                yield return MeasureFree($"loop.single.speed.{count}", count, false, true);
                yield return MeasureFree($"loop.sequence.time.{count}", count, true, true);
            }
            foreach (int count in QueueSizes)
                yield return MeasureQueue($"queue.speed.{count}", count, false);
            yield return MeasureQueue("queue.time.1000", 1000, true);
            yield return MeasureQueueDrain(100);
            yield return MeasureQueueDrain(300);
            // Room to spare, a line that fills the loop, and arrivals faster
            // than the restart delay can release them.
            yield return MeasureQueueLoop("queueLoop.spare", 20, 80f, 5f, 1200);
            yield return MeasureQueueLoop("queueLoop.full", 38, 80f, 5f, 1200);
            yield return MeasureQueueLoop("queueLoop.fast", 20, 80f, 40f, 600);
            yield return MeasureSampleScene();
            Finish();
        }

        private IEnumerator MeasureFree(string scenario, int count, bool sequence, bool loop)
        {
            var root = new GameObject(scenario);
            float segmentLength = loop ? 1f : 200f;
            var paths = new List<PathData>();
            int segments = sequence ? 8 : 1;
            for (int i = 0; i < segments; i++)
            {
                Vector3 start = Vector3.right * (segmentLength * i);
                paths.Add(CreatePath(root, start, start + Vector3.right * segmentLength,
                    sequence
                        ? new PathMovementSettings(EPathMoveType.TimeBased, loop ? 0.25f : 300f, AnimationCurve.EaseInOut(0f, 0f, 1f, 1f))
                        : new PathMovementSettings(EPathMoveType.SpeedBased, loop ? 4f : 5f, null)));
            }
            MultiPathData multi = sequence ? CreateSequence(root, paths) : null;
            PathPlaybackRequest request = sequence
                ? PathPlaybackRequest.Sequence(multi, loop)
                : PathPlaybackRequest.Single(paths[0], loop);

            var followers = new PathFollower[count];
            Burst(scenario, "create", () =>
            {
                for (int i = 0; i < count; i++)
                {
                    var agent = new GameObject("Follower");
                    agent.transform.SetParent(root.transform, false);
                    followers[i] = agent.AddComponent<PathFollower>();
                }
            });
            yield return Frames(scenario + ".idle", 30);

            Burst(scenario, "start", () =>
            {
                for (int i = 0; i < count; i++)
                    followers[i].StartPlayback(request);
            });
            yield return Frames(scenario + ".steady", SteadyFrames);
            RecordMoving(scenario + ".steady", followers);

            var random = new System.Random(1234);
            Burst(scenario, "seek", () =>
            {
                for (int i = 0; i < count; i++)
                    followers[i].Seek((float)random.NextDouble());
            });
            yield return Frames(scenario + ".afterSeek", 5);

            Burst(scenario, "pause", () =>
            {
                for (int i = 0; i < count; i++)
                    followers[i].PauseMove();
            });
            yield return Frames(scenario + ".paused", 30);
            Burst(scenario, "resume", () =>
            {
                for (int i = 0; i < count; i++)
                    followers[i].ResumeMove();
            });
            yield return Frames(scenario + ".afterResume", 5);

            Burst(scenario, "pathChange", () => MovePathEnd(paths[0], Vector3.right * segmentLength + Vector3.up));
            yield return Frames(scenario + ".afterPathChange", 5);
            RecordMoving(scenario + ".afterPathChange", followers);

            Burst(scenario, "stop", () =>
            {
                for (int i = 0; i < count; i++)
                    followers[i].StopMove();
            });
            Burst(scenario, "destroy", () => Object.DestroyImmediate(root));
            yield return null;
            Progress();
        }

        private IEnumerator MeasureQueue(string scenario, int count, bool timeBased)
        {
            var root = new GameObject(scenario);
            float length = count * 2f + 200f;
            PathData path = CreatePath(root, Vector3.zero, Vector3.right * length, timeBased
                ? new PathMovementSettings(EPathMoveType.TimeBased, length / 5f, AnimationCurve.Linear(0f, 0f, 1f, 1f))
                : new PathMovementSettings(EPathMoveType.SpeedBased, 5f, null));
            QueuedPathManager manager = CreateManager(root, path);
            PathPlaybackRequest request = PathPlaybackRequest.Single(path);

            var agents = new QueuedPathFollower[count];
            Burst(scenario, "create", () =>
            {
                for (int i = 0; i < count; i++)
                    agents[i] = CreateAgent(root);
            });
            yield return Frames(scenario + ".idle", 30);
            Burst(scenario, "start", () =>
            {
                for (int i = 0; i < count; i++)
                    agents[i].StartQueuedPlayback(manager, request);
            });
            Burst(scenario, "seek", () =>
            {
                for (int i = 0; i < count; i++)
                    agents[i].Seek((count - i) * 2f / length);
            });
            yield return Frames(scenario + ".settle", 60);
            yield return Frames(scenario + ".steady", SteadyFrames);
            RecordQueue(scenario + ".steady", agents, length);

            Burst(scenario, "pause", () =>
            {
                for (int i = 0; i < count; i++)
                    agents[i].PauseMove();
            });
            yield return Frames(scenario + ".paused", 30);
            Burst(scenario, "resume", () =>
            {
                for (int i = 0; i < count; i++)
                    agents[i].ResumeMove();
            });
            yield return Frames(scenario + ".afterResume", 60);
            RecordQueue(scenario + ".afterResume", agents, length);

            Burst(scenario, "pathChange", () => MovePathEnd(path, Vector3.right * length + Vector3.up));
            yield return Frames(scenario + ".afterPathChange", 60);
            RecordQueue(scenario + ".afterPathChange", agents, length);

            Burst(scenario, "stop", () =>
            {
                for (int i = 0; i < count; i++)
                    agents[i].StopMove();
            });
            Burst(scenario, "destroy", () => Object.DestroyImmediate(root));
            yield return null;
            Progress();
        }

        // Everyone starts at one point, forms a line and leaves at the end, so
        // this also shows whether a queue finishes.
        private IEnumerator MeasureQueueDrain(int count)
        {
            string scenario = $"queueDrain.{count}";
            var root = new GameObject(scenario);
            const float length = 60f;
            const float spacing = 0.5f;
            PathData path = CreatePath(root, Vector3.zero, Vector3.right * length,
                new PathMovementSettings(EPathMoveType.SpeedBased, 30f, null));
            QueuedPathManager manager = CreateManager(root, path);
            manager.DefaultSpacing = spacing;
            manager.SlowdownStartDistance = spacing * 2f;
            var agents = new QueuedPathFollower[count];
            int completed = 0;
            for (int i = 0; i < count; i++)
            {
                agents[i] = CreateAgent(root);
                agents[i].Completed += () => completed++;
            }
            PathPlaybackRequest request = PathPlaybackRequest.Single(path);
            Burst(scenario, "start", () =>
            {
                for (int i = 0; i < count; i++)
                    agents[i].StartQueuedPlayback(manager, request);
            });

            _updates.Clear();
            _frames.Clear();
            int gcBefore = GC.CollectionCount(0);
            int frames = 0;
            int overlapFrames = 0;
            int firstFinished = -1;
            float started = Time.realtimeSinceStartup;
            while (completed < count && Time.realtimeSinceStartup - started < DrainTimeoutSeconds)
            {
                yield return null;
                frames++;
                _updates.Add(TransformPathProbeFrameClock.LastUpdateMilliseconds);
                _frames.Add(Time.unscaledDeltaTime * 1000d);
                if (MinimumGap(agents, length, false) < spacing - SpacingTolerance)
                    overlapFrames++;
                if (firstFinished < 0 && completed > 0)
                    firstFinished = frames;
            }
            float seconds = Time.realtimeSinceStartup - started;
            RecordFrameStats(scenario, GC.CollectionCount(0) - gcBefore);
            Record(scenario, "seconds", seconds);
            Record(scenario, "frames", frames);
            Record(scenario, "completed", completed);
            Record(scenario, "unfinishedAtLimit", count - completed);
            Record(scenario, "completedPerSecond", completed / seconds);
            Record(scenario, "overlapFrames", overlapFrames);
            Object.DestroyImmediate(root);
            yield return null;
            Progress();
        }

        // A loop's end meets its start, and the gap across that seam counts too.
        private IEnumerator MeasureQueueLoop(string scenario, int count, float length, float speed, int frames)
        {
            var root = new GameObject(scenario);
            PathData path = CreatePath(root, Vector3.zero, Vector3.right * length,
                new PathMovementSettings(EPathMoveType.SpeedBased, speed, null));
            QueuedPathManager manager = CreateManager(root, path);
            var agents = new QueuedPathFollower[count];
            for (int i = 0; i < count; i++)
                agents[i] = CreateAgent(root);
            PathPlaybackRequest request = PathPlaybackRequest.Single(path, true);
            for (int i = 0; i < count; i++)
                agents[i].StartQueuedPlayback(manager, request);
            for (int i = 0; i < count; i++)
                agents[i].Seek((count - i) * 2f / length);

            _updates.Clear();
            _frames.Clear();
            int gcBefore = GC.CollectionCount(0);
            int seamOverlapFrames = 0;
            int lineOverlapFrames = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                yield return null;
                _updates.Add(TransformPathProbeFrameClock.LastUpdateMilliseconds);
                _frames.Add(Time.unscaledDeltaTime * 1000d);
                if (MinimumGap(agents, length, false) < Spacing - SpacingTolerance)
                    lineOverlapFrames++;
                if (MinimumGap(agents, length, true) < Spacing - SpacingTolerance)
                    seamOverlapFrames++;
            }
            RecordFrameStats(scenario, GC.CollectionCount(0) - gcBefore);
            Record(scenario, "lineOverlapFrames", lineOverlapFrames);
            Record(scenario, "seamOverlapFrames", seamOverlapFrames);
            int moving = 0;
            for (int i = 0; i < count; i++)
                if (agents[i].IsMoving)
                    moving++;
            Record(scenario, "moving", moving);
            Object.DestroyImmediate(root);
            yield return null;
            Progress();
        }

        private IEnumerator MeasureSampleScene()
        {
            int errorsBefore = _errors;
            yield return SceneManager.LoadSceneAsync(SampleScene);
            yield return new WaitForSecondsRealtime(SampleSeconds);
            Record(SampleScene, "ran", 1);
            Record(SampleScene, "followers", FindObjectsByType<PathFollower>(FindObjectsSortMode.None).Length);
            Record(SampleScene, "errors", _errors - errorsBefore);
            Progress();
        }

        private static PathData CreatePath(GameObject root, Vector3 start, Vector3 end, PathMovementSettings movement)
        {
            var host = new GameObject("Path");
            host.transform.SetParent(root.transform, false);
            var path = host.AddComponent<PathData>();
            path.ConfigureMovementSettings(movement);
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 500));
            path.ConfigureControlPoints(new[] { start, end });
            path.Init();
            return path;
        }

        private static MultiPathData CreateSequence(GameObject root, List<PathData> paths)
        {
            var host = new GameObject("Sequence");
            host.transform.SetParent(root.transform, false);
            var sequence = host.AddComponent<MultiPathData>();
            var segments = new PathSegmentDescriptor[paths.Count];
            for (int i = 0; i < paths.Count; i++)
                segments[i] = new PathSegmentDescriptor(paths[i], paths[i].MovementSettings, false);
            sequence.ConfigureSegments(segments);
            return sequence;
        }

        private static QueuedPathManager CreateManager(GameObject root, IPathProvider route)
        {
            var host = new GameObject("Queue");
            host.transform.SetParent(root.transform, false);
            var manager = host.AddComponent<QueuedPathManager>();
            manager.DefaultSpacing = Spacing;
            manager.ConfigureRoute(route);
            return manager;
        }

        private static QueuedPathFollower CreateAgent(GameObject root)
        {
            var agent = new GameObject("Agent");
            agent.transform.SetParent(root.transform, false);
            return agent.AddComponent<QueuedPathFollower>();
        }

        private void MovePathEnd(PathData path, Vector3 end)
        {
            _points.Clear();
            path.CopyControlPoints(_points);
            _points[_points.Count - 1] = end;
            path.ConfigureControlPoints(_points);
        }

        // Distance between neighbours on the route, leaving out those still
        // waiting at the very start. Across the seam the leader is compared with
        // the last one, as a closed loop would.
        private static float MinimumGap(QueuedPathFollower[] agents, float length, bool acrossSeam)
        {
            var progress = new List<float>(agents.Length);
            for (int i = 0; i < agents.Length; i++)
                if (agents[i].IsRegistered && agents[i].IsMoving && agents[i].GlobalNormalizedTime > 0.0001f)
                    progress.Add(agents[i].GlobalNormalizedTime);
            if (progress.Count < 2)
                return float.PositiveInfinity;
            progress.Sort();
            if (acrossSeam)
                return (progress[0] + 1f - progress[progress.Count - 1]) * length;
            float minimum = float.PositiveInfinity;
            for (int i = 1; i < progress.Count; i++)
                minimum = Mathf.Min(minimum, (progress[i] - progress[i - 1]) * length);
            return minimum;
        }

        private void Burst(string scenario, string metric, Action action)
        {
            int gcBefore = GC.CollectionCount(0);
            long allocated = Profiler.GetMonoUsedSizeLong();
            long start = Stopwatch.GetTimestamp();
            action();
            long end = Stopwatch.GetTimestamp();
            Record(scenario, metric + ".ms", (end - start) * 1000d / Stopwatch.Frequency);
            if (TransformPathProbeFrameClock.MonoUsedWorks)
                Record(scenario, metric + ".allocKB", Math.Max(0L, Profiler.GetMonoUsedSizeLong() - allocated) / 1024d);
            Record(scenario, metric + ".gc", GC.CollectionCount(0) - gcBefore);
            Progress();
        }

        private IEnumerator Frames(string scenario, int count)
        {
            _updates.Clear();
            _frames.Clear();
            long allocated = 0L;
            int gcBefore = GC.CollectionCount(0);
            for (int i = 0; i < count; i++)
            {
                yield return null;
                _updates.Add(TransformPathProbeFrameClock.LastUpdateMilliseconds);
                _frames.Add(Time.unscaledDeltaTime * 1000d);
                allocated += TransformPathProbeFrameClock.LastAllocatedBytes;
            }
            RecordFrameStats(scenario, GC.CollectionCount(0) - gcBefore);
            if (TransformPathProbeFrameClock.MonoUsedWorks)
                Record(scenario, "updateAllocBytesPerFrame", (double)allocated / count);
            Progress();
        }

        private void RecordFrameStats(string scenario, int collections)
        {
            Record(scenario, "updateMeanMs", Mean(_updates));
            Record(scenario, "updateP95Ms", Percentile(_updates, 0.95d));
            Record(scenario, "updateMaxMs", Percentile(_updates, 1d));
            Record(scenario, "frameMeanMs", Mean(_frames));
            Record(scenario, "frameMaxMs", Percentile(_frames, 1d));
            Record(scenario, "gc", collections);
        }

        private void RecordMoving(string scenario, PathFollower[] followers)
        {
            int moving = 0;
            for (int i = 0; i < followers.Length; i++)
                if (followers[i].IsMoving)
                    moving++;
            Record(scenario, "moving", moving);
        }

        private void RecordQueue(string scenario, QueuedPathFollower[] agents, float length)
        {
            int moving = 0;
            int registered = 0;
            int blocked = 0;
            for (int i = 0; i < agents.Length; i++)
            {
                if (agents[i].IsMoving) moving++;
                if (agents[i].IsRegistered) registered++;
                if (agents[i].IsBlocked) blocked++;
            }
            Record(scenario, "moving", moving);
            Record(scenario, "registered", registered);
            Record(scenario, "blocked", blocked);
            Record(scenario, "minimumGap", MinimumGap(agents, length, false));
        }

        private static double Mean(List<double> values)
        {
            if (values.Count == 0)
                return 0d;
            double total = 0d;
            for (int i = 0; i < values.Count; i++)
                total += values[i];
            return total / values.Count;
        }

        private static double Percentile(List<double> values, double fraction)
        {
            if (values.Count == 0)
                return 0d;
            var sorted = new List<double>(values);
            sorted.Sort();
            return sorted[Mathf.Clamp(Mathf.CeilToInt((float)(fraction * sorted.Count)) - 1, 0, sorted.Count - 1)];
        }

        private void Progress() => _lastProgress = Time.realtimeSinceStartup;

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                _errors++;
        }

        private void ReportError(string message)
        {
            _errors++;
            Debug.LogError($"[TransformPath probe] {message}");
        }

        private void Record(string scenario, string metric, double value)
        {
            _rows.Append(_run).Append(',')
                .Append(SystemInfo.processorType.Replace(',', ' ')).Append(',')
                .Append(SystemInfo.graphicsDeviceName.Replace(',', ' ')).Append(',')
                .Append(Application.unityVersion).Append(',')
                .Append(scenario).Append(',').Append(metric).Append(',')
                .Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }

        private void Finish()
        {
            _finished = true;
            Record("run", "errors", _errors);
            File.WriteAllText(_outputPath, _rows.ToString());
            Debug.Log($"[TransformPath probe] Finished with {_errors} errors: {_outputPath}");
            Application.Quit(_errors == 0 ? 0 : 1);
        }

        private static string ReadOutputPath()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, OutputArgument);
            return index >= 0 && index + 1 < arguments.Length
                ? arguments[index + 1]
                : Path.Combine(Application.persistentDataPath, "transformpath-probe.csv");
        }
    }
}
