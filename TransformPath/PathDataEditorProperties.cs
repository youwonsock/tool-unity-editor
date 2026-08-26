#if UNITY_EDITOR
namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// PathData gizmo/인스펙터 SerializedProperty 필드명 상수.
    /// 필드명 변경 시 이 클래스만 수정합니다.
    /// </summary>
    internal static class PathDataEditorProperties
    {
        public const string ShowPathInEditor = "_showPathInEditor";
        public const string PointColor = "_pointColor";
        public const string PathColor = "_pathColor";
        public const string SamplePointColor = "_samplePointColor";
        public const string EventPointColor = "_eventPointColor";
        public const string LineWidth = "_lineWidth";
        public const string PointSize = "_pointSize";
        public const string SamplePointSize = "_samplePointSize";
        public const string EventPointSize = "_eventPointSize";
    }
}
#endif
