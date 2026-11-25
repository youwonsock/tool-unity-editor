using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The Editor bake of each sample scene, run in Edit Mode without writing
    /// an asset, gives exactly the committed Bake Asset (apart from its name,
    /// revision and owner). A change to what a bake computes or saves fails here.
    /// </summary>
    [TestFixture, Category("Gate")]
    internal sealed class BakeExportTests
    {
        [TearDown]
        public void CloseScene() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        [Test]
        public void SampleSurfaceBake() => Check(FlowFieldPlayScene.SampleScene);

        [Test]
        public void VolumeBake() => Check(FlowFieldPlayScene.VolumeScene);

        private static void Check(string scene)
        {
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            FlowFieldManager manager = Object.FindObjectsByType<FlowFieldManager>(FindObjectsSortMode.None)[0];
            FlowFieldBakeAssets assets = manager.CaptureConfig().BakeAssets;
            ScriptableObject committed = manager.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? (ScriptableObject)assets.Volume
                : assets.Surface;
            Assert.That(committed, Is.Not.Null, "The sample Manager has no Bake Asset.");
            string exported = FlowFieldBakeExport.Export(manager);
            string expected = FlowFieldBakeExport.Describe(committed);
            Assert.That(exported == expected, Is.True,
                "The bake differs from the committed asset: export=" + FlowFieldBakeExport.Hash(exported)
                + " committed=" + FlowFieldBakeExport.Hash(expected));
        }
    }
}
