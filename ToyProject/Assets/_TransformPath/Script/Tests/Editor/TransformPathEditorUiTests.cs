using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    [Category("EditorUI")]
    public sealed class TransformPathEditorUiTests
    {
        private Scene _previous;
        private Scene _scene;
        private GameObject _root;
        private Transform _a0, _a1, _b0, _b1, _third;
        private PathData _a, _b;
        private MultiPathData _route;
        private PathWaypointSyncRegistry _registry;
        private TransformPathInspectorProbe _window;

        [SetUp]
        public void SetUp()
        {
            if (Application.isBatchMode)
                Assert.Ignore("Editor UI tests require a graphical Unity Editor.");
            _previous = SceneManager.GetActiveScene();
            _scene = _previous;
            SceneManager.SetActiveScene(_scene);
            _root = new GameObject("TransformPath editor UI verification");
            _a0 = Point("A0", 0f); _a1 = Point("A1", 4f);
            _b0 = Point("B0", 8f); _b1 = Point("B1", 12f);
            _third = Point("Third", 18f);
            _a = Path("A", _a0, _a1); _b = Path("B", _b0, _b1);
            _route = _root.AddComponent<MultiPathData>();
            var route = new SerializedObject(_route);
            route.FindProperty("_autoLinkPathPoints").boolValue = false;
            var segments = route.FindProperty("_segments");
            segments.arraySize = 2;
            for (int i=0; i<2; i++)
            {
                var segment=segments.GetArrayElementAtIndex(i);
                segment.FindPropertyRelative("_providerObject").objectReferenceValue=i==0 ? _a : _b;
                segment.FindPropertyRelative("_movementSource").enumValueIndex=0;
            }
            route.ApplyModifiedPropertiesWithoutUndo();
            _route.Init(); _route.Rebuild();
            Assert.That(_route.IsReady, Is.True);
            _registry = _root.AddComponent<PathWaypointSyncRegistry>();
            var registry = new SerializedObject(_registry);
            registry.FindProperty("_syncInEditor").boolValue=false;
            var groups=registry.FindProperty("_groups"); groups.arraySize=1;
            var group=groups.GetArrayElementAtIndex(0);
            group.FindPropertyRelative("_displayName").stringValue="Shared authoring points";
            group.FindPropertyRelative("_enabled").boolValue=true;
            var members=group.FindPropertyRelative("_members");members.arraySize=3;
            members.GetArrayElementAtIndex(0).objectReferenceValue=_a0;
            members.GetArrayElementAtIndex(1).objectReferenceValue=_b1;
            members.GetArrayElementAtIndex(2).objectReferenceValue=_third;
            registry.ApplyModifiedPropertiesWithoutUndo();
            _window=ScriptableObject.CreateInstance<TransformPathInspectorProbe>();
            _window.titleContent=new GUIContent("TransformPath UI verification");
            _window.position=new Rect(40,40,680,950);
            _window.ShowUtility();
        }

        [TearDown]
        public void TearDown()
        {
            if (_window!=null) _window.Close();
            if (_root!=null)
            {
                foreach(var component in _root.GetComponentsInChildren<Component>(true))
                    if(component!=null) Undo.ClearUndo(component);
                Object.DestroyImmediate(_root);
            }
            if (_previous.IsValid() && _previous.isLoaded) SceneManager.SetActiveScene(_previous);
            if (_scene.IsValid() && _scene.isLoaded && _scene.handle!=_previous.handle) EditorSceneManager.CloseScene(_scene,true);
        }

        [UnityTest]
        public IEnumerator AutomaticJointLinkingFollowsEitherEndpointAndCanBeDisabled()
        {
            _window.Target(_route);
            yield return Settle();
            _window.ClickPosition(new Vector2(162,58));
            yield return Settle();
            Assert.That(new SerializedObject(_route).FindProperty("_autoLinkPathPoints").boolValue,Is.True);
            Same(_a1,_b0,"Initial alignment");
            Move(_a1,new Vector3(6,1,2)); yield return Settle();
            Same(_a1,_b0,"Move previous endpoint");
            Move(_b0,new Vector3(9,2,3)); yield return Settle();
            Same(_a1,_b0,"Move next start point");
            SetBool(_route,"_autoLinkPathPoints",false); yield return Settle();
            Move(_a1,new Vector3(2,0,0)); yield return Settle();
            Assert.That(Vector3.Distance(_a1.position,_b0.position),Is.GreaterThan(1f));
            Assert.That(_window.RenderError,Is.Null);
        }

        [UnityTest]
        public IEnumerator JointMoveUndoRedoRestoresBothEndpoints()
        {
            SetBool(_route,"_autoLinkPathPoints",true); yield return Settle();
            Vector3 before=_a1.position;
            Move(_a1,new Vector3(6,1,2)); yield return Settle();
            Same(_a1,_b0,"After move");
            Undo.PerformUndo(); yield return Settle();
            Assert.That(Vector3.Distance(_a1.position,before),Is.LessThan(0.001f),"Undo source");
            Same(_a1,_b0,"Undo synchronized endpoint");
            Undo.PerformRedo(); yield return Settle();
            Assert.That(Vector3.Distance(_a1.position,new Vector3(6,1,2)),Is.LessThan(0.001f),"Redo source");
            Same(_a1,_b0,"Redo synchronized endpoint");
        }

        [UnityTest]
        public IEnumerator MultiPathInspectorForceSyncAndRebuildButtonsExecute()
        {
            _window.Target(_route); yield return Settle();
            _window.ScrollToBottom(); yield return Settle();
            Assert.That(_window.Buttons.Count,Is.EqualTo(4),_window.Describe());
            _window.Capture("multipath-before.png");
            _window.Click(1); yield return Settle();
            Same(_a1,_b0,"Force Sync All Path Points button");
            _window.Click(3); yield return Settle();
            Assert.That(_route.IsReady,Is.True);
            Assert.That(_window.RenderError,Is.Null);
            _window.Capture("multipath-inspector.png");
        }

        [UnityTest]
        public IEnumerator RegistryInspectorValidateAndAlignButtonsExecute()
        {
            _window.Target(_registry); yield return Settle();
            Assert.That(_window.Buttons.Count,Is.EqualTo(3),_window.Describe());
            _window.Click(0); yield return Settle();
            _window.Click(1); yield return Settle();
            Same(_a0,_b1,"Align All Groups");Same(_a0,_third,"Align All Groups third member");
            Move(_b1,new Vector3(11,0,0));yield return Settle();
            _window.Click(2);yield return Settle();
            Same(_a0,_b1,"Align first group button");
            Assert.That(_window.RenderError,Is.Null);
            _window.Capture("waypoint-inspector.png");
        }

        [UnityTest]
        public IEnumerator WaypointGroupSyncFollowsAnyMemberAndRebuildsGeometry()
        {
            SetBool(_registry,"_syncInEditor",true); yield return Settle();
            Move(_a0,new Vector3(2,1,3));yield return Settle();
            Same(_a0,_b1,"First member drives group");Same(_a0,_third,"Third member follows");
            Assert.That(Vector3.Distance(_a.Sample(0),_a0.position),Is.LessThan(0.001f));
            Assert.That(Vector3.Distance(_b.Sample(1),_b1.position),Is.LessThan(0.001f));
            Move(_third,new Vector3(3,2,4));yield return Settle();
            Same(_a0,_third,"Third member drives first");Same(_b1,_third,"Third member drives second");
            SetBool(_registry,"_syncInEditor",false);yield return Settle();
            Move(_third,new Vector3(20,0,0));yield return Settle();
            Assert.That(Vector3.Distance(_a0.position,_third.position),Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator GroupMemberMoveUndoRedoRestoresAllMembers()
        {
            PathWaypointSyncEditorUtility.AlignAllGroups(_registry,0.000001f);
            SetBool(_registry,"_syncInEditor",true);yield return Settle();
            Vector3 before=_a0.position;
            Move(_third,new Vector3(3,2,4));yield return Settle();
            Same(_a0,_third,"After move");
            Undo.PerformUndo();yield return Settle();
            Assert.That(Vector3.Distance(_third.position,before),Is.LessThan(0.001f),"Undo moved member");
            Same(_a0,_third,"Undo first member");Same(_b1,_third,"Undo second member");
            Undo.PerformRedo();yield return Settle();
            Assert.That(Vector3.Distance(_third.position,new Vector3(3,2,4)),Is.LessThan(0.001f),"Redo moved member");
            Same(_a0,_third,"Redo first member");Same(_b1,_third,"Redo second member");
        }

        [UnityTest]
        public IEnumerator DisabledGroupDoesNotSynchronize()
        {
            SetBool(_registry,"_syncInEditor",true);
            var so=new SerializedObject(_registry);
            so.FindProperty("_groups").GetArrayElementAtIndex(0).FindPropertyRelative("_enabled").boolValue=false;
            so.ApplyModifiedProperties();yield return Settle();
            Move(_third,new Vector3(25,0,0));yield return Settle();
            Assert.That(Vector3.Distance(_a0.position,_third.position),Is.GreaterThan(1f));
        }

        [UnityTest]
        public IEnumerator InspectorGroupListEditsAreTrackedWithoutHierarchyChanges()
        {
            yield return Settle();
            var so=new SerializedObject(_registry);
            so.FindProperty("_groups").arraySize=0;so.ApplyModifiedProperties();yield return Settle();
            so.Update();var groups=so.FindProperty("_groups");groups.arraySize=1;
            var group=groups.GetArrayElementAtIndex(0);group.FindPropertyRelative("_enabled").boolValue=true;
            var members=group.FindPropertyRelative("_members");members.arraySize=2;
            members.GetArrayElementAtIndex(0).objectReferenceValue=_a0;
            members.GetArrayElementAtIndex(1).objectReferenceValue=_third;
            so.FindProperty("_syncInEditor").boolValue=true;so.ApplyModifiedProperties();yield return Settle();
            Move(_third,new Vector3(5,2,3));yield return Settle();
            Same(_a0,_third,"New group tracks Inspector list edit");
            Assert.That(Vector3.Distance(_b1.position,Vector3.right*12f),Is.LessThan(0.001f),"Removed member stays independent");

            // Replace references without changing group or member counts.
            Vector3 oldMemberPosition=_a0.position;
            so.Update();members=so.FindProperty("_groups").GetArrayElementAtIndex(0).FindPropertyRelative("_members");
            members.GetArrayElementAtIndex(0).objectReferenceValue=_a1;
            members.GetArrayElementAtIndex(1).objectReferenceValue=_b0;
            so.ApplyModifiedProperties();yield return Settle();
            Move(_b0,new Vector3(7,3,4));yield return Settle();
            Same(_a1,_b0,"Replacement members synchronize with unchanged list sizes");
            Assert.That(Vector3.Distance(_a0.position,oldMemberPosition),Is.LessThan(0.001f),"Previous member stays independent");
        }

        [UnityTest]
        public IEnumerator InspectorGroupAlignmentSupportsUndoRedo()
        {
            _window.Target(_registry);yield return Settle();
            Vector3 secondBefore=_b1.position,thirdBefore=_third.position;
            Undo.IncrementCurrentGroup();
            _window.Click(1);yield return Settle();
            Same(_a0,_third,"Align All Groups");
            Undo.PerformUndo();yield return Settle();
            Assert.That(Vector3.Distance(_b1.position,secondBefore),Is.LessThan(0.001f));
            Assert.That(Vector3.Distance(_third.position,thirdBefore),Is.LessThan(0.001f));
            Undo.PerformRedo();yield return Settle();
            Same(_a0,_third,"Redo Align All Groups");Same(_a0,_b1,"Redo second member");
        }

        private Transform Point(string name,float x)
        {
            var go=new GameObject(name);go.transform.SetParent(_root.transform);go.transform.position=Vector3.right*x;return go.transform;
        }
        private PathData Path(string name,params Transform[] points)
        {
            var go=new GameObject(name);go.transform.SetParent(_root.transform);
            var path=go.AddComponent<PathData>();path.ConfigureMovementSettings(PathMovementSettings.Time(5));
            var so=new SerializedObject(path);var list=so.FindProperty("_pathPoints");list.arraySize=points.Length;
            for(int i=0;i<points.Length;i++)list.GetArrayElementAtIndex(i).objectReferenceValue=points[i];
            so.ApplyModifiedPropertiesWithoutUndo();path.Init();path.Rebuild();Assert.That(path.IsReady,Is.True);return path;
        }
        private static void Same(Transform a,Transform b,string message)
        { Assert.That(Vector3.Distance(a.position,b.position),Is.LessThan(0.001f),message); }
        private static void SetBool(Object target,string field,bool value)
        { var so=new SerializedObject(target);so.FindProperty(field).boolValue=value;so.ApplyModifiedProperties(); }
        private static void Move(Transform target,Vector3 position)
        {
            Undo.IncrementCurrentGroup();Undo.SetCurrentGroupName("Move verification point");
            var so=new SerializedObject(target);so.FindProperty("m_LocalPosition").vector3Value=position;
            so.ApplyModifiedProperties();Undo.FlushUndoRecordObjects();
        }
        private static IEnumerator Settle()
        { double until=EditorApplication.timeSinceStartup+0.25;while(EditorApplication.timeSinceStartup<until)yield return null; }
    }

    internal sealed class TransformPathInspectorProbe : EditorWindow
    {
        private Editor _editor;
        private Vector2 _scroll;
        public readonly List<Rect> Buttons=new List<Rect>();
        public string RenderError {get;private set;}
        public void Target(Object target)
        {
            if(_editor!=null)DestroyImmediate(_editor);
            _editor=Editor.CreateEditor(target);
            _scroll=Vector2.zero;
            var groups=_editor.serializedObject.FindProperty("_groups");
            if(groups!=null)
            {
                groups.isExpanded=true;
                for(int i=0;i<groups.arraySize;i++)
                {
                    var group=groups.GetArrayElementAtIndex(i);group.isExpanded=true;
                    group.FindPropertyRelative("_members").isExpanded=true;
                }
            }
            Repaint();Focus();
        }
        private void OnGUI()
        {
            if(_editor==null)return;
            try
            {
                _scroll=EditorGUILayout.BeginScrollView(_scroll);
                _editor.OnInspectorGUI();
                if(Event.current.type==EventType.Repaint)
                {
                    Buttons.Clear();
                    var flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
                    var property=typeof(GUILayoutUtility).GetProperty("current",flags);
                    object cache=property!=null?property.GetValue(null):typeof(GUILayoutUtility).GetField("current",flags).GetValue(null);
                    object group=cache.GetType().GetField("topLevel",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(cache);
                    Collect(group);
                }
                EditorGUILayout.EndScrollView();
            }
            catch(Exception error){RenderError=error.ToString();throw;}
        }
        private void Collect(object entry)
        {
            var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
            var type=entry.GetType();
            var style=(GUIStyle)type.GetProperty("style",flags).GetValue(entry);
            if(style!=null && style.name=="button")
            {
                Rect rect=(Rect)type.GetField("rect",flags).GetValue(entry);
                rect.position=GUIUtility.GUIToScreenPoint(rect.position)-position.position;
                Buttons.Add(rect);
            }
            var children=type.GetField("entries",flags);
            if(children!=null)foreach(var child in (IEnumerable)children.GetValue(entry))Collect(child);
        }
        public void Click(int index)
        {
            ClickPosition(Buttons[index].center);
        }
        public void ScrollToBottom(){_scroll.y=10000f;Repaint();}
        public void ClickPosition(Vector2 position)
        {
            SendEvent(new Event{type=EventType.MouseDown,mousePosition=position,button=0});
            SendEvent(new Event{type=EventType.MouseUp,mousePosition=position,button=0});
        }
        public string Describe(){return string.Join(";",Buttons.ConvertAll(r=>r.ToString()));}
        public void Capture(string name)
        {
            string folder=Environment.GetEnvironmentVariable("TRANSFORMPATH_UI_OUTPUT");
            if(string.IsNullOrEmpty(folder))return;
            var texture=new Texture2D((int)position.width,(int)position.height,TextureFormat.RGB24,false);
            texture.SetPixels(UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(position.position,(int)position.width,(int)position.height));
            texture.Apply();File.WriteAllBytes(System.IO.Path.Combine(folder,name),texture.EncodeToPNG());DestroyImmediate(texture);
        }
        private void OnDisable(){if(_editor!=null)DestroyImmediate(_editor);}
    }
}
