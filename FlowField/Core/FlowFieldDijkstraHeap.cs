namespace Supercent.Common.FlowField
{
    /// <summary>
    /// FlowFieldWorkspace의 배열을 이용한 decrease-key 이진 힙 Dijkstra 큐.
    /// 동일 비용 셀은 낮은 셀 인덱스가 먼저 pop되도록 tie-break 합니다.
    /// </summary>
    internal static class FlowFieldDijkstraHeap
    {
        public static void InsertOrDecrease(FlowFieldWorkspace workspace, int cell)
        {
            int position = workspace.HeapPositions[cell];
            if (position < 0)
            {
                position = workspace.HeapCount++;
                workspace.HeapCells[position] = cell;
                workspace.HeapPositions[cell] = position;
            }

            SiftUp(workspace, position);
        }

        public static int Pop(FlowFieldWorkspace workspace)
        {
            int root = workspace.HeapCells[0];
            int lastPosition = --workspace.HeapCount;
            int last = workspace.HeapCells[lastPosition];
            workspace.HeapPositions[root] = -1;
            if (lastPosition > 0)
            {
                workspace.HeapCells[0] = last;
                workspace.HeapPositions[last] = 0;
                SiftDown(workspace, 0);
            }

            return root;
        }

        private static void SiftUp(FlowFieldWorkspace workspace, int position)
        {
            while (position > 0)
            {
                int parent = (position - 1) >> 1;
                if (!Less(workspace, workspace.HeapCells[position], workspace.HeapCells[parent]))
                    break;

                Swap(workspace, position, parent);
                position = parent;
            }
        }

        private static void SiftDown(FlowFieldWorkspace workspace, int position)
        {
            while (true)
            {
                int left = position * 2 + 1;
                if (left >= workspace.HeapCount)
                    return;

                int right = left + 1;
                int smallest = right < workspace.HeapCount
                    && Less(workspace, workspace.HeapCells[right], workspace.HeapCells[left])
                    ? right
                    : left;
                if (!Less(workspace, workspace.HeapCells[smallest], workspace.HeapCells[position]))
                    return;

                Swap(workspace, position, smallest);
                position = smallest;
            }
        }

        private static bool Less(FlowFieldWorkspace workspace, int leftCell, int rightCell)
        {
            int leftCost = workspace.Costs[leftCell];
            int rightCost = workspace.Costs[rightCell];
            return leftCost < rightCost || leftCost == rightCost && leftCell < rightCell;
        }

        private static void Swap(FlowFieldWorkspace workspace, int left, int right)
        {
            int leftCell = workspace.HeapCells[left];
            int rightCell = workspace.HeapCells[right];
            workspace.HeapCells[left] = rightCell;
            workspace.HeapCells[right] = leftCell;
            workspace.HeapPositions[leftCell] = right;
            workspace.HeapPositions[rightCell] = left;
        }
    }
}
