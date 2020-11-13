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

    internal class BufferPool : IDisposable
    {
        private List<ComputeBuffer> m_Buffers;
        private Stack<int> m_FreeBufferIds;

        private int m_Count;
        private int m_Stride;
        private ComputeBufferType m_Type;
        private ComputeBufferMode m_Mode;


        public BufferPool(int count, int stride, ComputeBufferType type = ComputeBufferType.Default, ComputeBufferMode mode = ComputeBufferMode.Immutable)
        {
            m_Buffers = new List<ComputeBuffer>();
            m_FreeBufferIds = new Stack<int>();

            m_Count = count;
            m_Stride = stride;
            m_Type = type;
            m_Mode = mode;
        }

        public void Dispose()
        {
            for (int i = 0; i < m_Buffers.Count; ++i)
            {
                m_Buffers[i].Dispose();
            }
        }

        private int AllocateBuffer()
        {
            var id = m_Buffers.Count;
            var cb = new ComputeBuffer(m_Count, m_Stride, m_Type, m_Mode);
            m_Buffers.Add(cb);
            return id;
        }

        public int GetBufferId()
        {
            if (m_FreeBufferIds.Count == 0)
                return AllocateBuffer();

            return m_FreeBufferIds.Pop();
        }

        public ComputeBuffer GetBufferFromId(int id)
        {
            return m_Buffers[id];
        }

        public void PutBufferId(int id)
        {
            m_FreeBufferIds.Push(id);
        }
    }

    public unsafe struct SparseUploader : IDisposable
    {
        const int k_MaxThreadGroupsPerDispatch = 65535;

        int m_BufferChunkSize;

        ComputeBuffer m_DestinationBuffer;

        BufferPool m_FenceBufferPool;
        BufferPool m_UploadBufferPool;

        NativeArray<MappedBuffer> m_MappedBuffers;

        class FrameData
        {
            public Stack<int> m_Buffers;
            public int m_FenceBuffer;
            public AsyncGPUReadbackRequest m_Fence;

            public FrameData()
            {
                m_Buffers = new Stack<int>();
                m_FenceBuffer = -1;
            }
        }

        Stack<FrameData> m_FreeFrameData;
        List<FrameData> m_FrameData;

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
            m_BufferChunkSize = bufferChunkSize;

            m_DestinationBuffer = destinationBuffer;

            m_FenceBufferPool = new BufferPool(1, 4);
            m_UploadBufferPool = new BufferPool(m_BufferChunkSize / 4, 4, ComputeBufferType.Raw, ComputeBufferMode.SubUpdates);
            m_MappedBuffers = new NativeArray<MappedBuffer>();
            m_FreeFrameData = new Stack<FrameData>();
            m_FrameData = new List<FrameData>();

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
            m_FenceBufferPool.Dispose();
            m_UploadBufferPool.Dispose();
            Memory.Unmanaged.Free(m_ThreadData, Allocator.Persistent);
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


        private void RecoverBuffers()
        {
            var numFree = 0;
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                for (int i = 0; i < m_FrameData.Count; ++i)
                {
                    if (m_FrameData[i].m_Fence.done)
                    {
                        numFree = i + 1;
                    }
                }
            }
            else
            {
                // Platform does not support async readbacks so we assume 3 frames in flight and once building on CPU
                // always pop one from the frame data queue
                if (m_FrameData.Count > 3)
                    numFree = 1;
            }

            for (int i = 0; i < numFree; ++i)
            {
                while (m_FrameData[i].m_Buffers.Count > 0)
                {
                    m_FenceBufferPool.PutBufferId(m_FrameData[i].m_FenceBuffer);
                    var buffer = m_FrameData[i].m_Buffers.Pop();
                    m_UploadBufferPool.PutBufferId(buffer);
                }
                m_FreeFrameData.Push(m_FrameData[i]);
            }

            if (numFree > 0)
            {
                m_FrameData.RemoveRange(0, numFree);
            }
        }

        public ThreadedSparseUploader Begin(int maxDataSizeInBytes, int biggestDataUpload, int maxOperationCount)
        {
            // First: recover all buffers from the previous frames (if any)
            RecoverBuffers();

            // Second: calculate total size needed this frame, allocate buffers and map what is needed
            var operationSize = UnsafeUtility.SizeOf<Operation>();
            var maxOperationSizeInBytes = maxOperationCount * operationSize;
            var sizeNeeded = maxOperationSizeInBytes + maxDataSizeInBytes;
            var bufferSizeWithMaxPaddingRemoved = m_BufferChunkSize - operationSize - biggestDataUpload;
            var numBuffersNeeded = (sizeNeeded + bufferSizeWithMaxPaddingRemoved - 1) / bufferSizeWithMaxPaddingRemoved;

            if (numBuffersNeeded < 0)
                numBuffersNeeded = 0;

            m_MappedBuffers = new NativeArray<MappedBuffer>(numBuffersNeeded, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for(int i = 0; i < numBuffersNeeded; ++i)
            {
                var id = m_UploadBufferPool.GetBufferId();
                var cb = m_UploadBufferPool.GetBufferFromId(id);
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

        private void StepFrame()
        {
            // TODO: release safety handle of thread data
            m_ThreadData->m_Buffers = null;
            m_ThreadData->m_NumBuffers = 0;
            m_ThreadData->m_CurrBuffer = 0;
        }

        public void EndAndCommit(ThreadedSparseUploader tsu)
        {
            var numBuffers = m_ThreadData->m_NumBuffers;
            var frameData = m_FreeFrameData.Count > 0 ? m_FreeFrameData.Pop() : new FrameData();
            for (int iBuf = 0; iBuf < numBuffers; ++iBuf)
            {
                var mappedBuffer = m_MappedBuffers[iBuf];
                MappedBuffer.UnpackMarker(mappedBuffer.m_Marker, out var operationOffset, out var dataOffset);
                var numOps = (int) (operationOffset / UnsafeUtility.SizeOf<Operation>());
                var computeBufferID = mappedBuffer.m_BufferID;
                var computeBuffer = m_UploadBufferPool.GetBufferFromId(computeBufferID);

                if (numOps > 0)
                {
                    computeBuffer.EndWrite<byte>(m_BufferChunkSize);

                    DispatchUploads(numOps, computeBuffer);

                    frameData.m_Buffers.Push(computeBufferID);
                }
                else
                {
                    computeBuffer.EndWrite<byte>(0);
                    m_UploadBufferPool.PutBufferId(computeBufferID);
                }
            }

            if (SystemInfo.supportsAsyncGPUReadback)
            {
                var fenceBufferId = m_FenceBufferPool.GetBufferId();
                frameData.m_FenceBuffer = fenceBufferId;
                frameData.m_Fence = AsyncGPUReadback.Request(m_FenceBufferPool.GetBufferFromId(fenceBufferId));
            }

            m_FrameData.Add(frameData);

            m_MappedBuffers.Dispose();

            StepFrame();
        }

        public void FrameCleanup()
        {
            var numBuffers = m_ThreadData->m_NumBuffers;

            if (numBuffers == 0)
                return;

            // These buffers where never used, so they gets returned to the pool at once
            for (int iBuf = 0; iBuf < numBuffers; ++iBuf)
            {
                var mappedBuffer = m_MappedBuffers[iBuf];
                MappedBuffer.UnpackMarker(mappedBuffer.m_Marker, out var operationOffset, out var dataOffset);
                var computeBufferID = mappedBuffer.m_BufferID;
                var computeBuffer = m_UploadBufferPool.GetBufferFromId(computeBufferID);

                computeBuffer.EndWrite<byte>(0);
                m_UploadBufferPool.PutBufferId(computeBufferID);
            }

            m_MappedBuffers.Dispose();

            StepFrame();
        }
    }
}
