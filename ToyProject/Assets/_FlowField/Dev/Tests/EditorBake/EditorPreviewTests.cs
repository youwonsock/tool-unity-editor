using System;
using System.Collections;
using NUnit.Framework;
using Common.FlowField.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// G3: the Edit Mode preview of a Manager that a Modifier left is solved
    /// again. After a Modifier Volume is reassigned to another Manager, the
    /// first Manager's preview must equal a fresh solve without it. Opens the
    /// sample scene and never saves it.
    /// </summary>
    [TestFixture, Category("EditorBake")]
    internal sealed class EditorPreviewTests
    {
        private const string ModifierName = "Speed Modifier Volume";
        private const string ModifierManagerPath = "_flowFieldManager";
        private const ulong OffsetBasis = 1469598103934665603UL;
        private const ulong Prime = 1099511628211UL;

        private readonly FlowFieldTestLog _log = new FlowFieldTestLog();

        [SetUp]
        public void Prepare() => _log.Start();

        [TearDown]
        public void CleanUp()
        {
            FlowFieldEditorPreview.DisposeAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _log.StopAndAssertClean();
        }

        [UnityTest, Timeout(120000)]
        public IEnumerator ReassignedModifierSolvesTheOldPreviewAgain()
        {
            EditorSceneManager.OpenScene(FlowFieldPlayScene.SampleScene, OpenSceneMode.Single);
            FlowFieldManager a = FlowFieldBakeTestSupport.SingleManager();
            FlowFieldVectorModifierVolume volume = FindModifier(a);

            var bObject = new GameObject("FlowFieldTestManagerB");
            bObject.transform.position = a.transform.position + new Vector3(0f, 0f, 500f);
            FlowFieldManager b = bObject.AddComponent<FlowFieldManager>();
            EditorUtility.CopySerialized(a, b);

            ViewHash(a);
            yield return KeepViewing(a, 0.5d);
            string before = ViewHash(a);

            var serialized = new SerializedObject(volume);
            serialized.FindProperty(ModifierManagerPath).objectReferenceValue = b;
            serialized.ApplyModifiedProperties();
            yield return KeepViewing(a, 2d);
            string after = ViewHash(a);

            FlowFieldEditorPreview.DisposeAll();
            string fresh = ViewHash(a);
            Assert.That(fresh, Is.Not.EqualTo(before), "The Modifier must change A's preview, or this test checks nothing.");
            Assert.That(after, Is.EqualTo(fresh), "A's preview still shows the Modifier it no longer has.");
        }

        private static FlowFieldVectorModifierVolume FindModifier(FlowFieldManager manager)
        {
            foreach (FlowFieldVectorModifierVolume candidate in UnityEngine.Object.FindObjectsByType<FlowFieldVectorModifierVolume>(FindObjectsSortMode.None))
            {
                if (candidate.isActiveAndEnabled && candidate.FlowFieldManager == manager && candidate.name == ModifierName)
                    return candidate;
            }
            Assert.Fail("The sample has no enabled '" + ModifierName + "' on its Manager.");
            return null;
        }

        // The Scene View asks for the preview each repaint, which keeps it solved.
        private static IEnumerator KeepViewing(FlowFieldManager manager, double seconds)
        {
            double end = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < end)
            {
                FlowFieldEditorPreview.TryGetView(manager, out _, out _);
                yield return null;
            }
        }

        // FNV-1a over every cell the preview shows.
        private static string ViewHash(FlowFieldManager manager)
        {
            bool solved = FlowFieldEditorPreview.TryGetView(manager, out FlowFieldReadView view, out _);
            FlowFieldEditorPreview.TryGetFault(manager, out string fault);
            Assert.That(solved, Is.True, "The preview did not solve: " + fault);
            ulong hash = OffsetBasis;
            for (int i = 0; i < view.CellCount; i++)
            {
                view.TryGetCell(i, out bool blocked, out FlowFieldGoalFlags goalFlags,
                    out Vector3 direction, out float speed, out bool influenced);
                Mix(ref hash, blocked ? 1 : 0);
                Mix(ref hash, (int)goalFlags);
                Mix(ref hash, BitConverter.SingleToInt32Bits(direction.x));
                Mix(ref hash, BitConverter.SingleToInt32Bits(direction.y));
                Mix(ref hash, BitConverter.SingleToInt32Bits(direction.z));
                Mix(ref hash, BitConverter.SingleToInt32Bits(speed));
                Mix(ref hash, influenced ? 1 : 0);
            }
            return hash.ToString("X16") + " cells=" + view.CellCount;
        }

        private static void Mix(ref ulong hash, int value)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= Prime;
            }
        }
    }
}
