using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Common.FlowField.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Shared steps of the Editor and bake tests. A bake that writes assets
    /// runs on a copy of a sample scene in <see cref="TempFolder"/>, whose
    /// Manager has no bake asset and so bakes into a new one next to the
    /// copy. <see cref="Clean"/> deletes the whole folder, so the sample
    /// scenes and their committed bake assets are never written.
    /// </summary>
    internal static class FlowFieldBakeTestSupport
    {
        internal const string TestsFolder = "Assets/_FlowField/Dev/Tests";
        internal const string TempFolderName = "Temp";
        internal const string TempFolder = TestsFolder + "/" + TempFolderName;
        internal const string BakeFinishedPattern = @"^\[FlowField\] (Surface2D|Volume3D) Bake finished: ";

        private const double BakeTimeoutSeconds = 180d;

        /// <summary>
        /// Copies the scene into the Temp folder and opens the copy. Its Manager
        /// is set to StaticBaked with no bake asset, and the copy is saved, as a
        /// bake needs a saved scene.
        /// </summary>
        internal static FlowFieldManager OpenBakeCopy(string scenePath)
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
                Assert.That(AssetDatabase.CreateFolder(TestsFolder, TempFolderName), Is.Not.Empty, "Could not create " + TempFolder);
            string copyPath = TempFolder + "/" + Path.GetFileName(scenePath);
            Assert.That(AssetDatabase.CopyAsset(scenePath, copyPath), Is.True, "Could not copy " + scenePath);
            Scene scene = EditorSceneManager.OpenScene(copyPath, OpenSceneMode.Single);
            FlowFieldManager manager = SingleManager();
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeMode).intValue = (int)FlowFieldBakeMode.StaticBaked;
            // The copy still points at the committed asset; it must bake into its own.
            serialized.FindProperty(FlowFieldManager.PropertyNames.SurfaceBakeData).objectReferenceValue = null;
            serialized.FindProperty(FlowFieldManager.PropertyNames.VolumeBakeData).objectReferenceValue = null;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True, "Could not save " + copyPath);
            return manager;
        }

        /// <summary>The one Manager of the open scene, inactive ones included.</summary>
        internal static FlowFieldManager SingleManager()
        {
            FlowFieldManager[] managers = UnityEngine.Object.FindObjectsByType<FlowFieldManager>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            Assert.That(managers.Length, Is.EqualTo(1), "Expected one FlowFieldManager in the open scene.");
            return managers[0];
        }

        /// <summary>Schedules the Manager's bake as the Inspector's Bake button does and waits for the queue to empty.</summary>
        internal static IEnumerator Bake(FlowFieldManager manager)
        {
            FlowFieldBakeEditor.ScheduleBake(manager);
            yield return WaitForBake();
        }

        /// <summary>
        /// The bake queue starts from EditorApplication.delayCall and the bake
        /// runs from EditorApplication.update; this yields until it has stopped.
        /// The Editor does not run delayCall while a test runs, so the wait
        /// runs the pending calls itself, as the Editor would between updates.
        /// </summary>
        internal static IEnumerator WaitForBake()
        {
            double deadline = EditorApplication.timeSinceStartup + BakeTimeoutSeconds;
            while (FlowFieldBakeEditor.IsBaking)
            {
                RunDelayCalls();
                if (EditorApplication.timeSinceStartup > deadline)
                {
                    string label = FlowFieldBakeEditor.ProgressLabel;
                    FlowFieldBakeEditor.CancelBake();
                    Assert.Fail("The bake did not finish: " + label);
                }
                yield return null;
            }
        }

        private static void RunDelayCalls()
        {
            EditorApplication.CallbackFunction pending = EditorApplication.delayCall;
            if (pending == null)
                return;
            EditorApplication.delayCall = null;
            pending();
        }

        /// <summary>The bake asset the Manager's Space Mode loads, as assigned now.</summary>
        internal static FlowFieldBakeAsset Assigned(FlowFieldManager manager)
        {
            FlowFieldManagerConfig config = manager.CaptureConfig();
            return manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? (FlowFieldBakeAsset)config.BakeAssets.Volume
                : config.BakeAssets.Surface;
        }

        internal static string PathOf(UnityEngine.Object asset) => AssetDatabase.GetAssetPath(asset);

        internal static byte[] FileBytes(UnityEngine.Object asset) => File.ReadAllBytes(PathOf(asset));

        /// <summary>A bake asset exists and lies in the Temp folder, never next to a sample scene.</summary>
        internal static void AssertInTemp(FlowFieldBakeAsset asset, string what)
        {
            Assert.That(asset != null, Is.True, what + ": no bake asset is assigned.");
            Assert.That(PathOf(asset), Does.StartWith(TempFolder + "/"), what);
        }

        /// <summary>Expects a "[FlowField" log line, both for the Test Framework and for the FlowField log guard.</summary>
        internal static void ExpectFlowFieldLog(FlowFieldTestLog log, LogType type, string pattern)
        {
            LogAssert.Expect(type, new Regex(pattern));
            log.Expect(pattern);
        }

        /// <summary>Deletes a Temp folder an interrupted run left behind.</summary>
        internal static void DeleteTempFolder()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.DeleteAsset(TempFolder);
        }

        /// <summary>Stops any bake, leaves the copied scene without saving and deletes the Temp folder.</summary>
        internal static void Clean()
        {
            FlowFieldBakeEditor.CancelBake();
            Selection.activeObject = null;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            DeleteTempFolder();
        }
    }
}
