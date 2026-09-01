using System;

namespace Common.TransformPath
{
    public partial class PathData
    {
        /// <summary>
        /// 현재 경로의 곡선 생성 방식을 읽습니다.
        /// </summary>
        public ECurveType CurveType => _curveType;

        /// <summary>
        /// 현재 경로의 에디터 샘플링 방식을 읽습니다.
        /// </summary>
        public ESamplingType SamplingType => _samplingType;

        /// <summary>
        /// 곡선 생성 방식을 변경하고 경로를 stale 상태로 만듭니다.
        /// 변경 후 명시적으로 <see cref="Rebuild"/>를 호출해야 합니다.
        /// </summary>
        public void SetCurveType(ECurveType curveType)
        {
            ThrowIfFaulted();
            if (!Enum.IsDefined(typeof(ECurveType), curveType))
                throw new ArgumentOutOfRangeException(nameof(curveType));
            if (_curveType == curveType)
                return;

            _curveType = curveType;
            MarkStale();
        }

        /// <summary>
        /// 에디터 경로 샘플링 방식을 변경하고 결과를 stale 상태로 만듭니다.
        /// 변경 후 명시적으로 <see cref="Rebuild"/>를 호출해야 합니다.
        /// </summary>
        public void SetSamplingType(ESamplingType samplingType)
        {
            ThrowIfFaulted();
            if (!Enum.IsDefined(typeof(ESamplingType), samplingType))
                throw new ArgumentOutOfRangeException(nameof(samplingType));
            if (_samplingType == samplingType)
                return;

            _samplingType = samplingType;
            MarkStale();
        }
    }
}
