#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.Tests
{
    /// <summary>EditMode runner tests that remain in Play Mode across real script recompilation.</summary>
    [Serializable]
    public sealed class PhysicsDemoReloadLifecycleTests
    {
        private const string ScenePath = "Assets/_OptimizeTool/Scene/OptimizeToolSample.unity";
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly string DomainIdentity = Guid.NewGuid().ToString("N");

        // Unity's runner restores the coroutine program counter, not its local variables.
        [SerializeField] private string _runKey, _beforeReload;
        [SerializeField] private int _cycle, _anchorId;
        [SerializeField] private bool _oldOptionsEnabled;
        [SerializeField] private EnterPlayModeOptions _oldOptions;

        private static Samples.OptimizeToolOverviewController Overview =>
            Object.FindFirstObjectByType<Samples.OptimizeToolOverviewController>();
        private static Samples.OptimizeToolPhysicsDemo Demo =>
            Object.FindFirstObjectByType<Samples.OptimizeToolPhysicsDemo>(FindObjectsInactive.Include);
        private Transform Anchor => (Transform)EditorUtility.InstanceIDToObject(_anchorId);
        private int PreviewCount => Anchor.GetComponentsInChildren<Animation>(true).Length;

        [SetUp]
        public void SetUp()
        {
            // The EditMode runner invokes SetUp again when it restores a test after reload.
            if (!string.IsNullOrEmpty(_runKey)) return;
            _runKey = "PhysicsReloadTests." + Guid.NewGuid().ToString("N");
            _cycle = 0;
            _oldOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _oldOptions = EditorSettings.enterPlayModeOptions;
            SessionState.SetBool(_runKey + ".optionsEnabled", _oldOptionsEnabled);
            SessionState.SetInt(_runKey + ".options", (int)_oldOptions);
            EditorSettings.enterPlayModeOptionsEnabled = false;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (EditorApplication.isPlaying) yield return new ExitPlayMode();
            EditorSettings.enterPlayModeOptions = _oldOptions;
            EditorSettings.enterPlayModeOptionsEnabled = _oldOptionsEnabled;
            // Check against an independent session snapshot, not just fields restored by the runner.
            Assert.AreEqual(SessionState.GetBool(_runKey + ".optionsEnabled", false), EditorSettings.enterPlayModeOptionsEnabled);
            Assert.AreEqual(SessionState.GetInt(_runKey + ".options", -1), (int)EditorSettings.enterPlayModeOptions);
            TestContext.WriteLine($"OPTIONS_RESTORED enabled={_oldOptionsEnabled} flags={_oldOptions}");
            SessionState.EraseBool(_runKey);
            SessionState.EraseBool(_runKey + ".optionsEnabled");
            SessionState.EraseInt(_runKey + ".options");
            _runKey = null;
        }

        [UnityTest, Timeout(180000)]
        public IEnumerator DisabledResetAndRepeatedRecompilationDoNotLeavePreviewsOrDuplicateListeners()
        {
            yield return new EnterPlayMode();
            yield return SceneManager.LoadSceneAsync(ScenePath);
            yield return null;
            ConfigureScene();
            Demo.enabled = false;

            for (; _cycle < 3; _cycle++)
            {
                for (int reset = 0; reset < 4; reset++)
                {
                    ButtonNamed("RESET").onClick.Invoke();
                    yield return null; // Destroy is deferred until the end of the frame.
                    Assert.AreEqual(1, PreviewCount, "Repeated RESET must replace the previous preview.");
                    Assert.AreEqual(1, ReloadCallbacks().Count(c => ReferenceEquals(c.Target, Demo)));
                    Assert.AreEqual(1, ButtonListenerCount(ButtonNamed("RESET")));
                }
                _beforeReload = DomainIdentity;
                yield return new RecompileWhilePlaying(_runKey);
                AssertRecompiled();
                yield return null;
                Assert.IsFalse(Demo.enabled);
                Assert.AreEqual(0, PreviewCount, "A disabled owner must clean its preview before domain reload.");
                Assert.IsNull(Demo.Playback);
                Assert.AreEqual(0, ReloadCallbacks().Count(c => c.Target is Samples.OptimizeToolPhysicsDemo));
                TestContext.WriteLine($"RELOAD cycle={_cycle + 1} previews={PreviewCount} demo_callbacks=0 panel_count=1");
                Assert.AreEqual(1, Overview.GetComponentsInChildren<RectTransform>(true)
                    .Count(t => t.name == "OptimizeTool Compact Panel"));
            }

            Demo.enabled = true;
            for (int reset = 0; reset < 3; reset++)
            {
                ButtonNamed("RESET").onClick.Invoke();
                yield return null;
                Assert.AreEqual(1, PreviewCount);
                Assert.AreEqual(1, ReloadCallbacks().Count(c => ReferenceEquals(c.Target, Demo)));
            }
            ButtonNamed("COMPARE").onClick.Invoke();
            double deadline = EditorApplication.timeSinceStartup + 15d;
            while (Demo.State == Samples.OptimizeToolPhysicsDemo.DemoState.Comparing
                && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.AreEqual(Samples.OptimizeToolPhysicsDemo.DemoState.Finished, Demo.State, Demo.LastError);
            Assert.AreEqual(1, PreviewCount);
            Assert.AreEqual(1, ReloadCallbacks().Count(c => ReferenceEquals(c.Target, Demo)));
            TestContext.WriteLine("REPLAY previews=1 demo_callbacks=1");
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }

        [UnityTest, Timeout(120000)]
        public IEnumerator DestroyedDisabledOwnerLeavesNoPreviewOrListenersAfterRecompilation()
        {
            yield return new EnterPlayMode();
            yield return SceneManager.LoadSceneAsync(ScenePath);
            yield return null;
            ConfigureScene();
            Demo.enabled = false;
            for (int reset = 0; reset < 3; reset++)
            {
                ButtonNamed("RESET").onClick.Invoke();
                yield return null;
                Assert.AreEqual(1, PreviewCount);
                Assert.AreEqual(1, ReloadCallbacks().Count(c => ReferenceEquals(c.Target, Demo)));
            }
            Object.Destroy(Demo);
            yield return null;
            yield return null;
            AssertDestroyedOwner();
            _beforeReload = DomainIdentity;
            yield return new RecompileWhilePlaying(_runKey);
            AssertRecompiled();
            yield return null;
            AssertDestroyedOwner();
            Overview.SelectFeature(Samples.OptimizeToolSampleModes.Original);
            Overview.SelectFeature(Samples.OptimizeToolSampleModes.Physics);
            yield return null;
            AssertDestroyedOwner();
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }

        private void ConfigureScene()
        {
            Time.timeScale = 1f;
            Assert.NotNull(Demo.SavedPrefab, "The sample's saved recording is required for lifecycle tests.");
            _anchorId = ((Transform)typeof(Samples.OptimizeToolPhysicsDemo)
                .GetField("_replayAnchor", PrivateInstance).GetValue(Demo)).GetInstanceID();
            Overview.SelectFeature(Samples.OptimizeToolSampleModes.Physics);
            Assert.AreEqual(1, PreviewCount);
            Assert.AreEqual(1, ReloadCallbacks().Count(c => ReferenceEquals(c.Target, Demo)));
        }

        private void AssertRecompiled()
        {
            Assert.IsNotEmpty(_beforeReload, "The runner must restore the serialized test continuation.");
            Assert.AreNotEqual(_beforeReload, DomainIdentity, "The managed domain must actually change.");
            Assert.IsTrue(EditorApplication.isPlaying, "Recompilation must not exit Play Mode.");
            Assert.IsTrue(SessionState.GetBool(_runKey, false), "The compilation pipeline must have started.");
            Assert.IsFalse(EditorUtility.scriptCompilationFailed);
        }

        private void AssertDestroyedOwner()
        {
            Assert.IsNull(Demo);
            Assert.AreEqual(0, PreviewCount);
            // Include destroyed Unity targets: using Unity's overloaded == would hide leaked delegates.
            Assert.AreEqual(0, ReloadCallbacks().Count(c => c.Target is Samples.OptimizeToolPhysicsDemo));
            TestContext.WriteLine($"DESTROY previews={PreviewCount} demo_callbacks=0");
            foreach (string name in new[] { "RECORD", "COMPARE", "RESET" })
            {
                Button button = ButtonNamed(name);
                Assert.IsFalse(button.interactable);
                var ui = Overview.GetComponentInChildren<Samples.OptimizeToolShowcaseUI>(true);
                var bindings = (IDictionary)typeof(Samples.OptimizeToolShowcaseUI)
                    .GetField("_buttonActions", PrivateInstance).GetValue(ui);
                Assert.IsFalse(bindings.Contains(button), "The dead owner's button callback must be detached.");
                Assert.AreEqual(0, ButtonListenerCount(button), "The UnityEvent itself must no longer retain the callback.");
            }
        }

        private static Button ButtonNamed(string name) => Overview.GetComponentsInChildren<Button>(true)
            .Single(b => b.name == name + " Button");

        private static int ButtonListenerCount(Button button)
        {
            MethodInfo prepare = typeof(UnityEventBase).GetMethod("PrepareInvoke", PrivateInstance);
            Assert.NotNull(prepare, "Update the UnityEvent subscriber probe for this Unity version.");
            return ((ICollection)prepare.Invoke(button.onClick, null)).Count;
        }

        private static Delegate[] ReloadCallbacks()
        {
            // Unity has no public subscriber inspection API. Keep the version-specific read adapter in tests.
            FieldInfo field = typeof(AssemblyReloadEvents).GetField("m_BeforeAssemblyReloadEvent",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field, "Update the reload subscriber probe for this Unity version.");
            object tracker = field.GetValue(null);
            var iterator = (IEnumerator)tracker.GetType().GetMethod("GetEnumerator").Invoke(tracker, null);
            var callbacks = new List<Delegate>();
            while (iterator.MoveNext()) callbacks.Add((Delegate)iterator.Current);
            return callbacks.ToArray();
        }

        private sealed class RecompileWhilePlaying : IEditModeTestYieldInstruction
        {
            private readonly string _key;
            public bool ExpectDomainReload => true;
            public bool ExpectedPlaymodeState => true;
            public RecompileWhilePlaying(string key) => _key = key;

            public IEnumerator Perform()
            {
                yield return null; // Let the EditMode runner save its continuation first.
                SessionState.SetBool(_key, false);
                Action<object> started = _ => SessionState.SetBool(_key, true);
                CompilationPipeline.compilationStarted += started;
                try
                {
                    // A normal request can reuse cached assemblies and only reload the domain.
                    CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
                    EditorApplication.UnlockReloadAssemblies();
                    double deadline = EditorApplication.timeSinceStartup + 60d;
                    while (EditorApplication.timeSinceStartup < deadline)
                    {
                        if (!EditorApplication.isCompiling && EditorUtility.scriptCompilationFailed)
                            Assert.Fail("Script compilation failed.");
                        yield return null;
                    }
                    Assert.Fail("The requested compilation did not reload the domain within 60 seconds.");
                }
                finally { CompilationPipeline.compilationStarted -= started; }
            }
        }
    }
}
#endif
