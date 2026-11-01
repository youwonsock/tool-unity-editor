using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common.TransformPath.PlayerProbe
{
    /// <summary>
    /// Builds the measurement Player for Tools/run-transformpath-probe.sh. The
    /// probe scene is generated in the disposable test worktree, so it never
    /// enters the project or a normal build.
    /// </summary>
    internal static class TransformPathPlayerProbeBuild
    {
        private const string ScenePath = "Assets/TransformPathPlayerProbe.unity";
        private const string SampleScenePath = "Assets/_TransformPath/Scene/TransformPathSample.unity";
        private const string OutputArgument = "-transformPathProbeApp";

        internal static void Build()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int outputIndex = Array.IndexOf(arguments, OutputArgument);
            if (outputIndex < 0 || outputIndex + 1 >= arguments.Length)
                throw new InvalidOperationException($"{OutputArgument} is required.");

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            new GameObject("Probe").AddComponent<TransformPathPlayerProbe>();
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException($"Could not save {ScenePath}.");

            EditorUserBuildSettings.SetPlatformSettings("Standalone", "OSXUniversal", "Architecture", "ARM64");
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath, SampleScenePath },
                locationPathName = arguments[outputIndex + 1],
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
                // The probe assembly stays out of every other Player.
                extraScriptingDefines = new[] { "TRANSFORMPATH_PLAYER_PROBE" },
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Probe Player build {report.summary.result}: {report.summary.totalErrors} errors");
        }
    }
}
