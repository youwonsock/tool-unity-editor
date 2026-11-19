using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;

namespace Common.OptimizeTool.Benchmark.Editor
{
    public static class OptimizeToolMetalPluginBuilder
    {
        public const string PluginPath = "Assets/_OptimizeTool/Dev/Benchmark/Plugins/macOS/OptimizeToolGpuTimer.bundle";
        /// <summary>Only Players built with this define load the timer; the benchmark builds pass it.</summary>
        public const string BenchmarkDefine = "OPTIMIZETOOL_BENCHMARK";

        public static void Build()
        {
            const string source = "Assets/_OptimizeTool/Dev/Editor/Native/OptimizeToolGpuTimer.mm";
            Directory.CreateDirectory(Path.GetDirectoryName(PluginPath));
            string headers = Path.Combine(EditorApplication.applicationContentsPath, "PluginAPI");
            string arguments = "clang++ -bundle -fobjc-arc -std=c++17 -arch arm64 -arch x86_64 -mmacosx-version-min=11.0 -I \"" + headers + "\" -framework Metal -framework Foundation \"" + Path.GetFullPath(source) + "\" -o \"" + Path.GetFullPath(PluginPath) + "\"";
            using (var process = Process.Start(new ProcessStartInfo("/usr/bin/xcrun", arguments) { UseShellExecute = false, RedirectStandardError = true }))
            {
                string error = process.StandardError.ReadToEnd(); process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("Metal plugin compilation failed: " + error);
            }
            AssetDatabase.ImportAsset(PluginPath, ImportAssetOptions.ForceSynchronousImport);
            Configure((PluginImporter)AssetImporter.GetAtPath(PluginPath));
        }

        /// <summary>macOS Players only, preloaded, and only in builds that define <see cref="BenchmarkDefine"/>.</summary>
        public static void Configure(PluginImporter importer)
        {
            importer.SetCompatibleWithAnyPlatform(false); importer.SetCompatibleWithEditor(false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true); importer.isPreloaded = true;
            importer.DefineConstraints = new[] { BenchmarkDefine };
            importer.SaveAndReimport();
        }
    }
}
