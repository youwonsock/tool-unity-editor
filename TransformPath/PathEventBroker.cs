namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// 씬 전역 기본 경로 이벤트 수신기. 프로젝트별 <see cref="IPathEventSink"/> 구현을 등록합니다.
    /// </summary>
    public static class PathEventBroker
    {
        public static IPathEventSink Sink { get; set; }
        public static IPathEventReceiver Receiver { get; set; }
    }
}
