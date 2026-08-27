using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// 새 Path 계약에서 사용하는 이동 모드입니다.
    /// </summary>
    public enum EPathMoveType
    {
        TimeBased = 0,
        SpeedBased = 1,
    }

    /// <summary>
    /// PathFollower의 외부에 공개되는 상태입니다.
    /// </summary>
    public enum EPathFollowerState
    {
        Stopped = 0,
        Moving = 1,
        Paused = 2,
    }

    /// <summary>
    /// Provider 주입 이동에 필요한 설정입니다.
    /// </summary>
    public readonly struct PathMoveSettings
    {
        public EPathMoveType MoveType { get; }
        public float Value { get; }
        public AnimationCurve TimeCurve { get; }
        public bool Loop { get; }

        public PathMoveSettings(
            EPathMoveType moveType,
            float value,
            AnimationCurve timeCurve = null,
            bool loop = false)
        {
            MoveType = moveType;
            Value = value;
            TimeCurve = timeCurve;
            Loop = loop;
        }
    }

    /// <summary>
    /// Sequence가 외부에 제공하는 하나의 Path 구간입니다.
    /// </summary>
    public readonly struct PathSegmentDescriptor
    {
        public IPathProvider Provider { get; }
        public EPathMoveType MoveType { get; }
        public float Value { get; }
        public AnimationCurve TimeCurve { get; }

        public PathSegmentDescriptor(
            IPathProvider provider,
            EPathMoveType moveType,
            float value,
            AnimationCurve timeCurve)
        {
            Provider = provider;
            MoveType = moveType;
            Value = value;
            TimeCurve = timeCurve;
        }
    }

    /// <summary>
    /// Path를 Pull 방식으로 읽는 최소 계약입니다.
    /// </summary>
    public interface IPathProvider
    {
        bool IsReady { get; }
        int Revision { get; }
        float PathLength { get; }
        event Action PathChanged;

        bool TrySample(float normalizedTime, out Vector3 position);
        bool TrySampleDistance(float distance, out Vector3 position);
    }

    /// <summary>
    /// Path 재빌드 제어 계약입니다.
    /// </summary>
    public interface IPathController
    {
        bool TryRebuild(bool forceRebuild = false);
    }

    /// <summary>
    /// 여러 Path를 하나의 순서 있는 Provider로 제공하는 계약입니다.
    /// </summary>
    public interface IPathSequenceProvider : IPathProvider
    {
        int SegmentCount { get; }
        bool TryGetSegment(int index, out PathSegmentDescriptor descriptor);
    }

    /// <summary>
    /// Path 이벤트 목록을 제공하는 선택적 계약입니다.
    /// </summary>
    public interface IPathEventSource
    {
        IReadOnlyList<PathEventEntry> PathEvents { get; }
    }

    /// <summary>
    /// Follower 상태 조회와 이동 제어 계약입니다.
    /// </summary>
    public interface IPathFollower
    {
        IPathProvider CurrentProvider { get; }
        EPathFollowerState State { get; }
        int StateRevision { get; }
        bool IsMoving { get; }
        float NormalizedTime { get; }
        float GlobalNormalizedTime { get; }
        int CurrentSegmentIndex { get; }
        EPathMoveType CurrentMoveType { get; }
        float Speed { get; set; }
        float Duration { get; set; }

        event Action StateChanged;
        event Action SegmentChanged;
        event Action Completed;

        bool TryStartMove(IPathProvider provider, PathMoveSettings settings);
        void StopMove();
        void PauseMove(bool pauseAnimation = false);
        void ResumeMove(bool resumeAnimation = false);
        bool TrySeek(float normalizedTime);
    }

    /// <summary>
    /// Queue에서 관리되는 Agent의 읽기 계약입니다.
    /// </summary>
    public interface IQueuedPathAgent
    {
        IPathFollower PathFollower { get; }
        UnityEngine.Object UnityOwner { get; }
        bool IsMoving { get; }
        float GlobalNormalizedTime { get; }
        float ActorSpacing { get; }
        bool UseManagerSpacing { get; }
        bool EnableGradualSlowdown { get; }
        bool EnableOvertakeProtection { get; }
    }

    /// <summary>
    /// Path Queue의 범용 제어 계약입니다.
    /// </summary>
    public interface IPathQueue
    {
        int AgentCount { get; }
        void Register(IQueuedPathAgent agent);
        void Unregister(IQueuedPathAgent agent);
        bool ShouldBlock(IQueuedPathAgent agent);
        float GetDistanceToAhead(IQueuedPathAgent agent);
        float GetSpeedMultiplier(IQueuedPathAgent agent);
        float GetClampedNormalizedTime(IQueuedPathAgent agent, float targetNormalizedTime);
        void NotifySortNeeded();
    }

    /// <summary>
    /// 프로젝트 외부에서 Path 이벤트를 수신하기 위한 계약입니다.
    /// </summary>
    public interface IPathEventReceiver
    {
        void ReceivePathEvent(string eventName, IPathFollower follower);
    }

    public interface IPathEventSink
    {
        void SendPathEvent(string eventName, PathFollower follower);
    }

    /// <summary>
    /// 씬 전역 기본 경로 이벤트 수신기. 프로젝트별 이벤트 구현을 등록합니다.
    /// </summary>
    public static class PathEventBroker
    {
        public static IPathEventSink Sink { get; set; }
        public static IPathEventReceiver Receiver { get; set; }
    }

    internal static class PathTypeConversion
    {
        public static EPathMoveType ToPublic(PathFollower.EMoveType moveType)
            => (EPathMoveType)(int)moveType;

        public static PathFollower.EMoveType ToLegacy(EPathMoveType moveType)
            => (PathFollower.EMoveType)(int)moveType;
    }
}
