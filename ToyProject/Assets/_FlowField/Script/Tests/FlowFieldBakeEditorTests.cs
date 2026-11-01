using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using Common.FlowField.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Drives the Editor bake entry point against a temporary scene and asset,
    /// so a failure can never touch the committed sample bakes.
    /// </summary>
    public sealed class FlowFieldBakeEditorTests
    {
        private const string TempFolder = "Assets/FlowFieldBakeEditorTemp";
        private const string CompleteLog = "Bake complete";
        private const string InputChangedLog = "input changed; existing asset was preserved";

        private string _assetPath;
        private SceneSetup[] _sceneSetup;

        [SetUp]
        public void SetUp()
        {
            _sceneSetup = EditorSceneManager.GetSceneManagerSetup();
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(TempFolder));
            _assetPath = TempFolder + "/Bake.asset";
            UnityEngine.SceneManagement.Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, TempFolder + "/Bake.unity"), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            bool canRestore = _sceneSetup.Length > 0 && Array.TrueForAll(_sceneSetup, setup => !string.IsNullOrEmpty(setup.path));
            if (canRestore) EditorSceneManager.RestoreSceneManagerSetup(_sceneSetup);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(TempFolder);
        }

        [UnityTest] public IEnumerator SurfaceRebakeChangesOnlyTheRevision() => CheckRebake(volume: false);
        [UnityTest] public IEnumerator VolumeRebakeChangesOnlyTheRevision() => CheckRebake(volume: true);
        [UnityTest] public IEnumerator SurfaceBakePreservesTheAssetWhenInputChanges() => CheckInputChange(volume: false);
        [UnityTest] public IEnumerator VolumeBakePreservesTheAssetWhenInputChanges() => CheckInputChange(volume: true);

        [UnityTest]
        public IEnumerator AFailedSaveKeepsThePreviousBake(
            [Values(false, true)] bool volume,
            [Values(true, false)] bool saveThrows)
        {
            FlowFieldManager manager = CreateManager(volume);
            FlowFieldSurfaceBakeEditor.BakeAndAssign(manager);
            yield return WaitForLog(CompleteLog);
            string baked = File.ReadAllText(_assetPath);
            int revision = BakeRevision(manager, volume);

            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_obstacleClearance").floatValue += 0.25f;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            // A write that fails quietly leaves the Asset dirty.
            FlowFieldBakeAssetUtility.SaveOverride = asset =>
            {
                if (saveThrows)
                    throw new IOException("Intentional save failure");
            };
            LogAssert.ignoreFailingMessages = true;
            try
            {
                FlowFieldSurfaceBakeEditor.BakeAndAssign(manager);
                yield return WaitForLog(saveThrows ? "Intentional save failure" : "Could not save");
            }
            finally
            {
                FlowFieldBakeAssetUtility.SaveOverride = null;
                LogAssert.ignoreFailingMessages = false;
            }

            Assert.That(File.ReadAllText(_assetPath), Is.EqualTo(baked));
            Assert.That(BakeRevision(manager, volume), Is.EqualTo(revision), "The in-memory Asset must be rolled back too.");
            Assert.That(volume ? (Object)manager.VolumeStaticBakeData : manager.StaticBakeData, Is.Not.Null);
        }

        private static int BakeRevision(FlowFieldManager manager, bool volume)
            => volume ? manager.VolumeStaticBakeData.Revision : manager.StaticBakeData.Revision;

        [UnityTest]
        public IEnumerator BakeRejectsAGoalWithoutAWalkableCellAndKeepsEarlierUndo([Values(false, true)] bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            string before = File.ReadAllText(_assetPath);
            var blocker = new GameObject("Bake blocker");
            blocker.layer = 28;
            blocker.AddComponent<BoxCollider>().size = Vector3.one * 100f;
            Physics.SyncTransforms();

            // An edit the person made before baking must survive the rollback of a failed bake.
            Undo.IncrementCurrentGroup();
            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_maxStepHeight").floatValue = 0.75f;
                settings.ApplyModifiedProperties();
            }
            LogAssert.ignoreFailingMessages = true;
            try
            {
                FlowFieldSurfaceBakeEditor.BakeAndAssign(manager);
                yield return WaitForLog("Cannot bake");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
            Assert.That(File.ReadAllText(_assetPath), Is.EqualTo(before));
            using (var settings = new SerializedObject(manager))
                Assert.That(settings.FindProperty("_maxStepHeight").floatValue, Is.EqualTo(0.75f));
        }

        [UnityTest]
        public IEnumerator SurfaceBakeValidationFollowsContentChanges()
        {
            FlowFieldManager manager = CreateManager(false);
            FlowFieldSurfaceBakeEditor.BakeAndAssign(manager);
            yield return WaitForLog(CompleteLog);
            FlowFieldStaticBakeData data = manager.StaticBakeData;
            Assert.That(data.HasValidData, Is.True);
            using (var settings = new SerializedObject(data))
            {
                float cellSize = settings.FindProperty("_cellSize").floatValue;
                settings.FindProperty("_cellSize").floatValue = -1f;
                settings.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(data.HasValidData, Is.False);
                settings.FindProperty("_cellSize").floatValue = cellSize;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(data.HasValidData, Is.True);
        }

        [UnityTest]
        public IEnumerator VolumeStaticBuildAllocatesNoSearchArrays()
        {
            FlowFieldManager manager = CreateManager(true);
            FlowFieldSurfaceBakeEditor.BakeAndAssign(manager);
            yield return WaitForLog(CompleteLog);
            manager.Init();
            for (int i = 0; i < 1000 && !manager.IsReady && !manager.IsFaulted; i++)
                manager.VolumeSession.Pump(1000d);
            Assert.That(manager.IsReady, Is.True, manager.LastError);
            foreach (string field in new[] { "_spareCosts", "_spareQueue", "_spareInfluence" })
            {
                object spare = typeof(FlowFieldVolumeSession)
                    .GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .GetValue(manager.VolumeSession);
                Assert.That(spare, Is.Null, field);
            }
            manager.Release();
        }

        [Test]
        public void BakeTargetMustBelongToTheManager()
        {
            var assigned = ScriptableObject.CreateInstance<FlowFieldStaticBakeData>();
            var other = ScriptableObject.CreateInstance<FlowFieldStaticBakeData>();
            try
            {
                Assert.DoesNotThrow(() => FlowFieldBakeAssetUtility.ValidateTargetOwnership(null, null, null, "new", "Test"));
                Assert.DoesNotThrow(() => FlowFieldBakeAssetUtility.ValidateTargetOwnership(assigned, assigned, assigned, "own", "Test"));
                Assert.Throws<InvalidOperationException>(() => FlowFieldBakeAssetUtility.ValidateTargetOwnership(null, null, other, "foreign", "Test"));
                Assert.Throws<InvalidOperationException>(() => FlowFieldBakeAssetUtility.ValidateTargetOwnership(assigned, other, other, "shared", "Test"));
            }
            finally
            {
                Object.DestroyImmediate(assigned);
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void BakeRequiresASavedScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            FlowFieldManager manager = new GameObject("Unsaved").AddComponent<FlowFieldManager>();
            Assert.Throws<InvalidOperationException>(() => FlowFieldBakeAssetUtility.ResolveStaticAssetPath(manager));
            Assert.Throws<InvalidOperationException>(() => FlowFieldBakeAssetUtility.ResolveVolumeAssetPath(manager));
        }

        private IEnumerator CheckRebake(bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            FlowFieldSurfaceBakeEditor.BakeAndAssign(manager);
            yield return WaitForLog(CompleteLog);
            string first = ReadAssetWithoutRevision();

            FlowFieldSurfaceBakeEditor.BakeAndAssign(manager);
            yield return WaitForLog(CompleteLog);
            Assert.That(ReadAssetWithoutRevision(), Is.EqualTo(first));
        }

        private IEnumerator CheckInputChange(bool volume)
        {
            FlowFieldManager manager = CreateManager(volume);
            FlowFieldSurfaceBakeEditor.BakeAndAssign(manager);
            yield return WaitForLog(CompleteLog);
            string baked = File.ReadAllText(_assetPath);

            FlowFieldSurfaceBakeEditor.BakeAndAssign(manager);
            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_obstacleClearance").floatValue += 0.25f;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            yield return WaitForLog(InputChangedLog);
            Assert.That(File.ReadAllText(_assetPath), Is.EqualTo(baked));
        }

        private FlowFieldManager CreateManager(bool volume)
        {
            var ground = new GameObject("Bake ground");
            ground.layer = FlowFieldRegressionFixture.Layer;
            ground.transform.position = new Vector3(1.5f, -.5f, 1.5f);
            ground.AddComponent<BoxCollider>().size = new Vector3(3f, 1f, 3f);
            var goal = new GameObject("Bake goal");
            goal.transform.position = volume ? Vector3.one * 2.5f : new Vector3(2.5f, 0f, 2.5f);

            var root = new GameObject("Bake manager");
            var manager = root.AddComponent<FlowFieldManager>();
            using (var settings = new SerializedObject(manager))
            {
                settings.FindProperty("_spaceMode").enumValueIndex = (int)(volume ? FlowFieldSpaceMode.Volume3D : FlowFieldSpaceMode.Surface2D);
                settings.FindProperty("_bakeMode").enumValueIndex = (int)FlowFieldBakeMode.StaticBaked;
                settings.FindProperty("_bakeBoundsLocal").boundsValue = volume
                    ? new Bounds(Vector3.one * 1.5f, Vector3.one * 3f)
                    : FlowFieldRegressionFixture.Settings.BakeBounds;
                settings.FindProperty("_cellSize").floatValue = 1f;
                settings.FindProperty("_groundBakeLayer").intValue = 1 << FlowFieldRegressionFixture.Layer;
                settings.FindProperty("_obstacleLayer").intValue = 1 << 28;
                settings.FindProperty("_obstacleCheckHeight").floatValue = 1f;
                settings.FindProperty("_obstacleCheckCenterOffset").floatValue = .5f;
                settings.FindProperty("_goalTransform").objectReferenceValue = goal.transform;
                settings.FindProperty("_showField").boolValue = false;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            Physics.SyncTransforms();

            // A pre-assigned asset keeps the bake out of the shared Settings folder.
            if (volume)
            {
                var asset = ScriptableObject.CreateInstance<FlowFieldVolumeBakeData>();
                AssetDatabase.CreateAsset(asset, _assetPath);
                manager.AssignVolumeBakeData(asset);
            }
            else
            {
                var asset = ScriptableObject.CreateInstance<FlowFieldStaticBakeData>();
                AssetDatabase.CreateAsset(asset, _assetPath);
                manager.AssignStaticBakeData(asset);
            }
            return manager;
        }

        private string ReadAssetWithoutRevision()
            => Regex.Replace(File.ReadAllText(_assetPath), @"^\s*_revision:.*$", string.Empty, RegexOptions.Multiline);

        private static IEnumerator WaitForLog(string text)
        {
            bool seen = false;
            void Listen(string message, string stackTrace, LogType type)
            {
                if (message.Contains(text))
                    seen = true;
            }

            Application.logMessageReceived += Listen;
            try
            {
                double deadline = EditorApplication.timeSinceStartup + 30d;
                while (!seen && EditorApplication.timeSinceStartup < deadline)
                    yield return null;
            }
            finally { Application.logMessageReceived -= Listen; }
            Assert.That(seen, Is.True, "No log containing: " + text);
        }
    }
}
