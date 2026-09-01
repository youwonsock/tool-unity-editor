using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Common.FlowField
{
    internal enum FlowFieldComputeFailureKind
    {
        Unsupported,
        Error,
        Overflow,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GpuSurfaceCell
    {
        public float Height;
        public Vector3 Normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GpuFlowCell
    {
        public Vector3 Direction;
        public int NextCell;
    }

    internal readonly struct FlowFieldComputeRequest
    {
        internal FlowFieldGridSpace Grid { get; }
        internal FlowFieldSurfaceBakeData Surface { get; }
        internal FlowFieldWorkspace Workspace { get; }
        internal int GoalIndex { get; }
        internal int MaxGpuWaves { get; }
        internal int Version { get; }

        internal FlowFieldComputeRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            int goalIndex,
            int maxGpuWaves,
            int version)
        {
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            GoalIndex = goalIndex;
            MaxGpuWaves = maxGpuWaves;
            Version = version;
        }
    }

    internal sealed class FlowFieldComputeSolver : IDisposable
    {
        private const int THREADS = 64;
        private const int MAX_BATCH_WAVES = 64;
        private const uint VALID = 1u << 0;
        private const uint BLOCKED = 1u << 1;
        private const uint INFLUENCE = 1u << 2;
        private const int NEIGHBOR_SHIFT = 3;
        private const uint UNVISITED = 0x7fffffffu;

        private readonly ComputeShader _shader;
        private readonly int _initializeKernel = -1;
        private readonly int _prepareKernel = -1;
        private readonly int _expandKernel = -1;
        private readonly int _directionKernel = -1;
        private ComputeBuffer _cellState;
        private ComputeBuffer _surfaceData;
        private ComputeBuffer _frontierA;
        private ComputeBuffer _frontierB;
        private ComputeBuffer _control;
        private ComputeBuffer _indirectArgs;
        private ComputeBuffer _output;
        private uint[] _stateUpload;
        private GpuSurfaceCell[] _surfaceUpload;
        private readonly uint[] _zeroControl = new uint[4];
        private readonly uint[] _initialArgs = { 1u, 1u, 1u };
        private NativeArray<GpuFlowCell> _readbackA;
        private NativeArray<GpuFlowCell> _readbackB;
        private bool _disposed;
        private bool _running;
        private bool _pendingControl;
        private bool _pendingOutput;
        private AsyncGPUReadbackRequest _controlRequest;
        private AsyncGPUReadbackRequest _outputRequest;
        private bool _outputParity;
        private int _cellCount;
        private int _frontierParity;
        private int _waves;
        private int _maxWaves;
        private FlowFieldComputeRequest _request;
        private Action<FlowFieldComputeRequest, NativeArray<GpuFlowCell>> _completed;
        private Action<FlowFieldComputeRequest, FlowFieldComputeFailureKind, Exception> _failed;

        internal bool IsRunning => _running;
        internal bool IsSupported => !_disposed
            && _shader != null
            && SystemInfo.supportsComputeShaders
            && SystemInfo.supportsAsyncGPUReadback;

        internal FlowFieldComputeSolver(ComputeShader shader)
        {
            _shader = shader;
            if (_shader == null)
                return;

            _initializeKernel = _shader.FindKernel("InitializeField");
            _prepareKernel = _shader.FindKernel("PrepareWave");
            _expandKernel = _shader.FindKernel("ExpandFrontier");
            _directionKernel = _shader.FindKernel("BuildFlowDirections");
        }

        internal bool Start(
            in FlowFieldComputeRequest request,
            Action<FlowFieldComputeRequest, NativeArray<GpuFlowCell>> completed,
            Action<FlowFieldComputeRequest, FlowFieldComputeFailureKind, Exception> failed)
        {
            if (_disposed || _running || !IsSupported || completed == null || failed == null)
                return false;
            if (!request.Grid.IsValid
                || request.Surface == null
                || !request.Surface.HasValidData
                || request.Workspace == null
                || request.Workspace.Capacity != request.Grid.CellCount
                || request.Workspace.TopologyMasks == null
                || request.Workspace.TopologyMasks.Length != request.Grid.CellCount
                || request.GoalIndex < 0
                || request.GoalIndex >= request.Grid.CellCount)
                return false;

            try
            {
                DrainPendingReadbacks();
                EnsureBuffers(request.Grid.CellCount);
                UploadInputs(request);
                BindCommonParameters(request);
                _shader.SetInt("_GoalIndex", request.GoalIndex);
                _shader.SetInt("_FrontierParity", 0);
                _shader.SetInt("_CurrentCountOffset", 0);
                _shader.SetInt("_NextCountOffset", 4);
                _shader.SetInt("_CellCount", request.Grid.CellCount);
                _shader.SetInt("_Width", request.Grid.Width);
                _shader.SetInt("_Depth", request.Grid.Depth);
                BindKernel(_initializeKernel);
                _shader.Dispatch(_initializeKernel, Mathf.Max(1, (request.Grid.CellCount + THREADS - 1) / THREADS), 1, 1);

                _request = request;
                _completed = completed;
                _failed = failed;
                _cellCount = request.Grid.CellCount;
                _frontierParity = 0;
                _waves = 0;
                _maxWaves = Mathf.Min(request.Grid.CellCount, Mathf.Max(64, request.MaxGpuWaves));
                _running = true;
                _pendingControl = false;
                _pendingOutput = false;
                _outputParity = false;
                RunBatch();
                return true;
            }
            catch (Exception exception)
            {
                bool callbackWillHandleFailure = _running;
                Fail(FlowFieldComputeFailureKind.Error, exception);
                // If dispatch reached the running state, Fail already invoked
                // the Manager fallback callback. Treat the request as accepted
                // so the caller does not compose the managed field twice.
                return callbackWillHandleFailure;
            }
        }

        private void RunBatch()
        {
            if (!_running)
                return;

            int remaining = _maxWaves - _waves;
            int batch = Mathf.Min(MAX_BATCH_WAVES, remaining);
            if (batch <= 0)
            {
                Fail(FlowFieldComputeFailureKind.Overflow, null);
                return;
            }

            for (int i = 0; i < batch; i++)
            {
                int currentOffset = _frontierParity == 0 ? 0 : 4;
                int nextOffset = _frontierParity == 0 ? 4 : 0;
                _shader.SetInt("_FrontierParity", _frontierParity);
                _shader.SetInt("_CurrentCountOffset", currentOffset);
                _shader.SetInt("_NextCountOffset", nextOffset);
                BindKernel(_prepareKernel);
                _shader.Dispatch(_prepareKernel, 1, 1, 1);
                BindKernel(_expandKernel);
                _shader.DispatchIndirect(_expandKernel, _indirectArgs, 0);
                _frontierParity ^= 1;
                _waves++;
            }

            _pendingControl = true;
            _controlRequest = AsyncGPUReadback.Request(_control, OnControlReadback);
        }

        private void OnControlReadback(AsyncGPUReadbackRequest readback)
        {
            _pendingControl = false;
            if (!_running)
                return;
            if (readback.hasError)
            {
                Fail(FlowFieldComputeFailureKind.Error, new InvalidOperationException("FlowField ControlBuffer readback failed."));
                return;
            }

            try
            {
                NativeArray<uint> values = readback.GetData<uint>();
                uint overflow = values.Length > 2 ? values[2] : 1u;
                int activeOffset = _frontierParity == 0 ? 0 : 4;
                uint active = values.Length > activeOffset / 4 ? values[activeOffset / 4] : 0u;
                if (overflow != 0u)
                {
                    Fail(FlowFieldComputeFailureKind.Overflow, null);
                    return;
                }

                if (active == 0u)
                {
                    BuildDirectionsAndReadback();
                    return;
                }

                if (_waves >= _maxWaves)
                {
                    Fail(FlowFieldComputeFailureKind.Overflow, null);
                    return;
                }

                RunBatch();
            }
            catch (Exception exception)
            {
                Fail(FlowFieldComputeFailureKind.Error, exception);
            }
        }

        private void BuildDirectionsAndReadback()
        {
            try
            {
                BindKernel(_directionKernel);
                _shader.Dispatch(_directionKernel, Mathf.Max(1, (_cellCount + THREADS - 1) / THREADS), 1, 1);
                _outputParity = !_outputParity;
                NativeArray<GpuFlowCell> target = _outputParity ? _readbackA : _readbackB;
                _pendingOutput = true;
                _outputRequest = AsyncGPUReadback.RequestIntoNativeArray(ref target, _output, OnOutputReadback);
            }
            catch (Exception exception)
            {
                Fail(FlowFieldComputeFailureKind.Error, exception);
            }
        }

        private void OnOutputReadback(AsyncGPUReadbackRequest readback)
        {
            _pendingOutput = false;
            if (!_running)
                return;
            if (readback.hasError)
            {
                Fail(FlowFieldComputeFailureKind.Error, new InvalidOperationException("FlowField output readback failed."));
                return;
            }

            NativeArray<GpuFlowCell> result = _outputParity ? _readbackA : _readbackB;
            _running = false;
            Action<FlowFieldComputeRequest, NativeArray<GpuFlowCell>> callback = _completed;
            _completed = null;
            _failed = null;
            callback?.Invoke(_request, result);
        }

        private void Fail(FlowFieldComputeFailureKind kind, Exception exception)
        {
            if (!_running && _failed == null)
                return;
            _running = false;
            Action<FlowFieldComputeRequest, FlowFieldComputeFailureKind, Exception> callback = _failed;
            _completed = null;
            _failed = null;
            callback?.Invoke(_request, kind, exception);
        }

        private void DrainPendingReadbacks()
        {
            if (_pendingControl)
            {
                _controlRequest.WaitForCompletion();
                _pendingControl = false;
            }

            if (_pendingOutput)
            {
                _outputRequest.WaitForCompletion();
                _pendingOutput = false;
            }
        }

        private void EnsureBuffers(int cellCount)
        {
            if (_cellCount == cellCount && _cellState != null)
                return;

            ReleaseBuffers();
            try
            {
                _cellState = new ComputeBuffer(cellCount * 2, sizeof(uint), ComputeBufferType.Raw);
                _surfaceData = new ComputeBuffer(cellCount, 16, ComputeBufferType.Structured);
                _frontierA = new ComputeBuffer(cellCount, sizeof(uint), ComputeBufferType.Structured);
                _frontierB = new ComputeBuffer(cellCount, sizeof(uint), ComputeBufferType.Structured);
                _control = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.Raw);
                _indirectArgs = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
                _output = new ComputeBuffer(cellCount, 16, ComputeBufferType.Structured);
                _stateUpload = new uint[cellCount * 2];
                _surfaceUpload = new GpuSurfaceCell[cellCount];
                _readbackA = new NativeArray<GpuFlowCell>(cellCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _readbackB = new NativeArray<GpuFlowCell>(cellCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _cellCount = cellCount;
            }
            catch
            {
                ReleaseBuffers();
                throw;
            }
        }

        private void UploadInputs(in FlowFieldComputeRequest request)
        {
            Array.Clear(_stateUpload, 0, _stateUpload.Length);
            Array.Clear(_surfaceUpload, 0, _surfaceUpload.Length);
            uint[] state = _stateUpload;
            GpuSurfaceCell[] surfaces = _surfaceUpload;
            for (int index = 0; index < request.Grid.CellCount; index++)
            {
                uint flags = 0u;
                if (request.Surface.IsSurfaceValid(index))
                    flags |= VALID;
                if (request.Workspace.Blocked[index])
                    flags |= BLOCKED;
                if (request.Workspace.InfluenceMask[index])
                    flags |= INFLUENCE;

                uint topology = request.Workspace.TopologyMasks[index];
                state[index * 2] = UNVISITED;
                state[index * 2 + 1] = flags | (topology << NEIGHBOR_SHIFT);
                if (request.Surface.IsSurfaceValid(index))
                {
                    surfaces[index] = new GpuSurfaceCell
                    {
                        Height = request.Surface.GetCellCenter(request.Grid, index).y,
                        Normal = request.Surface.GetSurfaceNormal(index),
                    };
                }
            }

            _cellState.SetData(state);
            _surfaceData.SetData(surfaces);
            _control.SetData(_zeroControl);
            _indirectArgs.SetData(_initialArgs);
        }

        private void BindCommonParameters(in FlowFieldComputeRequest request)
        {
            _shader.SetInt("_CellCount", request.Grid.CellCount);
            _shader.SetInt("_Width", request.Grid.Width);
            _shader.SetInt("_Depth", request.Grid.Depth);
            _shader.SetFloat("_CellSize", request.Grid.CellSize);
            _shader.SetVector("_GridOrigin", request.Grid.Origin);
        }

        private void BindKernel(int kernel)
        {
            _shader.SetBuffer(kernel, "CellStateBuffer", _cellState);
            _shader.SetBuffer(kernel, "SurfaceDataBuffer", _surfaceData);
            _shader.SetBuffer(kernel, "FrontierA", _frontierA);
            _shader.SetBuffer(kernel, "FrontierB", _frontierB);
            _shader.SetBuffer(kernel, "ControlBuffer", _control);
            _shader.SetBuffer(kernel, "IndirectArgsBuffer", _indirectArgs);
            _shader.SetBuffer(kernel, "OutputBuffer", _output);
        }

        private void ReleaseBuffers()
        {
            _cellState?.Release();
            _surfaceData?.Release();
            _frontierA?.Release();
            _frontierB?.Release();
            _control?.Release();
            _indirectArgs?.Release();
            _output?.Release();
            _cellState = null;
            _surfaceData = null;
            _frontierA = null;
            _frontierB = null;
            _control = null;
            _indirectArgs = null;
            _output = null;
            _stateUpload = null;
            _surfaceUpload = null;
            if (_readbackA.IsCreated)
                _readbackA.Dispose();
            if (_readbackB.IsCreated)
                _readbackB.Dispose();
            _cellCount = 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                // Invalidate callbacks first. Unity may invoke a completed
                // request while WaitForCompletion drains the graphics queue.
                bool hadControl = _pendingControl;
                bool hadOutput = _pendingOutput;
                _running = false;
                _pendingControl = false;
                _pendingOutput = false;
                if (hadControl)
                    _controlRequest.WaitForCompletion();
                if (hadOutput)
                    _outputRequest.WaitForCompletion();
            }
            finally
            {
                _running = false;
                _completed = null;
                _failed = null;
                ReleaseBuffers();
                _disposed = true;
            }
        }
    }
}
