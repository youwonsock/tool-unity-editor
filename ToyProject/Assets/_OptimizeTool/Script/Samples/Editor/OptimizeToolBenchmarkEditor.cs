#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Build;
using UnityEngine;

namespace Common.OptimizeTool.Samples.Editor
{
    /// <summary>Builds only the sample scene without changing the project's build or Player settings.</summary>
    public static class OptimizeToolBenchmarkEditor
    {
        [MenuItem("Tools/OptimizeTool/Benchmark/Build macOS Development Player")]
        public static void BuildFromMenu()
        {
            BeginBuild(Path.Combine(Path.GetTempPath(), "OptimizeToolBenchmark-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        }

        [MenuItem("Tools/OptimizeTool/Benchmark/Build macOS GPU Benchmark Player")]
        public static void BuildGpuFromMenu() => BeginBuild(Path.Combine(Path.GetTempPath(), "OptimizeToolGpuBenchmark-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")), true);

        public static string BeginBuild(string outputDirectory, bool gpu = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("Build the benchmark in Edit Mode after compilation.");
            string folder = PrepareDirectory(outputDirectory);
            EditorApplication.delayCall += () =>
            {
                try { BuildNow(folder, gpu); }
                catch (Exception exception)
                {
                    File.WriteAllText(Path.Combine(folder, "build-status.json"),
                        JsonUtility.ToJson(new BuildStatus { status = "Failed", error = exception.ToString() }, true));
                    Debug.LogException(exception);
                }
            };
            return folder;
        }

        [MenuItem("Tools/OptimizeTool/Benchmark/Capture Editor Play Mode")]
        public static void CaptureEditorFromMenu()
        {
            StartEditorCapture(Path.Combine(Path.GetTempPath(), "OptimizeToolCapture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        }

        public static string StartEditorCapture(string directory, int rounds = OptimizeToolPerformanceBenchmark.DefaultRounds)
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Open the sample and enter Play Mode first.");
            var view = new GameViewCaptureScope(1920, 1080);
            try
            {
                string folder = Path.GetFullPath(directory);
                OptimizeToolPerformanceBenchmark benchmark = OptimizeToolPerformanceBenchmark.StartEditorRun(folder, rounds);
                benchmark.Completed += exitCode =>
                {
                    view.Dispose();
                    Debug.Log("OptimizeTool Editor benchmark finished (exit " + exitCode + "): " + folder);
                };
                return folder;
            }
            catch
            {
                view.Dispose();
                throw;
            }
        }

        // Unity -batchmode -executeMethod Common.OptimizeTool.Samples.Editor.OptimizeToolBenchmarkEditor.BuildBatch
        //       --optimize-build-output /absolute/output/folder -quit
        public static void BuildBatch()
        {
            string[] args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, "--optimize-build-output");
            if (flag < 0 || flag + 1 == args.Length) throw new ArgumentException("Supply --optimize-build-output.");
            if (Array.IndexOf(args, "--optimize-arm64") >= 0)
            {
                if (!Application.isBatchMode) throw new InvalidOperationException("Use --optimize-arm64 in an isolated batch build project.");
                PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, 1);
            }
            BuildNow(PrepareDirectory(args[flag + 1]), Array.IndexOf(args, "--optimize-gpu") >= 0);
        }

        public static void BuildScaledBatch()
        {
            OptimizeToolScaleBuilder.Build();
            BuildBatch();
        }

        public static void BuildGpuBatch()
        {
            OptimizeToolScaleBuilder.BuildGpu();
            BuildBatch();
        }

        private static string PrepareDirectory(string directory)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
                throw new InvalidOperationException("The benchmark requires the macOS build target.");
            string folder = Path.GetFullPath(directory);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "build-status.json"), "{\"status\":\"queued\"}");
            return folder;
        }

        private static void BuildNow(string folder, bool gpu = false)
        {
            if (gpu) OptimizeToolMetalPluginBuilder.Build();
            string scene = gpu ? OptimizeToolScaleBuilder.GpuScenePath : OptimizeToolScaleBuilder.ScenePath;
            if (!File.Exists(scene)) { if (gpu) OptimizeToolScaleBuilder.BuildGpu(); else OptimizeToolScaleBuilder.Build(); }
            string status = Path.Combine(folder, "build-status.json");
            File.WriteAllText(status, "{\"status\":\"building\"}");
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = Path.Combine(folder, "OptimizeToolBenchmark.app"),
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.Development
            });
            File.WriteAllText(status, JsonUtility.ToJson(new BuildStatus
            {
                status = report.summary.result.ToString(), errors = report.summary.totalErrors,
                warnings = report.summary.totalWarnings, durationSeconds = report.summary.totalTime.TotalSeconds,
                outputPath = report.summary.outputPath
            }, true));
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Benchmark Player build failed; inspect build-status.json and the Unity log.");
            Debug.Log("OptimizeTool benchmark build succeeded at " + folder);
        }

        [Serializable]
        private sealed class BuildStatus
        {
            public string status, outputPath, error;
            public int errors, warnings;
            public double durationSeconds;
        }
    }
}
#endif
