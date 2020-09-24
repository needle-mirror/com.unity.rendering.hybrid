using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    internal enum OperationType : int
    {
        Upload = 0,
        Matrix_4x4 = 1,
        Matrix_Inverse_4x4 = 2,
        Matrix_3x4 = 3,
        Matrix_Inverse_3x4 = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Operation
    {
        public uint type;
        public uint srcOffset;
        public uint dstOffset;
        public uint dstOffsetExtra;
        public uint size;
        public uint count;
    }

    internal unsafe struct MappedBuffer
    {
        public byte* m_Data;
        public long m_Marker;
        public int m_BufferID;

        public static long PackMarker(long operationOffset, long dataOffset)
        {
            return (dataOffset << 32) | (operationOffset & 0xFFFFFFFF);
        }

        public static void UnpackMarker(long marker, out long operationOffset, out long dataOffset)
        {
            operationOffset = marker & 0xFFFFFFFF;
            dataOffset = (marker >> 32) & 0xFFFFFFFF;
        }

        public bool TryAlloc(int operationSize, int dataSize, out byte* ptr, out int operationOffset, out int dataOffset)
        {
            long originalMarker;
            long newMarker;
            long currOperationOffset;
            long currDataOffset;
            do
            {
                // Read the marker as is right now
                originalMarker = Interlocked.Read(ref m_Marker);
                UnpackMarker(originalMarker, out currOperationOffset, out currDataOffset);

                // Calculate the new offsets for operation and data
                // Operations are stored in the beginning of the buffer
                // Data is stored at the end of the buffer
                var newOperationOffset = currOperationOffset + operationSize;
                var newDataOffset = currDataOffset - dataSize;

                // Check if there was enough space in the buffer for this allocation
                if (newDataOffset < newOperationOffset)
                {
                    // Not enough space, return false
                    ptr = null;
                    operationOffset = 0;
                    dataOffset = 0;
                    return false;
                }

                newMarker = PackMarker(newOperationOffset, newDataOffset);

                // Finally we try to CAS the new marker in.
                // If anyone has allocated from the buffer in the meantime this will fail and the loop will rerun
            } while (Interlocked.CompareExchange(ref m_Marker, newMarker, originalMarker) != originalMarker);

            // Now we have succeeded in getting a data slot out and can return true.
            ptr = m_Data;
            operationOffset = (int)currOperationOffset;
            dataOffset = (int)(currDataOffset - dataSize);
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ThreadedSparseUploaderData
    {
        [NativeDisableUnsafePtrRestriction] public MappedBuffer* m_Buffers;
        public int m_NumBuffers;
        public int m_CurrBuffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ThreadedSparseUploader
    {
        // TODO: safety handle?
        [NativeDisableUnsafePtrRestriction] internal ThreadedSparseUploaderData* m_Data;

        public bool TryAlloc(int operationSize, int dataSize, out byte* ptr, out int operationOffset, out int dataOffset)
        {
            // Fetch current buffer and ensure we are not already out of GPU buffers to allocate from;
            var numBuffers = m_Data->m_NumBuffers;
            var buffer = m_Data->m_CurrBuffer;
            if (buffer < numBuffers)
            {
                do
                {
                    // Try to allocate from the current buffer
                    if (m_Data->m_Buffers[buffer].TryAlloc(operationSize, dataSize, out var p, out var op, out var d))
                    {
                        // Success, we can return true at onnce
                        ptr = p;
                        operationOffset = op;
                        dataOffset = d;
                        return true;
                    }

                    // Try to increment the buffer.
                    // If someone else has done this while we where trying to alloc we will use their
                    // value and du another iteration. Otherwise we will use our new value
                    buffer = Interlocked.CompareExchange(ref m_Data->m_CurrBuffer, buffer + 1, buffer);
                } while (buffer < m_Data->m_NumBuffers);
            }

            // We have run out of buffers, return false
            ptr = null;
            operationOffset = 0;
            dataOffset = 0;
            return false;
        }

        public void AddUpload(void* src, int size, int offsetInBytes, int repeatCount = 1)
        {
            var opsize = UnsafeUtility.SizeOf<Operation>();
            var allocSucceeded = TryAlloc(opsize, size, out var dst, out var operationOffset, out var dataOffset);

            if (!allocSucceeded)
                return; // TODO: message?

            if (repeatCount <= 0)
                repeatCount = 1;

            // TODO: Vectorized memcpy
            UnsafeUtility.MemCpy(dst + dataOffset, src, size);
            var op = new Operation
            {
                type = (uint)OperationType.Upload,
                srcOffset = (uint)dataOffset,
                dstOffset = (uint)offsetInBytes,
                dstOffsetExtra = 0,
                size = (uint)size,
                count = (uint)repeatCount
            };
            UnsafeUtility.MemCpy(dst + operationOffset, &op, opsize);
        }

        public void AddUpload<T>(T val, int offsetInBytes, int repeatCount = 1) where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>();
            AddUpload(&val, size, offsetInBytes, repeatCount);
        }

        public void AddUpload<T>(NativeArray<T> array, int offsetInBytes, int repeatCount = 1) where T : struct
        {
            var size = UnsafeUtility.SizeOf<T>() * array.Length;
            AddUpload(array.GetUnsafeReadOnlyPtr(), size, offsetInBytes, repeatCount);
        }

        public enum MatrixType
        {
            MatrixType4x4,
            MatrixType3x4,
        }

        // Expects an array of float4x4
        public void AddMatrixUpload(void* src, int numMatrices, int offset, int offsetInverse, MatrixType srcType, MatrixType dstType)
        {
            var size = numMatrices * sizeof(float3x4);
            var opsize = UnsafeUtility.SizeOf<Operation>();

            var allocSucceeded = TryAlloc(opsize, size, out var dst, out var operationOffset, out var dataOffset);

            if (!allocSucceeded)
                return; // TODO: message?

            if (srcType == MatrixType.MatrixType4x4)
            {
                var srcLocal = (byte*)src;
                var dstLocal = dst + dataOffset;
                for (int i = 0; i < numMatrices; ++i)
                {
                    for (int j = 0; j < 4; ++j)
                    {
                        UnsafeUtility.MemCpy(dstLocal, srcLocal, 12);
                        dstLocal += 12;
                        srcLocal += 16;
                    }
                }
            }
            else
            {
                UnsafeUtility.MemCpy(dst + dataOffset, src, size);
            }

            var uploadType = (offsetInverse == -1) ? (uint)OperationType.Matrix_4x4 : (uint)OperationType.Matrix_Inverse_4x4;
            uploadType += (dstType == MatrixType.MatrixType3x4) ? 2u : 0u;

            var op = new Operation
            {
                type = uploadType,
                srcOffset = (uint)dataOffset,
                dstOffset = (uint)offset,
                dstOffsetExtra = (uint)offsetInverse,
                size = (uint)size,
                count = 1,
            };
            UnsafeUtility.MemCpy(dst + operationOffset, &op, opsize);
        }
    }

    public unsafe struct SparseUploader : IDisposable
    {
        const int k_MaxThreadGroupsPerDispatch = 65535;

        int m_NumBufferedFrames;

        // Below is a frame queueing limiting hack to avoid overwriting live buffers
        // This will be removed once we have actual queries to the GPU what frame has passed
        ComputeBuffer[] m_LimitingBuffers;
        AsyncGPUReadbackRequest[] m_LimitingRequests;

        int m_BufferChunkSize;

        int m_CurrFrame;

        ComputeBuffer m_DestinationBuffer;

        List<ComputeBuffer> m_UploadBuffers;
        NativeArray<MappedBuffer> m_MappedBuffers;
        Stack<int> m_FreeBuffers;
        Stack<int>[] m_FrameReuseBuffers;

        ThreadedSparseUploaderData* m_ThreadData;

        ComputeShader m_SparseUploaderShader;
        int m_CopyKernelIndex;
        int m_ReplaceKernelIndex;

        int m_SrcBufferID;
        int m_DstBufferID;
        int m_OperationsBaseID;
        int m_ReplaceOperationSize;

        public SparseUploader(ComputeBuffer destinationBuffer, int bufferChunkSize = 16 * 1024 * 1024)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal)
                m_NumBufferedFrames = 4; // metal is hardcoded to 4
            else
                m_NumBufferedFrames = 3; // We use 3 to be safe, but the default value is 2.

#if !DISABLE_HYBRID_RENDERER_V2_FRAME_LIMIT
            // initialize frame queue limitation if we have async readback
            // if async readback is not available we have to fallback to frame counting
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                m_LimitingBuffers = new ComputeBuffer[m_NumBufferedFrames];
                m_LimitingRequests = new AsyncGPUReadbackRequest[m_NumBufferedFrames];
                for (var i = 0; i < m_NumBufferedFrames; i++)
                {
                    m_LimitingBuffers[i] = new ComputeBuffer(1, 4, ComputeBufferType.Default);
                    m_LimitingRequests[i] = AsyncGPUReadback.Request(m_LimitingBuffers[i]);
                }
            }
            else
#endif
            {
                m_LimitingBuffers = null;
                m_LimitingRequests = null;
            }

            m_BufferChunkSize = bufferChunkSize;
            m_CurrFrame = 0;

            m_DestinationBuffer = destinationBuffer;

            m_UploadBuffers = new List<ComputeBuffer>();
            m_MappedBuffers = new NativeArray<MappedBuffer>();
            m_FreeBuffers = new Stack<int>();
            m_FrameReuseBuffers = new Stack<int>[m_NumBufferedFrames];
            for (int i = 0; i < m_NumBufferedFrames; ++i)
            {
                m_FrameReuseBuffers[i] = new Stack<int>();
            }

            m_ThreadData = (ThreadedSparseUploaderData*)Memory.Unmanaged.Allocate(sizeof(ThreadedSparseUploaderData),
                UnsafeUtility.AlignOf<ThreadedSparseUploaderData>(), Allocator.Persistent);
            m_ThreadData->m_Buffers = null;
            m_ThreadData->m_NumBuffers = 0;
            m_ThreadData->m_CurrBuffer = 0;

            m_SparseUploaderShader = Resources.Load<ComputeShader>("SparseUploader");
            m_CopyKernelIndex = m_SparseUploaderShader.FindKernel("CopyKernel");
            m_ReplaceKernelIndex = m_SparseUploaderShader.FindKernel("ReplaceKernel");

            m_SrcBufferID = Shader.PropertyToID("srcBuffer");
            m_DstBufferID = Shader.PropertyToID("dstBuffer");
            m_OperationsBaseID = Shader.PropertyToID("operationsBase");
            m_ReplaceOperationSize = Shader.PropertyToID("replaceOperationSize");
        }

        public void Dispose()
        {
            ReleaseAllUploadBuffers();
            Memory.Unmanaged.Free(m_ThreadData, Allocator.Persistent);

#if !DISABLE_HYBRID_RENDERER_V2_FRAME_LIMIT
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                for (var i = 0; i < m_LimitingRequests.Length; i++)
                    m_LimitingRequests[i].WaitForCompletion();
                for (var i = 0; i < m_LimitingBuffers.Length; i++)
                   m_LimitingBuffers[i].Dispose();
            }
#endif
        }

        public void ReplaceBuffer(ComputeBuffer buffer, bool copyFromPrevious = false)
        {
            if (copyFromPrevious && m_DestinationBuffer != null)
            {
                // Since we have no code such as Graphics.CopyBuffer(dst, src) currently
                // we have to do this ourselves in a compute shader
                var srcSize = m_DestinationBuffer.count * m_DestinationBuffer.stride;
                m_SparseUploaderShader.SetBuffer(m_ReplaceKernelIndex, m_SrcBufferID, m_DestinationBuffer);
                m_SparseUploaderShader.SetBuffer(m_ReplaceKernelIndex, m_DstBufferID, buffer);
                m_SparseUploaderShader.SetInt(m_ReplaceOperationSize, srcSize);

                m_SparseUploaderShader.Dispatch(m_ReplaceKernelIndex, 1, 1, 1);
            }

            m_DestinationBuffer = buffer;
        }

        private void ReleaseAllUploadBuffers()
        {
            for (int i = 0; i < m_UploadBuffers.Count; ++i)
            {
                m_UploadBuffers[i].Dispose();
            }
        }

        private int AllocateComputeBuffer()
        {
            var id = m_UploadBuffers.Count;
            var cb = new ComputeBuffer(m_BufferChunkSize / 4, 4, ComputeBufferType.Raw, ComputeBufferMode.SubUpdates);
            m_UploadBuffers.Add(cb);
            return id;
        }

        private void EnsurePreallocatedComputeBuffers(int numBuffersNeeded)
        {
            while (m_FreeBuffers.Count < numBuffersNeeded)
            {
                var id = AllocateComputeBuffer();
                m_FreeBuffers.Push(id);
            }
        }

        public unsafe ThreadedSparseUploader Begin(int maxDataSizeInBytes, int biggestDataUpload, int maxOperationCount)
        {
#if !DISABLE_HYBRID_RENDERER_V2_FRAME_LIMIT
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                m_LimitingRequests[m_CurrFrame].WaitForCompletion();
                m_LimitingRequests[m_CurrFrame] = AsyncGPUReadback.Request(m_LimitingBuffers[m_CurrFrame]);
            }
