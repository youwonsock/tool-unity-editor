using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// PathData Init 호출을 한곳에서 null 검증과 함께 수행합니다.
    /// forceReinit·캐시 무효화 타이밍은 <see cref="PathData.Init"/>에 위임합니다.
    /// </summary>
    internal static class PathDataInitialization
    {
        public static void Initialize(PathData pathData, bool forceReinit = false)
        {
            if (pathData == null)
                return;

            pathData.Init(forceReinit);
        }

        public static void Initialize(PathData pathData, List<Vector3> controlPoints, bool forceReinit = false)
        {
            if (pathData == null)
                return;

            pathData.Init(controlPoints, forceReinit);
        }
    }
}
