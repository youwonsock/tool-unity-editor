using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Common.FlowField.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// G4: an asset of another type at the Manager's own bake path is never
    /// overwritten. The bake fails, saying to move or delete it, the asset
    /// stays and the Manager gets no bake asset. Runs on a Temp copy of the
    /// sample scene.
    /// </summary>
    [TestFixture, Category("EditorBake")]
    internal sealed class BakeTargetTests
    {
        private readonly FlowFieldTestLog _log = new FlowFieldTestLog();

        [SetUp]
        public void Prepare()
        {
            FlowFieldBakeTestSupport.DeleteTempFolder();
            _log.Start();
        }

        [TearDown]
        public void CleanUp()
        {
            FlowFieldBakeTestSupport.Clean();
            _log.StopAndAssertClean();
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator ForeignAssetAtTheBakePathIsKept()
        {
            FlowFieldManager manager = FlowFieldBakeTestSupport.OpenBakeCopy(FlowFieldPlayScene.SampleScene);
            string path = FlowFieldBakeAssetUtility.ResolveAssetPath(
                manager, null, FlowFieldBakeAssetUtility.OwnerId(manager), "SurfaceBake");
            Assert.That(path, Does.StartWith(FlowFieldBakeTestSupport.TempFolder + "/"));
            FlowFieldBakeAssetUtility.CreateBakeFolder(path);
            Shader shader = Shader.Find("Standard");
            if (shader == null)
                shader = Shader.Find("Hidden/InternalErrorShader");
            AssetDatabase.CreateAsset(new Material(shader), path);

            LogAssert.Expect(LogType.Exception, new Regex(Regex.Escape(
                $"Surface2D Bake target '{path}' already exists and is not a Surface2D Bake Asset. Move or delete it before baking.")));
            yield return FlowFieldBakeTestSupport.Bake(manager);

            Assert.That(AssetDatabase.LoadMainAssetAtPath(path), Is.InstanceOf<Material>(), "The bake replaced the foreign asset.");
            Assert.That(FlowFieldBakeTestSupport.Assigned(manager) == null, Is.True, "The failed bake must assign no asset.");
        }
    }
}
