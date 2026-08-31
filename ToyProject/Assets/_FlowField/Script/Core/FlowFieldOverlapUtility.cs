using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// 특정 Collider 대상 Overlap 검증 NonAlloc 쿼리 헬퍼.
    /// 버퍼가 가득 차면 MAX_HITS까지 확장하며, 상한을 초과하면 계약 위반으로 예외를 던집니다.
    /// </summary>
    internal static class FlowFieldOverlapUtility
    {
        private const int INITIAL_OVERLAP_CAPACITY = 8;
        private const int MAX_OVERLAP_HITS = 256;

        public static bool OverlapsTarget(
            Vector3 center,
            Vector3 halfExtents,
            int layerMask,
            Collider target,
            QueryTriggerInteraction triggerInteraction,
            ref Collider[] overlapBuffer)
        {
            ResizeOverlapBuffer(ref overlapBuffer, INITIAL_OVERLAP_CAPACITY);
            while (true)
            {
                int count = Physics.OverlapBoxNonAlloc(
                    center,
                    halfExtents,
                    overlapBuffer,
                    Quaternion.identity,
                    layerMask,
                    triggerInteraction);

                for (int i = 0; i < count; i++)
                {
                    if (overlapBuffer[i] == target)
                        return true;
                }

                if (count < overlapBuffer.Length)
                    return false;

                if (overlapBuffer.Length >= MAX_OVERLAP_HITS)
                    throw new System.InvalidOperationException(
                        $"Target overlap count reached the configured capacity of {MAX_OVERLAP_HITS}.");

                ResizeOverlapBuffer(ref overlapBuffer, overlapBuffer.Length * 2);
            }
        }

        private static void ResizeOverlapBuffer(ref Collider[] overlapBuffer, int minimumCapacity)
        {
            int cappedCapacity = Mathf.Min(
                Mathf.Max(INITIAL_OVERLAP_CAPACITY, minimumCapacity),
                MAX_OVERLAP_HITS);
            if (overlapBuffer != null && overlapBuffer.Length >= cappedCapacity)
                return;

            overlapBuffer = new Collider[cappedCapacity];
        }
    }
}
