using UnityEngine;

namespace Common.FlowField
{
    internal static class FlowFieldBilinearSampler
    {
        /// <summary>
        /// 내부 샘플 계산 단계입니다. false는 호출자가 이미 검증한 입력과 작업 버퍼가
        /// 일치하지 않는 정상적인 계산 불가 결과이며, Manager 공개 API는 이를 예외로 승격합니다.
        /// </summary>
        /// <returns>유효한 Grid 내부 샘플이면 true, 계산에 필요한 데이터가 없으면 false입니다.</returns>
        public static bool TrySample(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            Vector3 worldPosition,
            out FlowFieldSample sample,
            out int baseCellIndex,
            out ushort gen0,
            out ushort gen1,
            out ushort gen2,
            out ushort gen3)
        {
            sample = FlowFieldSample.Stopped;
            baseCellIndex = -1;
            gen0 = gen1 = gen2 = gen3 = 0;
            if (!grid.IsValid
                || surface == null
                || !surface.HasValidData
                || workspace == null
                || workspace.Capacity != grid.CellCount
                || !grid.TryWorldToLocal(worldPosition, out int x, out int z))
                return false;

            baseCellIndex = grid.ToFlatIndex(x, z);
            float localX = (worldPosition.x - grid.Origin.x) / grid.CellSize - 0.5f;
            float localZ = (worldPosition.z - grid.Origin.z) / grid.CellSize - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(localX), 0, grid.Width - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(localZ), 0, grid.Depth - 1);
            int x1 = Mathf.Min(x0 + 1, grid.Width - 1);
            int z1 = Mathf.Min(z0 + 1, grid.Depth - 1);
            float tx = Mathf.Clamp01(localX - x0);
            float tz = Mathf.Clamp01(localZ - z0);

            int i00 = grid.ToFlatIndex(x0, z0);
            int i10 = grid.ToFlatIndex(x1, z0);
            int i01 = grid.ToFlatIndex(x0, z1);
            int i11 = grid.ToFlatIndex(x1, z1);
            gen0 = workspace.CellGeneration[i00];
            gen1 = workspace.CellGeneration[i10];
            gen2 = workspace.CellGeneration[i01];
            gen3 = workspace.CellGeneration[i11];

            bool baseHasSurface = surface.IsSurfaceValid(baseCellIndex);
            if (!baseHasSurface)
            {
                sample = FlowFieldSample.Stopped;
                return true;
            }

            if (workspace.Blocked[baseCellIndex])
            {
                sample = CreateCellSample(surface, workspace, baseCellIndex);
                return true;
            }

            // Exact cell-center match preserves legacy 1-sample behavior.
            Vector3 center = surface.GetCellCenter(grid, baseCellIndex);
            if (Mathf.Abs(worldPosition.x - center.x) <= grid.CellSize * 0.001f
                && Mathf.Abs(worldPosition.z - center.z) <= grid.CellSize * 0.001f)
            {
                sample = CreateCellSample(surface, workspace, baseCellIndex);
                return true;
            }

            Vector3 d00 = default, d10 = default, d01 = default, d11 = default;
            Vector3 n00 = Vector3.zero, n10 = Vector3.zero, n01 = Vector3.zero, n11 = Vector3.zero;
            float s00 = 1f, s10 = 1f, s01 = 1f, s11 = 1f;
            float w00 = TryWeight(surface, workspace, i00, out d00, out s00, out n00) ? 1f : 0f;
            float w10 = TryWeight(surface, workspace, i10, out d10, out s10, out n10) ? 1f : 0f;
            float w01 = TryWeight(surface, workspace, i01, out d01, out s01, out n01) ? 1f : 0f;
            float w11 = TryWeight(surface, workspace, i11, out d11, out s11, out n11) ? 1f : 0f;

            float w00b = w00 * (1f - tx) * (1f - tz);
            float w10b = w10 * tx * (1f - tz);
            float w01b = w01 * (1f - tx) * tz;
            float w11b = w11 * tx * tz;
            float sum = w00b + w10b + w01b + w11b;
            if (sum <= 0f)
            {
                sample = CreateCellSample(surface, workspace, baseCellIndex);
                return true;
            }

            Vector3 direction = (d00 * w00b + d10 * w10b + d01 * w01b + d11 * w11b) / sum;
            float speed = (s00 * w00b + s10 * w10b + s01 * w01b + s11 * w11b) / sum;
            Vector3 normal = (n00 * w00b + n10 * w10b + n01 * w01b + n11 * w11b) / sum;
            normal = FlowFieldVectorUtility.ValidateSurfaceNormal(normal);
            if (direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                direction = Vector3.ProjectOnPlane(direction, normal).normalized;
            else
                direction = Vector3.zero;

            sample = new FlowFieldSample(
                direction,
                speed,
                normal,
                true);
            return true;
        }

        private static FlowFieldSample CreateCellSample(
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            int index)
        {
            Vector3 normal = FlowFieldVectorUtility.ValidateSurfaceNormal(surface.GetSurfaceNormal(index));
            Vector3 direction = workspace.FinalDirections[index];
            if (direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                direction = Vector3.ProjectOnPlane(direction, normal).normalized;
            else
                direction = Vector3.zero;

            return new FlowFieldSample(
                direction,
                workspace.FinalSpeedMultipliers[index],
                normal,
                true);
        }

        private static bool TryWeight(
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            int index,
            out Vector3 direction,
            out float speed,
            out Vector3 normal)
        {
            direction = Vector3.zero;
            speed = 1f;
            normal = Vector3.zero;
            if (!surface.IsSurfaceValid(index) || workspace.Blocked[index])
                return false;

            direction = workspace.FinalDirections[index];
            speed = workspace.FinalSpeedMultipliers[index];
            normal = surface.GetSurfaceNormal(index);
            return true;
        }
    }
}
