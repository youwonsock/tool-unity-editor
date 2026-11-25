using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Common.FlowField.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// B3: a duplicated Manager keeps the original's asset reference, and its
    /// bake goes into an asset of its own, never the original's. Undo of that
    /// bake points the duplicate back at the original's asset. A duplicate
    /// baked before its scene is saved keeps its identity through the save,
    /// so the owner its asset records still matches. Runs on a Temp copy of
    /// the sample scene.
    /// </summary>
    [TestFixture, Category("EditorBake")]
    internal sealed class DuplicateBakeTests
    {
        private readonly FlowFieldTestLog _log = new FlowFieldTestLog();

        [SetUp]
        public void Prepare()
        {
            FlowFieldBakeTestSupport.DeleteTempFolder();
            _log.Start();
            _log.Expect(FlowFieldBakeTestSupport.BakeFinishedPattern);
        }

        [TearDown]
        public void CleanUp()
        {
            FlowFieldBakeTestSupport.Clean();
            _log.StopAndAssertClean();
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator DuplicatedManagerBakesIntoItsOwnAsset()
        {
            FlowFieldManager a = FlowFieldBakeTestSupport.OpenBakeCopy(FlowFieldPlayScene.SampleScene);
            yield return FlowFieldBakeTestSupport.Bake(a);
            Assert.That(EditorSceneManager.SaveScene(a.gameObject.scene), Is.True);
            FlowFieldBakeAsset aAsset = FlowFieldBakeTestSupport.Assigned(a);
            FlowFieldBakeTestSupport.AssertInTemp(aAsset, "A's bake");
            Assert.That(aAsset.OwnerId, Is.EqualTo(FlowFieldBakeAssetUtility.OwnerId(a)), "A's bake must record A as its owner.");
            var aBaked = new BakedAsset(aAsset);

            var bHolder = new FlowFieldManager[1];
            yield return Duplicate(a, "FlowFieldManagerB", bHolder);
            FlowFieldManager b = bHolder[0];
            Assert.That(EditorSceneManager.SaveScene(b.gameObject.scene), Is.True);
            Assert.That(FlowFieldBakeTestSupport.Assigned(b), Is.SameAs(aAsset), "A duplicate starts with the original's asset.");
            ExpectRedirect(aBaked.Path);
            // The Undo point just before B's bake; the bake records its own group after it.
            Undo.IncrementCurrentGroup();
            int beforeBake = Undo.GetCurrentGroup();
            yield return FlowFieldBakeTestSupport.Bake(b);
            FlowFieldBakeAsset bAsset = FlowFieldBakeTestSupport.Assigned(b);
            FlowFieldBakeTestSupport.AssertInTemp(bAsset, "B's bake");
            Assert.That(bAsset, Is.Not.SameAs(aAsset), "B baked into A's asset.");
            Assert.That(FlowFieldBakeTestSupport.PathOf(bAsset), Is.Not.EqualTo(aBaked.Path));
            Assert.That(bAsset.OwnerId, Is.EqualTo(FlowFieldBakeAssetUtility.OwnerId(b)), "B's bake must record B as its owner.");
            aBaked.AssertKept(a, "after B's bake");

            yield return null;
            // Undo.PerformUndo needs an Editor event to act on; reverting the
            // Undo history down to the point before the bake does what Ctrl+Z
            // of the bake's step does.
            Undo.RevertAllDownToGroup(beforeBake);
            Assert.That(FlowFieldBakeTestSupport.Assigned(b), Is.SameAs(aAsset), "Undoing B's bake must point B back at A's asset.");
            aBaked.AssertKept(a, "after the Undo");

            var cHolder = new FlowFieldManager[1];
            yield return Duplicate(a, "FlowFieldManagerC", cHolder);
            FlowFieldManager c = cHolder[0];
            Assert.That(c.gameObject.scene.isDirty, Is.True, "C must bake before its scene is saved.");
            ExpectRedirect(aBaked.Path);
            yield return FlowFieldBakeTestSupport.Bake(c);
            FlowFieldBakeAsset cAsset = FlowFieldBakeTestSupport.Assigned(c);
            FlowFieldBakeTestSupport.AssertInTemp(cAsset, "C's bake");
            Assert.That(cAsset, Is.Not.SameAs(aAsset), "C baked into A's asset.");
            aBaked.AssertKept(a, "after C's bake");

            string idBeforeSave = GlobalObjectId.GetGlobalObjectIdSlow(c).ToString();
            Assert.That(EditorSceneManager.SaveScene(c.gameObject.scene), Is.True);
            string idAfterSave = GlobalObjectId.GetGlobalObjectIdSlow(c).ToString();
            Assert.That(idAfterSave, Is.EqualTo(idBeforeSave), "Saving the scene changed C's identity.");
            Assert.That(cAsset.OwnerId, Is.EqualTo(idAfterSave), "C's asset owner must still match C after the save.");
        }

        // Ctrl+D on the Manager's GameObject. The copy is found by the new
        // Manager it adds, not by the selection, which can lag behind while the
        // Editor is not focused; a few frames are allowed for it to appear.
        private static IEnumerator Duplicate(FlowFieldManager original, string name, FlowFieldManager[] result)
        {
            var before = new System.Collections.Generic.HashSet<FlowFieldManager>(
                Object.FindObjectsByType<FlowFieldManager>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            Selection.activeGameObject = original.gameObject;
            Unsupported.DuplicateGameObjectsUsingPasteboard();
            FlowFieldManager copy = null;
            for (int frame = 0; frame < 30 && copy == null; frame++)
            {
                foreach (FlowFieldManager manager in Object.FindObjectsByType<FlowFieldManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (!before.Contains(manager))
                        copy = manager;
                if (copy == null)
                    yield return null;
            }
            Assert.That(copy != null, Is.True, "Duplicating the Manager made no copy.");
            // Nothing stays selected, so an Inspector or Scene View drawing the
            // copy adds no Undo step between its bake and the test's Undo.
            Selection.activeObject = null;
            copy.gameObject.name = name;
            result[0] = copy;
        }

        // A bake whose assigned asset another Manager baked says where it bakes instead.
        private void ExpectRedirect(string originalPath)
            => FlowFieldBakeTestSupport.ExpectFlowFieldLog(_log, LogType.Log,
                "^" + Regex.Escape("[FlowField] Surface2D Bake: '" + originalPath + "' was not baked by this Manager, so this Manager now bakes into '"));

        /// <summary>The original's asset as its own bake left it.</summary>
        private sealed class BakedAsset
        {
            private readonly FlowFieldBakeAsset _asset;
            private readonly int _revision;
            private readonly byte[] _bytes;

            internal string Path { get; }

            internal BakedAsset(FlowFieldBakeAsset asset)
            {
                _asset = asset;
                Path = FlowFieldBakeTestSupport.PathOf(asset);
                _revision = asset.Revision;
                _bytes = FlowFieldBakeTestSupport.FileBytes(asset);
            }

            internal void AssertKept(FlowFieldManager owner, string what)
            {
                Assert.That(FlowFieldBakeTestSupport.Assigned(owner), Is.SameAs(_asset), what + ": A lost its asset.");
                Assert.That(FlowFieldBakeTestSupport.PathOf(_asset), Is.EqualTo(Path), what);
                Assert.That(_asset.Revision, Is.EqualTo(_revision), what + ": A's asset was baked again.");
                Assert.That(FlowFieldBakeTestSupport.FileBytes(_asset), Is.EqualTo(_bytes), what + ": A's asset file changed.");
            }
        }
    }
}