#endif

            // First: recover all buffers from the previous frames (if any)
            // TODO: handle change in QualitySettings.maxQueuedFrames
            var currStack = m_FrameReuseBuffers[m_CurrFrame];
            while (currStack.Count != 0)
            {
                var buffer = currStack.Pop();
                m_FreeBuffers.Push(buffer);
            }

            // Second: calculate total size needed this frame, allocate buffers and map what is needed
            var operationSize = UnsafeUtility.SizeOf<Operation>();
            var maxOperationSizeInBytes = maxOperationCount * operationSize;
            var sizeNeeded = maxOperationSizeInBytes + maxDataSizeInBytes;
            var bufferSizeWithMaxPaddingRemoved = m_BufferChunkSize - operationSize - biggestDataUpload;
            var numBuffersNeeded = (sizeNeeded + bufferSizeWithMaxPaddingRemoved - 1) / bufferSizeWithMaxPaddingRemoved;
            EnsurePreallocatedComputeBuffers(numBuffersNeeded);

            if (numBuffersNeeded < 0)
                numBuffersNeeded = 0;

            m_MappedBuffers = new NativeArray<MappedBuffer>(numBuffersNeeded, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for(int i = 0; i < numBuffersNeeded; ++i)
            {
                var id = m_FreeBuffers.Pop();
                var cb = m_UploadBuffers[id];
                var data = cb.BeginWrite<byte>(0, m_BufferChunkSize);
                var marker = MappedBuffer.PackMarker(0, m_BufferChunkSize);
                m_MappedBuffers[i] = new MappedBuffer
                {
                    m_Data = (byte*)data.GetUnsafePtr(),
                    m_Marker = marker,
                    m_BufferID = id,
                };
            }

            m_ThreadData->m_Buffers = (MappedBuffer*)m_MappedBuffers.GetUnsafePtr();
            m_ThreadData->m_NumBuffers = numBuffersNeeded;

            // TODO: set safety handle on thread data
            return new ThreadedSparseUploader
            {
                m_Data = m_ThreadData
            };
        }

        private void DispatchUploads(int numOps, ComputeBuffer computeBuffer)
        {
            for (int iOp = 0; iOp < numOps; iOp += k_MaxThreadGroupsPerDispatch)
            {
                int opsBegin = iOp;
                int opsEnd = math.min(opsBegin + k_MaxThreadGroupsPerDispatch, numOps);
                int numThreadGroups = opsEnd - opsBegin;

                m_SparseUploaderShader.SetBuffer(m_CopyKernelIndex, m_SrcBufferID, computeBuffer);
                m_SparseUploaderShader.SetBuffer(m_CopyKernelIndex, m_DstBufferID, m_DestinationBuffer);
                m_SparseUploaderShader.SetInt(m_OperationsBaseID, opsBegin);

                m_SparseUploaderShader.Dispatch(m_CopyKernelIndex, numThreadGroups, 1, 1);
            }
        }

        public void EndAndCommit(ThreadedSparseUploader tsu)
        {
            var numBuffers = m_ThreadData->m_NumBuffers;
            for (int iBuf = 0; iBuf < numBuffers; ++iBuf)
            {
                var mappedBuffer = m_MappedBuffers[iBuf];
                MappedBuffer.UnpackMarker(mappedBuffer.m_Marker, out var operationOffset, out var dataOffset);
                var numOps = (int) (operationOffset / UnsafeUtility.SizeOf<Operation>());
                var computeBufferID = mappedBuffer.m_BufferID;
                var computeBuffer = m_UploadBuffers[computeBufferID];

                if (numOps > 0)
                {
                    computeBuffer.EndWrite<byte>(m_BufferChunkSize);

                    DispatchUploads(numOps, computeBuffer);

                    m_FrameReuseBuffers[m_CurrFrame].Push(computeBufferID);
                }
                else
                {
                    computeBuffer.EndWrite<byte>(0);
                    m_FreeBuffers.Push(computeBufferID);
                }
            }

            m_MappedBuffers.Dispose();

            m_CurrFrame += 1;
            if (m_CurrFrame >= m_NumBufferedFrames)
                m_CurrFrame = 0;
            // TODO: release safety handle of thread data
            m_ThreadData->m_Buffers = null;
            m_ThreadData->m_NumBuffers = 0;
            m_ThreadData->m_CurrBuffer = 0;
        }
    }
}
