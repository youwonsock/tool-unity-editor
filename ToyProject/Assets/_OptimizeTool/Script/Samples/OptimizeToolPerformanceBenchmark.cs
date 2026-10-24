using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Common.OptimizeTool.Samples
{
    /// <summary>Opt-in benchmark; normal sample runs never create this component or write results.</summary>
    public sealed class OptimizeToolPerformanceBenchmark : MonoBehaviour
    {
        public const string CommandLineFlag = "--optimize-benchmark";
        public const int WarmupFrames = 90;
        public const int SamplesPerBlock = 240;
        public const int DefaultRounds = 8;
        public const float StartupSettleSeconds = 3f;
        public const float MinimumWarmupSeconds = 0.25f;
        private static readonly int[][] Orders = CreateOrders();
        private static int[][] CreateOrders()
        {
            int count = OptimizeToolSampleModes.MeshCount;
            var orders = new int[count * 2][];
            for (int shift = 0; shift < count; shift++)
            {
                orders[shift] = new int[count]; orders[count + shift] = new int[count];
                for (int i = 0; i < count; i++)
                {
                    orders[shift][i] = (shift + i) % count;
                    orders[count + shift][i] = (2 * count - 1 - shift - i) % count;
                }
            }
            return orders;
        }

        [Serializable]
        public sealed class Metadata
        {
            public string utc, unityVersion, platform, operatingSystem, cpu, gpu, graphicsApi, graphicsDriver, quality, renderPipeline;
            public string scenePath, scope, timingDefinition, shader, runId, buildGuid, processArchitecture;
            public int processId;
            public int schemaVersion;
            public string captureId;
            public int sourceCubeCount, scaleCaseIndex, faceSubdivisions, orderOffset;
            public int[] rendererCounts, vertexCounts;
            public long[] triangleCounts;
            public bool cameraOrthographic;
            public float cameraOrthographicSize;
            public Vector3 cameraPosition;
            public Quaternion cameraRotation;
            public int cpuCores, memoryMb, width, height, cameraWidth, cameraHeight, vSyncCount, targetFrameRate;
            public int warmupFrames, samplesPerBlock, rounds, msaa, frameTimingHistory, historyOffset;
            public float renderScale;
            public float startupSettleSeconds, minimumWarmupSeconds, preconditionSeconds;
            public bool gpuRecorderSupported;
            public bool gpuQueueDrained;
            public bool offscreen, batchMode;
            public string gpuTimingSource;
            public bool splashFinishedBeforeCapture;
            public bool editor, developmentBuild, frameTimingEnabled, srpBatcher, dynamicBatching, materialInstancing;
            public bool drawCallsCounterValid, batchesCounterValid, setPassCounterValid, trianglesCounterValid;
            public ulong gpuTimerFrequency, cpuTimerFrequency;
        }

        private const int TimingHistory = 32;
        private const int HistoryOffset = 16;
        private readonly FrameTiming[] _timing = new FrameTiming[TimingHistory];
        private sealed class TimestampComparer : IComparer<FrameTiming>
        {
            public static readonly TimestampComparer Instance = new TimestampComparer();
            public int Compare(FrameTiming a, FrameTiming b) => a.frameStartTimestamp.CompareTo(b.frameStartTimestamp);
        }
        private readonly List<BenchmarkSample> _samples = new List<BenchmarkSample>(DefaultRounds * OptimizeToolSampleModes.MeshCount * SamplesPerBlock);
        private ProfilerRecorder _draws, _batches, _setPass, _triangles, _vertices;
        private OptimizeToolOverviewController _controller;
        private OptimizeToolScaleWorkload _scale;
        private string _output;
        private BenchmarkCaptureOutput _capture;
        private BenchmarkWorkloadScope _workload;
        private int _rounds;
        private bool _settingsCaptured, _finished;
        private BenchmarkEnvironmentScope _environment;
        private ulong _lastTimestamp;
        private Metadata _metadata;
        private float _warmupSeconds = MinimumWarmupSeconds, _preconditionSeconds;
        private int _orderOffset;
        private BenchmarkGpuDrain _gpuDrain;
        private BenchmarkRenderTargetScope _renderTarget;
        private BenchmarkMetalGpuTimer _metalTimer;
        public event Action<int> Completed;
        public bool IsCompleted { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string[] args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, CommandLineFlag);
            if (flag < 0) return;
            if (flag + 1 >= args.Length) { Debug.LogError("Supply an output directory after " + CommandLineFlag); return; }
            BenchmarkCaptureOutput capture;
            try { capture = new BenchmarkCaptureOutput(args[flag + 1]); }
            catch (Exception error) { Debug.LogException(error); Application.Quit(1); return; }
            var go = new GameObject("OptimizeTool Performance Benchmark");
            var benchmark = go.AddComponent<OptimizeToolPerformanceBenchmark>();
            benchmark._output = capture.DirectoryPath; benchmark._capture = capture;
            benchmark._rounds = DefaultRounds;
            int roundsFlag = Array.IndexOf(args, "--optimize-benchmark-rounds");
            if (roundsFlag >= 0 && roundsFlag + 1 < args.Length && int.TryParse(args[roundsFlag + 1], out int rounds))
                benchmark._rounds = Mathf.Clamp(rounds, 1, 32);
        }

        public static OptimizeToolPerformanceBenchmark StartEditorRun(string outputDirectory, int rounds = DefaultRounds)
        {
            if (!Application.isEditor || !Application.isPlaying) throw new InvalidOperationException("Start an Editor benchmark during Play Mode.");
            if (FindFirstObjectByType<OptimizeToolPerformanceBenchmark>() != null) throw new InvalidOperationException("A benchmark is already running.");
            var capture = new BenchmarkCaptureOutput(outputDirectory);
            var benchmark = new GameObject("OptimizeTool Performance Benchmark").AddComponent<OptimizeToolPerformanceBenchmark>();
            benchmark._output = capture.DirectoryPath; benchmark._capture = capture;
            benchmark._rounds = Mathf.Clamp(rounds, 1, 32);
            return benchmark;
        }

        private void DisableForMeasurement(Behaviour behaviour) => _environment.Disable(behaviour);

        private IEnumerator Start()
        {
            if (string.IsNullOrEmpty(_output)) yield break;
            _environment = new BenchmarkEnvironmentScope();
            _settingsCaptured = true;
            _environment.Apply();
            if (!Application.isEditor && !Application.isBatchMode) Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
            yield return null;
            yield return null;
            IEnumerator work = Run();
            while (true)
            {
                object next = null;
                bool more;
                try { more = work.MoveNext(); if (more) next = work.Current; }
                catch (Exception exception)
                {
                    TryWriteError(exception.ToString());
                    Debug.LogException(exception);
                    Finish(1);
                    yield break;
                }
                if (!more) break;
                yield return next;
            }
            Finish(0);
        }

        private IEnumerator Run()
        {
            Directory.CreateDirectory(_output);
            var options = new BenchmarkRunOptions(Environment.GetCommandLineArgs(), Application.isEditor, Application.isBatchMode);
            bool offscreen = options.Offscreen;
            _warmupSeconds = options.WarmupSeconds; _preconditionSeconds = options.PreconditionSeconds;
            _orderOffset = options.OrderOffset;
            if (options.FrameRate.HasValue) Application.targetFrameRate = options.FrameRate.Value;
            if (!Application.isEditor)
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 60d;
                while (!offscreen && (!Application.isFocused || !SplashScreen.isFinished) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                if (!offscreen && (!Application.isFocused || !SplashScreen.isFinished))
                    throw new InvalidOperationException("The focused Player and splash screen must finish initializing before capture.");
                double settleUntil = Time.realtimeSinceStartupAsDouble + StartupSettleSeconds;
                while (Time.realtimeSinceStartupAsDouble < settleUntil) yield return null;
            }
            _scale = FindFirstObjectByType<OptimizeToolScaleWorkload>();
            if (_scale != null)
            {
                _workload = new BenchmarkWorkloadScope(_scale, null);
                if (options.CubeCount.HasValue) _scale.SelectCubeCount(options.CubeCount.Value);
                SelectMode(0); DisableForMeasurement(_scale);
            }
            else
            {
                _controller = FindFirstObjectByType<OptimizeToolOverviewController>();
                if (_controller == null || !_controller.IsInitialized || _controller.FeatureCount < OptimizeToolSampleModes.MeshCount)
                    throw new InvalidOperationException("The benchmark requires an initialized scale or overview workload.");
                if (_controller.PhysicsDemo != null && _controller.PhysicsDemo.IsBusy)
                    throw new InvalidOperationException("Finish the active physics recording before starting a mesh benchmark.");
                _workload = new BenchmarkWorkloadScope(null, _controller);
                _controller.SelectFeature(0); _controller.UseBakeView(); DisableForMeasurement(_controller);
            }
            foreach (OptimizeToolShowcaseUI ui in FindObjectsByType<OptimizeToolShowcaseUI>(FindObjectsSortMode.None)) DisableForMeasurement(ui);
            foreach (OptimizeToolFreeCamera camera in FindObjectsByType<OptimizeToolFreeCamera>(FindObjectsSortMode.None)) DisableForMeasurement(camera);
            foreach (OptimizeToolOverviewBoard board in FindObjectsByType<OptimizeToolOverviewBoard>(FindObjectsSortMode.None))
            {
                if (board.TryGetComponent(out Canvas canvas)) DisableForMeasurement(canvas);
                DisableForMeasurement(board);
            }
            // A physics demo can coexist with the showcase but is not part of the mesh workload.
            foreach (OptimizeToolPhysicsDemo physics in FindObjectsByType<OptimizeToolPhysicsDemo>(FindObjectsSortMode.None))
                DisableForMeasurement(physics);
            if (offscreen)
            {
                _renderTarget = new BenchmarkRenderTargetScope(_scale != null ? _scale.Camera : Camera.main);
                _metalTimer = new BenchmarkMetalGpuTimer();
            }
            _draws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1);
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count", 1);
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count", 1);
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count", 1);
            _vertices = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count", 1);
            if (!_draws.Valid) throw new InvalidOperationException("The Draw Calls Count counter is unavailable in this Player.");
            if (!FrameTimingManager.IsFeatureEnabled()) throw new InvalidOperationException("Frame Timing is unavailable. Use a Development Player or enable Frame Timing Stats.");
            if (options.DrainGpu)
            {
                if (Application.isEditor) throw new InvalidOperationException("GPU drain diagnostics require a separate Player.");
                _gpuDrain = new BenchmarkGpuDrain();
                if (offscreen) RenderPipelineManager.endFrameRendering += DrainOffscreenFrame;
                else StartCoroutine(DrainGpuFrames());
            }
            double conditionUntil = Time.realtimeSinceStartupAsDouble + _preconditionSeconds;
            while (Time.realtimeSinceStartupAsDouble < conditionUntil) { FrameTimingManager.CaptureFrameTimings(); yield return null; }
            // Warm all material/geometry paths before the first measurement.
            for (int mode = 0; mode < OptimizeToolSampleModes.MeshCount; mode++)
            {
                SelectMode(mode);
                double warmupUntil = Time.realtimeSinceStartupAsDouble + _warmupSeconds;
                for (int frame = 0; frame < WarmupFrames || Time.realtimeSinceStartupAsDouble < warmupUntil; frame++)
                { FrameTimingManager.CaptureFrameTimings(); yield return null; }
                if (options.Screenshots)
                {
                    if (_renderTarget != null) _renderTarget.SavePng(Path.Combine(_output, "mode-" + mode + ".png"));
                    else
                    {
                        yield return new WaitForEndOfFrame();
                        ScreenCapture.CaptureScreenshot(Path.Combine(_output, "mode-" + mode + ".png"));
                    }
                    yield return null;
                }
            }
            if (!Application.isEditor && !offscreen && !Application.isFocused) throw new InvalidOperationException("Player lost focus during warmup; capture rejected.");
            _metadata = CaptureMetadata();
            _capture.WriteMetadata(_metadata);
            for (int round = 0; round < _rounds; round++)
            {
                int[] modes = Orders[(round + _orderOffset) % Orders.Length];
                for (int order = 0; order < modes.Length; order++)
                {
                    int mode = modes[order];
                    SelectMode(mode);
                    double warmupUntil = Time.realtimeSinceStartupAsDouble + _warmupSeconds;
                    for (int frame = 0; frame < WarmupFrames || Time.realtimeSinceStartupAsDouble < warmupUntil; frame++)
                    { FrameTimingManager.CaptureFrameTimings(); yield return null; }
                    int collected = 0, attempts = 0;
                    int firstMetalFrame = _metalTimer == null ? 0 : _metalTimer.NextFrame;
                    _metalTimer?.Clear();
                    while (collected < SamplesPerBlock)
                    {
                        FrameTimingManager.CaptureFrameTimings();
                        yield return null;
                        if (++attempts > SamplesPerBlock * 10)
                            throw new InvalidOperationException("Not enough unique frame timing records were returned. " + _metalTimer?.Diagnostic());
                        uint available = FrameTimingManager.GetLatestTimings(TimingHistory, _timing);
                        FrameTiming t = default;
                        if (available > HistoryOffset)
                        {
                            Array.Sort(_timing, 0, (int)available, TimestampComparer.Instance);
                            t = _timing[(int)available - 1 - HistoryOffset];
                        }
                        bool cpuTimingValid = t.frameStartTimestamp > _lastTimestamp && t.cpuFrameTime > 0;
                        if (!cpuTimingValid)
                        {
                            if (_metalTimer == null) continue;
                            t = default; // Unsupported offscreen FrameTimingManager values remain zero, never fabricated.
                        }
                        BenchmarkMetalGpuTimer.Result metal = default;
                        if (_metalTimer != null && !_metalTimer.TryRead(firstMetalFrame, out metal)) continue;
                        if (cpuTimingValid) _lastTimestamp = t.frameStartTimestamp;
                        _samples.Add(new BenchmarkSample
                        {
                            round = round, order = order, mode = mode, observedFrame = Time.frameCount,
                            timestamp = t.frameStartTimestamp, syncInterval = t.syncInterval, widthScale = t.widthScale, heightScale = t.heightScale, draws = Value(_draws), batches = Value(_batches),
                            setPass = Value(_setPass), triangles = Value(_triangles), vertices = Value(_vertices),
                            cpu = t.cpuFrameTime, main = t.cpuMainThreadFrameTime, render = t.cpuRenderThreadFrameTime,
                            presentWait = t.cpuMainThreadPresentWaitTime, gpu = t.gpuFrameTime,
                            unscaledDelta = Time.unscaledDeltaTime * 1000.0, focused = Application.isFocused,
                            gpuDrainComplete = _gpuDrain != null && _gpuDrain.LastFrame == Time.frameCount - 1, metal = metal
                        });
                        collected++;
                    }
                    File.WriteAllText(Path.Combine(_output, "progress.json"),
                        "{\"completedBlocks\":" + (round * OptimizeToolSampleModes.MeshCount + order + 1) + ",\"totalBlocks\":" + (_rounds * OptimizeToolSampleModes.MeshCount) + "}");
                }
            }
            _capture.Complete(BenchmarkCsvWriter.Serialize(_samples), _samples.Count);
            IsCompleted = true;
        }

        private void LateUpdate()
        {
            if (_finished || _renderTarget == null) return;
            try { _renderTarget.Render(); }
            catch (Exception error) { StopAllCoroutines(); TryWriteError(error.ToString()); Debug.LogException(error); Finish(1); }
        }

        private void SelectMode(int mode)
        {
            if (_scale != null) _scale.SelectMode(mode);
            else _controller.SelectFeature(mode);
        }

        private IEnumerator DrainGpuFrames()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (!_finished)
            {
                yield return endOfFrame;
                // Cancellation or normal completion can dispose the drain while this yield is pending.
                if (_finished || _gpuDrain == null) yield break;
                try { _gpuDrain.Drain(); }
                catch (Exception error)
                {
                    TryWriteError(error.ToString()); Debug.LogException(error); Finish(1); yield break;
                }
            }
        }

        private void DrainOffscreenFrame(ScriptableRenderContext context, Camera[] cameras)
        {
            if (_finished || _gpuDrain == null || _renderTarget == null) return;
            try { _gpuDrain.Drain(_renderTarget.Target); }
            catch (Exception error) { TryWriteError(error.ToString()); Debug.LogException(error); Finish(1); }
        }

        private static long Value(ProfilerRecorder recorder) => recorder.Valid ? recorder.LastValue : -1;

        private Metadata CaptureMetadata()
        {
            SelectMode(0);
            Material material = _scale != null ? _scale.Material : _controller.transform
                .Find(OptimizeToolSampleModes.All[OptimizeToolSampleModes.Original].Group).GetComponentInChildren<MeshRenderer>().sharedMaterial;
            Camera camera = _scale != null ? _scale.Camera : Camera.main;
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset
                ?? throw new InvalidOperationException("OptimizeTool benchmarks require the project's Universal Render Pipeline asset.");
            return new Metadata
            {
                utc = DateTime.UtcNow.ToString("o"), runId = Path.GetFileName(_output), buildGuid = Application.buildGUID,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                sourceCubeCount = _scale == null ? 9 : _scale.Current.cubeCount,
                scaleCaseIndex = _scale == null ? -1 : _scale.ActiveCase,
                faceSubdivisions = _scale == null ? 1 : Mathf.Max(1, _scale.Current.faceSubdivisions), orderOffset = _orderOffset,
                rendererCounts = _scale == null ? null : _scale.Current.rendererCounts,
                vertexCounts = _scale == null ? null : _scale.Current.vertexCounts,
                triangleCounts = _scale == null ? null : _scale.Current.triangleCounts,
                cameraOrthographic = camera.orthographic, cameraOrthographicSize = camera.orthographicSize,
                cameraPosition = camera.transform.position, cameraRotation = camera.transform.rotation,
                unityVersion = Application.unityVersion, platform = Application.platform.ToString(), editor = Application.isEditor,
                developmentBuild = Debug.isDebugBuild, operatingSystem = SystemInfo.operatingSystem,
                cpu = SystemInfo.processorType, cpuCores = SystemInfo.processorCount, memoryMb = SystemInfo.systemMemorySize,
                gpu = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(), graphicsDriver = SystemInfo.graphicsDeviceVersion,
                gpuTimerFrequency = FrameTimingManager.GetGpuTimerFrequency(), cpuTimerFrequency = FrameTimingManager.GetCpuTimerFrequency(),
                width = Screen.width, height = Screen.height, cameraWidth = camera.pixelWidth, cameraHeight = camera.pixelHeight,
                quality = QualitySettings.names[QualitySettings.GetQualityLevel()], renderPipeline = pipeline.name,
                srpBatcher = GraphicsSettings.useScriptableRenderPipelineBatching, msaa = pipeline.msaaSampleCount,
                renderScale = pipeline.renderScale, dynamicBatching = pipeline.supportsDynamicBatching,
                vSyncCount = QualitySettings.vSyncCount, targetFrameRate = Application.targetFrameRate,
                warmupFrames = WarmupFrames, samplesPerBlock = SamplesPerBlock, rounds = _rounds, frameTimingHistory = TimingHistory, historyOffset = HistoryOffset,
                startupSettleSeconds = Application.isEditor ? 0 : StartupSettleSeconds, minimumWarmupSeconds = _warmupSeconds,
                preconditionSeconds = _preconditionSeconds, gpuRecorderSupported = SystemInfo.supportsGpuRecorder, gpuQueueDrained = _gpuDrain != null,
                offscreen = _renderTarget != null, batchMode = Application.isBatchMode,
                gpuTimingSource = _metalTimer == null ? "FrameTimingManager" : "Metal GPUStartTime/GPUEndTime; explicit single-command-buffer camera scope, identity validated",
                splashFinishedBeforeCapture = Application.isEditor || Application.isBatchMode || SplashScreen.isFinished,
                frameTimingEnabled = FrameTimingManager.IsFeatureEnabled(), scenePath = gameObject.scene.path,
                shader = material.shader.name, materialInstancing = material.enableInstancing,
                drawCallsCounterValid = _draws.Valid, batchesCounterValid = _batches.Valid,
                setPassCounterValid = _setPass.Valid, trianglesCounterValid = _triangles.Valid,
                scope = _scale == null ? "Original nine-cube sample; floor/skybox/pipeline passes included; sample UI and updates disabled"
                    : "Prebaked opaque cubes; one orthographic camera; three aligned depth layers; shadows, light/reflection probes, instancing and authoring colliders disabled; no UI or scripted movement during measurement. All four modes use the same camera per scale.",
                timingDefinition = "FrameTimingManager CPU total includes waits; GPU is native frame duration including phase gaps, not isolated shader work. CPU timing uses a fixed 16-record offset in 32 records sorted by timestamp. Counter values belong to observed_frame; Metal IDs come from a separate completion queue. Rows are block-level observations, not aligned CPU/GPU frame pairs; zero timings are unavailable, never valid zero-cost frames. Player waits for splash and focus, then 3 real seconds. Warmup uses both 90 frames and the configured minimum seconds; preconditioning uses Original. wall_delta_ms is observed loop duration, not display-present FPS."
            };
        }

        public void Cancel()
        {
            if (_finished) return;
            StopAllCoroutines();
            try { TryWriteError("Capture interrupted; incomplete data must not be used."); }
            finally { Finish(2); }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (focused || Application.isEditor || Application.isBatchMode || _metadata == null || _finished) return;
            StopAllCoroutines();
            try { TryWriteError("Player lost focus; entire capture rejected."); }
            finally { Finish(2); }
        }

        private void TryWriteError(string error)
        {
            try { _capture?.WriteError(error); }
            catch (Exception failure) { Debug.LogWarning("Unable to write benchmark diagnostic: " + failure.Message); }
        }

        private void OnDisable()
        {
            if (_finished || !_settingsCaptured) return;
            StopAllCoroutines();
            try { TryWriteError("Capture interrupted; incomplete data must not be used."); }
            finally { Finish(2, true); }
        }

        private void OnDestroy()
        {
            if (_finished || !_settingsCaptured) return;
            try { TryWriteError("Capture interrupted; incomplete data must not be used."); }
            finally { Finish(2, true); }
        }

        private void Finish(int exitCode, bool destroying = false)
        {
            if (_finished) return;
            _finished = true;
            try
            {
                BenchmarkEnvironmentScope.Restore(() => _draws.Dispose());
                BenchmarkEnvironmentScope.Restore(() => _batches.Dispose());
                BenchmarkEnvironmentScope.Restore(() => _setPass.Dispose());
                BenchmarkEnvironmentScope.Restore(() => _triangles.Dispose());
                BenchmarkEnvironmentScope.Restore(() => _vertices.Dispose());
            }
            finally
            {
                RenderPipelineManager.endFrameRendering -= DrainOffscreenFrame;
                if (_metalTimer != null) { BenchmarkEnvironmentScope.Restore(_metalTimer.Dispose); _metalTimer = null; }
                if (_gpuDrain != null) { BenchmarkEnvironmentScope.Restore(_gpuDrain.Dispose); _gpuDrain = null; }
                if (_renderTarget != null) { BenchmarkEnvironmentScope.Restore(_renderTarget.Dispose); _renderTarget = null; }
                _environment?.Dispose();
                _workload?.Dispose();
                if (Completed != null)
                    foreach (Action<int> callback in Completed.GetInvocationList())
                        BenchmarkEnvironmentScope.Restore(() => callback(exitCode));
                if (!Application.isEditor) Application.Quit(exitCode);
                else if (!destroying) Destroy(gameObject);
            }
        }
    }
}
