using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Common.FlowField.PlayerProbe
{
    /// <summary>
    /// Measures real Player frames, which the Editor probes cannot: how long a
    /// rebuild blocks a frame, how often a moving Goal publishes, and whether
    /// the sample scenes run without errors. Only the probe build created by
    /// Tools/run-player-probe.sh contains its scene.
    /// </summary>
    public sealed class FlowFieldPlayerProbe : MonoBehaviour
    {
        private const string OutputArgument = "-flowFieldProbeOut";
        private const int TargetFrameRate = 60;
        private const int SettleFrames = 30;
        private const float TimeoutSeconds = 180f;
        private const float SampleSceneSeconds = 4f;
        private const float StallSeconds = 300f;

        [SerializeField] private FlowFieldManager _surfaceGpu;
        [SerializeField] private FlowFieldManager _surfaceManaged;
        [SerializeField] private FlowFieldManager _volumeGpu;
        [SerializeField] private FlowFieldManager _volumeManaged;
        [SerializeField] private GameObject _surfaceObstacles;
        [SerializeField] private GameObject _volumeObstacles;
        [SerializeField] private string[] _sampleScenes = Array.Empty<string>();
        [SerializeField] private int _surfaceSize;
        [SerializeField] private int _volumeSize;

        private readonly StringBuilder _rows = new StringBuilder("run,cpu,gpu,unity,backend,scenario,metric,value\n");
        private string _run;
        private string _outputPath;
        private string _backend = "none";
        private int _errors;
        private float _lastProgress;
        private bool _finished;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Application.runInBackground = true;
            Application.targetFrameRate = TargetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.logMessageReceived += OnLog;
            _run = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            _outputPath = ReadOutputPath();
            _lastProgress = Time.realtimeSinceStartup;
        }

        // An exception ends the measuring coroutine without ending the Player.
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
            Record("environment", "computeShaders", SystemInfo.supportsComputeShaders ? 1 : 0);
            Record("environment", "asyncReadback", SystemInfo.supportsAsyncGPUReadback ? 1 : 0);
            Record("environment", "staticFlagKept", _surfaceObstacles.transform.GetChild(0).gameObject.isStatic ? 1 : 0);

            yield return MeasureIdle();
            yield return MeasureSurface(_surfaceManaged, "managed");
            yield return MeasureSurface(_surfaceGpu, "gpu");
            yield return MeasureVolume(_volumeManaged, "managed");
            yield return MeasureVolume(_volumeGpu, "gpu");

            _backend = "none";
            foreach (string scene in _sampleScenes)
                yield return RunSampleScene(scene);

            Finish();
        }

        private void Finish()
        {
            _finished = true;
            _backend = "none";
            Record("run", "errors", _errors);
            Debug.Log($"[FlowField probe] Finished with {_errors} errors: {_outputPath}");
            Application.Quit(_errors == 0 ? 0 : 1);
        }

        private IEnumerator MeasureIdle()
        {
            yield return null;
            double max = 0d;
            double total = 0d;
            for (int frame = 0; frame < 120; frame++)
            {
                yield return null;
                double frameMs = Time.unscaledDeltaTime * 1000d;
                max = Math.Max(max, frameMs);
                total += frameMs;
            }
            Record("idle", "meanFrameMs", total / 120d);
            Record("idle", "maxFrameMs", max);
        }

        private IEnumerator MeasureSurface(FlowFieldManager manager, string backend)
        {
            _backend = backend;
            _surfaceObstacles.SetActive(true);
            Physics.SyncTransforms();
            float far = _surfaceSize - 2.5f;
            Vector3 firstGoal = new Vector3(far, 0f, 2.5f);
            Vector3 secondGoal = new Vector3(2.5f, 0f, far);

            yield return MeasureCommit("surface.build.initial", manager, () => manager.gameObject.SetActive(true));
            yield return MeasureCommit("surface.build.goalFirst", manager,
                () => manager.SetGoal(FlowFieldGoalRequest.Position(firstGoal)));
            MeasureWallWalk(manager, new Vector3(2.5f, 0f, far));
            for (int i = 0; i < 5; i++)
            {
                Vector3 goal = i % 2 == 0 ? secondGoal : firstGoal;
                yield return MeasureCommit($"surface.rebuild.goal{i}", manager,
                    () => manager.SetGoal(FlowFieldGoalRequest.Position(goal)));
            }

            BoxCollider moving = CreateMovingObstacle(new Vector3(100.5f, 1f, 100.5f), new Vector3(4f, 2f, 4f));
            yield return MeasureCommit("surface.rebuild.registerDynamic", manager,
                () => manager.RegisterDynamicObstacle(moving));
            for (int i = 0; i < 3; i++)
            {
                Vector3 offset = new Vector3(i % 2 == 0 ? 6f : -6f, 0f, 0f);
                yield return MeasureCommit($"surface.rebuild.moveDynamic{i}", manager,
                    () => moving.transform.position += offset);
            }

            yield return MeasureModifier("surface", manager, _surfaceSize, 1f);
            yield return MeasureMovingGoal("surface", manager, _surfaceSize, 0.5f);
            yield return MeasureRelease("surface", manager, moving);
            _surfaceObstacles.SetActive(false);
            Physics.SyncTransforms();
        }

        private IEnumerator MeasureVolume(FlowFieldManager manager, string backend)
        {
            _backend = backend;
            _volumeObstacles.SetActive(true);
            Physics.SyncTransforms();
            float far = _volumeSize - 2.5f;

            yield return MeasureCommit("volume.build.initial", manager, () => manager.gameObject.SetActive(true));
            yield return MeasureCommit("volume.build.goalFirst", manager,
                () => manager.SetGoal(FlowFieldGoalRequest.Position(new Vector3(far, 2.5f, far))));
            yield return MeasureCommit("volume.rebuild.goal", manager,
                () => manager.SetGoal(FlowFieldGoalRequest.Position(new Vector3(2.5f, far, 2.5f))));

            BoxCollider moving = CreateMovingObstacle(new Vector3(50.5f, 50.5f, 50.5f), new Vector3(3f, 3f, 3f));
            yield return MeasureCommit("volume.rebuild.registerDynamic", manager,
                () => manager.RegisterDynamicObstacle(moving));
            yield return MeasureCommit("volume.rebuild.moveDynamic", manager,
                () => moving.transform.position += new Vector3(4f, 0f, 0f));

            yield return MeasureModifier("volume", manager, _volumeSize, _volumeSize * 0.5f);
            yield return MeasureMovingGoal("volume", manager, _volumeSize, _volumeSize * 0.5f);
            yield return MeasureRelease("volume", manager, moving);
            _volumeObstacles.SetActive(false);
            Physics.SyncTransforms();
        }

        // Follows the published field across the map. A mover that crosses a
        // wall shows the wall never reached the field.
        private void MeasureWallWalk(FlowFieldManager manager, Vector3 position)
        {
            const float step = 0.5f;
            Collider[] walls = _surfaceObstacles.GetComponentsInChildren<Collider>();
            int inside = 0;
            int steps = 0;
            int limit = _surfaceSize * 8;
            while (steps < limit && manager.TrySample(position, out FlowFieldSample sample) && sample.Direction != Vector3.zero)
            {
                position += sample.Direction * step;
                steps++;
                foreach (Collider wall in walls)
                {
                    Bounds bounds = wall.bounds;
                    if (position.x > bounds.min.x && position.x < bounds.max.x
                        && position.z > bounds.min.z && position.z < bounds.max.z)
                    {
                        inside++;
                        break;
                    }
                }
            }
            Record("surface.wallWalk", "steps", steps);
            Record("surface.wallWalk", "stepsInsideWalls", inside);
        }

        private IEnumerator MeasureModifier(string mode, FlowFieldManager manager, int size, float height)
        {
            var modifierObject = new GameObject("Probe modifier");
            modifierObject.SetActive(false);
            modifierObject.transform.position = new Vector3(size * 0.5f, height, size * 0.5f);
            var box = modifierObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = Vector3.one * (size * 0.3f);
            var modifier = modifierObject.AddComponent<FlowFieldSpeedModifier>();
            modifier.SetFlowFieldManager(manager);
            modifier.SetInfluenceCollider(box);
            modifier.SetPriority(100);
            yield return MeasureCommit($"{mode}.rebuild.registerModifier", manager, () => modifierObject.SetActive(true));
            yield return MeasureCommit($"{mode}.rebuild.modifierValue", manager, () => modifier.SetSpeedMultiplier(0.5f));
            yield return MeasureCommit($"{mode}.rebuild.removeModifier", manager, () => Destroy(modifierObject));
        }

        // The Goal circles at about eight cells a second, faster than a large
        // field solves. Publications per second show what movers actually get.
        private IEnumerator MeasureMovingGoal(string mode, FlowFieldManager manager, int size, float height)
        {
            const float seconds = 10f;
            const float cellsPerSecond = 8f;
            float radius = size * 0.3f;
            Vector3 center = new Vector3(size * 0.5f, height, size * 0.5f);
            int firstRevision = manager.Revision;
            double max = 0d;
            int over33 = 0;
            int frames = 0;
            int gc = GC.CollectionCount(0);
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                float angle = elapsed * cellsPerSecond / radius;
                Vector3 goal = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                manager.SetGoal(FlowFieldGoalRequest.Position(goal));
                yield return null;
                elapsed += Time.unscaledDeltaTime;
                double frameMs = Time.unscaledDeltaTime * 1000d;
                max = Math.Max(max, frameMs);
                if (frameMs > 33.4d)
                    over33++;
                frames++;
            }
            string scenario = $"{mode}.movingGoal";
            Record(scenario, "publicationsPerSecond", (manager.Revision - firstRevision) / elapsed);
            Record(scenario, "maxFrameMs", max);
            Record(scenario, "framesOver33ms", over33);
            Record(scenario, "frames", frames);
            Record(scenario, "gcCollections", GC.CollectionCount(0) - gc);
            RecordHealth(scenario, manager);

            // Leave no solve running behind the next scenario.
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (manager.IsRebuilding && !manager.IsFaulted && Time.realtimeSinceStartup < deadline)
                yield return null;
        }

        private IEnumerator MeasureRelease(string mode, FlowFieldManager manager, BoxCollider moving)
        {
            var stopwatch = Stopwatch.StartNew();
            manager.Release();
            Record($"{mode}.release", "elapsedMs", stopwatch.Elapsed.TotalMilliseconds);
            Record($"{mode}.release", "monoUsedMiB", Profiler.GetMonoUsedSizeLong() / (1024d * 1024d));
            Destroy(moving.gameObject);
            manager.gameObject.SetActive(false);
            yield return null;
        }

        private IEnumerator MeasureCommit(string scenario, FlowFieldManager manager, Action trigger)
        {
            int revision = manager.Revision;
            int gc = GC.CollectionCount(0);
            var stopwatch = Stopwatch.StartNew();
            trigger();
            double max = 0d;
            int frames = 0;
            int over20 = 0;
            int over33 = 0;
            bool done = false;
            void CountFrame()
            {
                double frameMs = Time.unscaledDeltaTime * 1000d;
                max = Math.Max(max, frameMs);
                if (frameMs > 20d)
                    over20++;
                if (frameMs > 33.4d)
                    over33++;
            }
            while (!done && !manager.IsFaulted && stopwatch.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                yield return null;
                frames++;
                CountFrame();
                done = manager.Revision != revision && manager.IsReady && !manager.IsRebuilding;
            }
            double elapsed = stopwatch.Elapsed.TotalMilliseconds;
            int gcDuring = GC.CollectionCount(0) - gc;

            // unscaledDeltaTime reports the previous frame, so the frame that
            // did the publishing only shows up one frame later.
            yield return null;
            CountFrame();

            // A collection of the replaced field can land just after a commit.
            double settleMax = 0d;
            gc = GC.CollectionCount(0);
            for (int frame = 0; frame < SettleFrames; frame++)
            {
                yield return null;
                settleMax = Math.Max(settleMax, Time.unscaledDeltaTime * 1000d);
            }

            Record(scenario, "elapsedMs", elapsed);
            Record(scenario, "frames", frames);
            Record(scenario, "maxFrameMs", max);
            Record(scenario, "framesOver20ms", over20);
            Record(scenario, "framesOver33ms", over33);
            Record(scenario, "gcCollections", gcDuring);
            Record(scenario, "settleMaxFrameMs", settleMax);
            Record(scenario, "settleGcCollections", GC.CollectionCount(0) - gc);
            if (!done)
                ReportError($"{scenario} did not publish: {manager.State} {manager.LastError}");
            RecordHealth(scenario, manager);
        }

        private void RecordHealth(string scenario, FlowFieldManager manager)
        {
            if (manager.IsFaulted)
                ReportError($"{scenario} faulted: {manager.LastError}");
            if (_backend == "gpu" && manager.GpuFallbackReason != null)
                ReportError($"{scenario} left the GPU path: {manager.GpuFallbackReason}");
        }

        private IEnumerator RunSampleScene(string scene)
        {
            yield return LoadScene(scene);
            FlowFieldManager manager = FindAnyObjectByType<FlowFieldManager>();
            if (manager == null)
            {
                yield return new WaitForSecondsRealtime(SampleSceneSeconds);
                Record(scene, "ran", 1);
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!manager.IsReady && !manager.IsFaulted && Time.realtimeSinceStartup < deadline)
                yield return null;
            yield return new WaitForSecondsRealtime(SampleSceneSeconds);
            RecordManager(scene, manager);
        }

        private static IEnumerator LoadScene(string scene)
        {
            AsyncOperation operation = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
            while (operation != null && !operation.isDone)
                yield return null;
        }

        private void RecordManager(string scene, FlowFieldManager manager)
        {
            string scenario = $"{scene}.{manager.BakeMode}";
            Record(scenario, "ready", manager.IsReady ? 1 : 0);
            Record(scenario, "revision", manager.Revision);
            Record(scenario, "gpuFallback", manager.GpuFallbackReason != null ? 1 : 0);
            bool hasField = manager.TryGetFieldInfo(out _);
            Record(scenario, "hasField", hasField ? 1 : 0);
            if (manager.IsFaulted || !manager.IsReady || !hasField)
                ReportError($"{scenario} is not usable: {manager.State} {manager.LastError}");
        }

        private static BoxCollider CreateMovingObstacle(Vector3 position, Vector3 size)
        {
            var obstacle = new GameObject("Probe moving obstacle") { layer = FlowFieldPlayerProbeLayers.Obstacle };
            obstacle.transform.position = position;
            var box = obstacle.AddComponent<BoxCollider>();
            box.size = size;
            Physics.SyncTransforms();
            return box;
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                _errors++;
        }

        private void ReportError(string message) => Debug.LogError($"[FlowField probe] {message}");

        private void Record(string scenario, string metric, double value)
        {
            _lastProgress = Time.realtimeSinceStartup;
            _rows.Append(_run).Append(',')
                .Append(SystemInfo.processorType.Replace(',', ' ')).Append(',')
                .Append(SystemInfo.graphicsDeviceName.Replace(',', ' ')).Append(',')
                .Append(Application.unityVersion).Append(',')
                .Append(_backend).Append(',')
                .Append(scenario).Append(',')
                .Append(metric).Append(',')
                .Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
            // A crash must still leave the rows measured so far.
            WriteRows();
        }

        private void WriteRows()
        {
            // A full disk must cost the file, not the rest of the measurements.
            try
            {
                File.WriteAllText(_outputPath, _rows.ToString());
            }
            catch (IOException exception)
            {
                ReportError($"Could not write {_outputPath}: {exception.Message}");
            }
        }

        private static string ReadOutputPath()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < arguments.Length; i++)
            {
                if (arguments[i] == OutputArgument)
                    return arguments[i + 1];
            }
            return Path.Combine(Application.persistentDataPath, "flowfield-player-probe.csv");
        }
    }

    public static class FlowFieldPlayerProbeLayers
    {
        public const int Ground = 29;
        public const int Obstacle = 30;
    }
}
