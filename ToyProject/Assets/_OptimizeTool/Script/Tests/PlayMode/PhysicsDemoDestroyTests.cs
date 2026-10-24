#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.Tests
{
    public sealed class PhysicsDemoDestroyTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private Samples.OptimizeToolOverviewController _overview;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return SceneManager.LoadSceneAsync("Assets/_OptimizeTool/Scene/OptimizeToolSample.unity");
            yield return null;
            _overview = Object.FindFirstObjectByType<Samples.OptimizeToolOverviewController>();
            _overview.SelectFeature(Samples.OptimizeToolSampleModes.Physics);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_overview != null) Object.Destroy(_overview.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator DestroyingComparingDemoDetachesPlaybackAndAllOwnedCallbacks() => CheckDestruction(false);

        [UnityTest]
        public IEnumerator DestroyingDisabledDemoDetachesPlaybackAndAllOwnedCallbacks() => CheckDestruction(true);

        private IEnumerator CheckDestruction(bool disableFirst)
        {
            var demo = _overview.PhysicsDemo;
            Assert.NotNull(demo.SavedPrefab);
            var anchor = (Transform)typeof(Samples.OptimizeToolPhysicsDemo)
                .GetField("_replayAnchor", PrivateInstance).GetValue(demo);
            for (int reset = 0; reset < 5; reset++)
            {
                demo.ResetDemo();
                yield return null;
                Assert.AreEqual(1, anchor.GetComponentsInChildren<Animation>(true).Length);
                Assert.AreEqual(1, ReloadCallbackCount(demo));
            }
            if (disableFirst) demo.enabled = false;
            else
            {
                demo.Compare();
                yield return new WaitForFixedUpdate();
                yield return null;
                Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Comparing, demo.State);
            }
            Assert.AreEqual(1, ReloadCallbackCount(demo));
            Object.Destroy(demo);
            yield return null;
            yield return null;
            yield return new WaitForFixedUpdate();
            Assert.IsTrue(demo == null);
            Assert.AreEqual(0, ReloadCallbackCount(demo), "Do not hide destroyed Unity targets with a Unity null check.");
            Assert.AreEqual(0, anchor.GetComponentsInChildren<Animation>(true).Length);

            for (int toggle = 0; toggle < 3; toggle++)
            {
                AssertPhysicsButtonsDetached();
                foreach (string label in new[] { "RECORD", "COMPARE", "RESET" }) ButtonNamed(label).onClick.Invoke();
                _overview.SelectFeature(Samples.OptimizeToolSampleModes.Original);
                _overview.SelectFeature(Samples.OptimizeToolSampleModes.Physics);
                var ui = _overview.GetComponentInChildren<Samples.OptimizeToolShowcaseUI>(true);
                ui.enabled = false;
                ui.enabled = true;
                yield return null;
                Assert.AreEqual(0, anchor.GetComponentsInChildren<Animation>(true).Length);
                Assert.AreEqual(0, ReloadCallbackCount(demo));
            }
            AssertPhysicsButtonsDetached();
            LogAssert.NoUnexpectedReceived();
        }

        private void AssertPhysicsButtonsDetached()
        {
            var ui = _overview.GetComponentInChildren<Samples.OptimizeToolShowcaseUI>(true);
            var bindings = (IDictionary)typeof(Samples.OptimizeToolShowcaseUI)
                .GetField("_buttonActions", PrivateInstance).GetValue(ui);
            foreach (string label in new[] { "RECORD", "COMPARE", "RESET" })
            {
                Button button = ButtonNamed(label);
                Assert.IsFalse(button.interactable);
                Assert.IsFalse(bindings.Contains(button));
                MethodInfo prepare = typeof(UnityEventBase).GetMethod("PrepareInvoke", PrivateInstance);
                Assert.NotNull(prepare, "Update the UnityEvent subscriber probe for this Unity version.");
                Assert.AreEqual(0, ((ICollection)prepare.Invoke(button.onClick, null)).Count);
            }
        }

        private Button ButtonNamed(string label) => _overview.GetComponentsInChildren<Button>(true)
            .Single(b => b.name == label + " Button");

        private static int ReloadCallbackCount(object owner)
        {
            FieldInfo field = typeof(AssemblyReloadEvents).GetField("m_BeforeAssemblyReloadEvent",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field, "Update the reload subscriber probe for this Unity version.");
            object tracker = field.GetValue(null);
            var iterator = (IEnumerator)tracker.GetType().GetMethod("GetEnumerator").Invoke(tracker, null);
            int count = 0;
            while (iterator.MoveNext())
                if (ReferenceEquals(((Delegate)iterator.Current).Target, owner)) count++;
            return count;
        }
    }
}
#endif
