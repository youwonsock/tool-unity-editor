namespace Supercent.Common.TransformPath
{
    public interface IPathEventSink
    {
        void SendPathEvent(string eventName, PathFollower follower);
    }
}
