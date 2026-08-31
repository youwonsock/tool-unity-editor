using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Builds the coarse topology representation from an already baked fine surface.
    /// Kept separate from the data asset so the asset remains a serialization-focused type.
    /// </summary>
    internal static class FlowFieldCoarseTopologyBaker
    {
        public static void Bake(
            FlowFieldGridSpace fineGrid,
            FlowFieldSurfaceBakeData surface,
            int coarseMultiplier,
            float walkableRatioThreshold,
            out byte[] walkable,
            out byte[] neighborMasks)
        {
            walkable = null;
            neighborMasks = null;
            if (!fineGrid.IsValid)
                throw new System.ArgumentException("Fine Grid is invalid.", nameof(fineGrid));
            if (surface == null || !surface.HasValidData)
                throw new System.ArgumentException("Surface bake data is invalid.", nameof(surface));
            if (coarseMultiplier < 2)
                throw new System.ArgumentOutOfRangeException(nameof(coarseMultiplier));
            if (float.IsNaN(walkableRatioThreshold) || float.IsInfinity(walkableRatioThreshold)
                || walkableRatioThreshold < 0f || walkableRatioThreshold > 1f)
                throw new System.ArgumentOutOfRangeException(nameof(walkableRatioThreshold));

            int coarseWidth = (fineGrid.Width + coarseMultiplier - 1) / coarseMultiplier;
            int coarseDepth = (fineGrid.Depth + coarseMultiplier - 1) / coarseMultiplier;
            if (coarseWidth <= 0 || coarseDepth <= 0)
                throw new System.ArgumentException("Fine Grid cannot produce a coarse grid.", nameof(fineGrid));
            int count = coarseWidth * coarseDepth;
            walkable = new byte[count];
            neighborMasks = new byte[count];

            for (int cz = 0; cz < coarseDepth; cz++)
            {
                for (int cx = 0; cx < coarseWidth; cx++)
                {
                    int coarseIndex = cz * coarseWidth + cx;
                    int minX = cx * coarseMultiplier;
                    int minZ = cz * coarseMultiplier;
                    int maxX = Mathf.Min(fineGrid.Width - 1, minX + coarseMultiplier - 1);
                    int maxZ = Mathf.Min(fineGrid.Depth - 1, minZ + coarseMultiplier - 1);
                    int total = 0;
                    int valid = 0;
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            total++;
                            if (surface.IsSurfaceValid(fineGrid.ToFlatIndex(x, z)))
                                valid++;
                        }
                    }

                    float ratio = total > 0 ? (float)valid / total : 0f;
                    walkable[coarseIndex] = ratio >= walkableRatioThreshold && valid > 0 ? (byte)1 : (byte)0;
                }
            }

            for (int cz = 0; cz < coarseDepth; cz++)
            {
                for (int cx = 0; cx < coarseWidth; cx++)
                {
                    int coarseIndex = cz * coarseWidth + cx;
                    if (walkable[coarseIndex] == 0)
                        continue;

                    byte mask = 0;
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int nx = cx + FlowFieldNeighborUtility.DeltaX[dir];
                        int nz = cz + FlowFieldNeighborUtility.DeltaZ[dir];
                        if (nx < 0 || nx >= coarseWidth || nz < 0 || nz >= coarseDepth)
                            continue;
                        if (walkable[nz * coarseWidth + nx] == 0)
                            continue;
                        if (HasBoundaryConnection(
                                fineGrid,
                                surface,
                                cx,
                                cz,
                                nx,
                                nz,
                                coarseMultiplier))
                            mask |= (byte)(1 << dir);
                    }

                    neighborMasks[coarseIndex] = mask;
                }
            }
        }

        private static bool HasBoundaryConnection(
            FlowFieldGridSpace fineGrid,
            FlowFieldSurfaceBakeData surface,
            int coarseX,
            int coarseZ,
            int neighborCoarseX,
            int neighborCoarseZ,
            int multiplier)
        {
            int minX = coarseX * multiplier;
            int minZ = coarseZ * multiplier;
            int maxX = Mathf.Min(fineGrid.Width - 1, minX + multiplier - 1);
            int maxZ = Mathf.Min(fineGrid.Depth - 1, minZ + multiplier - 1);
            int nMinX = neighborCoarseX * multiplier;
            int nMinZ = neighborCoarseZ * multiplier;
            int nMaxX = Mathf.Min(fineGrid.Width - 1, nMinX + multiplier - 1);
            int nMaxZ = Mathf.Min(fineGrid.Depth - 1, nMinZ + multiplier - 1);

            if (neighborCoarseX > coarseX)
            {
                int x = maxX;
                int nx = nMinX;
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int nz = nMinZ; nz <= nMaxZ; nz++)
                    {
                        if (Mathf.Abs(z - nz) > 1)
                            continue;
                        int from = fineGrid.ToFlatIndex(x, z);
                        int to = fineGrid.ToFlatIndex(nx, nz);
                        int dir = FlowFieldNeighborUtility.FindDirectionIndex(nx - x, nz - z);
                        if (dir >= 0 && surface.HasConnection(from, dir) && surface.IsSurfaceValid(to))
                            return true;
                    }
                }
            }
            else if (neighborCoarseX < coarseX)
            {
                int x = minX;
                int nx = nMaxX;
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int nz = nMinZ; nz <= nMaxZ; nz++)
                    {
                        if (Mathf.Abs(z - nz) > 1)
                            continue;
                        int from = fineGrid.ToFlatIndex(x, z);
                        int dir = FlowFieldNeighborUtility.FindDirectionIndex(nx - x, nz - z);
                        if (dir >= 0 && surface.HasConnection(from, dir))
                            return true;
                    }
                }
            }
            else if (neighborCoarseZ > coarseZ)
            {
                int z = maxZ;
                int nz = nMinZ;
                for (int x = minX; x <= maxX; x++)
                {
                    for (int nx = nMinX; nx <= nMaxX; nx++)
                    {
                        if (Mathf.Abs(x - nx) > 1)
                            continue;
                        int from = fineGrid.ToFlatIndex(x, z);
                        int dir = FlowFieldNeighborUtility.FindDirectionIndex(nx - x, nz - z);
                        if (dir >= 0 && surface.HasConnection(from, dir))
                            return true;
                    }
                }
            }
            else if (neighborCoarseZ < coarseZ)
            {
                int z = minZ;
                int nz = nMaxZ;
                for (int x = minX; x <= maxX; x++)
                {
                    for (int nx = nMinX; nx <= nMaxX; nx++)
                    {
                        if (Mathf.Abs(x - nx) > 1)
                            continue;
                        int from = fineGrid.ToFlatIndex(x, z);
                        int dir = FlowFieldNeighborUtility.FindDirectionIndex(nx - x, nz - z);
                        if (dir >= 0 && surface.HasConnection(from, dir))
                            return true;
                    }
                }
            }

            return false;
        }
    }
}
