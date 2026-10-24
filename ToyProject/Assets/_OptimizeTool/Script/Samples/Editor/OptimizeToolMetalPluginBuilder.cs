#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;

namespace Common.OptimizeTool.Samples.Editor
{
    public static class OptimizeToolMetalPluginBuilder
    {
        public const string PluginPath = "Assets/_OptimizeTool/Plugins/macOS/OptimizeToolGpuTimer.bundle";
        public static void Build()
        {
            const string source = "Assets/_OptimizeTool/Script/Samples/Editor/Native/OptimizeToolGpuTimer.mm";
            Directory.CreateDirectory(Path.GetDirectoryName(PluginPath));
            string headers = Path.Combine(EditorApplication.applicationContentsPath, "PluginAPI");
            string arguments = "clang++ -bundle -fobjc-arc -std=c++17 -arch arm64 -arch x86_64 -mmacosx-version-min=11.0 -I \"" + headers + "\" -framework Metal -framework Foundation \"" + Path.GetFullPath(source) + "\" -o \"" + Path.GetFullPath(PluginPath) + "\"";
            using (var process = Process.Start(new ProcessStartInfo("/usr/bin/xcrun", arguments) { UseShellExecute = false, RedirectStandardError = true }))
            {
                string error = process.StandardError.ReadToEnd(); process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("Metal plugin compilation failed: " + error);
            }
            AssetDatabase.ImportAsset(PluginPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (PluginImporter)AssetImporter.GetAtPath(PluginPath);
            importer.SetCompatibleWithAnyPlatform(false); importer.SetCompatibleWithEditor(false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true); importer.isPreloaded = true;
            importer.SaveAndReimport();
        }
    }
}
#endif
