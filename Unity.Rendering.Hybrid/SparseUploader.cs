#if ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_1_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct UploadOperation
    {
        public int srcOffset;
        public int dstOffset;
        public int size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ThreadedSparseUploaderData
    {
        // TODO: safety handle?
        [NativeDisableUnsafePtrRestriction] public byte* m_DataPtr;
        [NativeDisableUnsafePtrRestriction] public UploadOperation* m_CopyOperationsPtr;
        public int m_CurrDataOffset;
        public int m_CurrOperation;
        public int m_MaxDataOffset;
        public int m_MaxOperation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ThreadedSparseUploader
    {
        // TODO: safety handle?
        [NativeDisableUnsafePtrRestriction] internal ThreadedSparseUploaderData* m_Data;

        public void AddUpload(void* src, int size, int offsetInBytes)
        {
            var dataOffset = Interlocked.Add(ref m_Data->m_CurrDataOffset, size);

            if (dataOffset > m_Data->m_MaxDataOffset)
                return; // TODO: message?
            dataOffset -= size; // since Interlocked.Add returns value after addition

            var operationIndex = Interlocked.Increment(ref m_Data->m_CurrOperation);
            
            if (operationIndex > m_Data->m_MaxOperation)
                return; // TODO: message?
            operationIndex -= 1; // since Interlocked.Increment returns value after incrementing


            var dst = m_Data->m_DataPtr;
            // TODO: Vectorized memcpy
            UnsafeUtility.MemCpy(dst + dataOffset, src, size);
            m_Data->m_CopyOperationsPtr[operationIndex] = new UploadOperation { dstOffset = offsetInBytes, srcOffset = dataOffset, size = size };
        }

        public void AddUpload<T>(T val, int offsetInBytes) where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>();
            AddUpload(&val, size, offsetInBytes);
        }

        public void AddUpload<T>(NativeArray<T> array, int offsetInBytes) where T : struct
        {
            var size = UnsafeUtility.SizeOf<T>() * array.Length;
            AddUpload(array.GetUnsafeReadOnlyPtr(), size, offsetInBytes);
        }
    }

    public unsafe struct SparseUploader : IDisposable
    {
        const int k_NumBufferedFrames = 3;
        int m_CurrFrame;

        ComputeBuffer m_DestinationBuffer;

        ComputeBuffer[] m_DataBuffer;
        ComputeBuffer[] m_UploadOperationBuffer;
        NativeArray<byte> m_DataArray;
        NativeArray<UploadOperation> m_UploadOperationsArray;

        ThreadedSparseUploaderData* m_ThreadData;

        ComputeShader m_SparseUploaderShader;
        int m_KernelIndex;

        public SparseUploader(ComputeBuffer dst)
        {
            m_CurrFrame = 0;

            m_DestinationBuffer = dst;

            m_DataBuffer = new ComputeBuffer[k_NumBufferedFrames];
            m_UploadOperationBuffer = new ComputeBuffer[k_NumBufferedFrames];

            m_DataArray = new NativeArray<byte>();
            m_UploadOperationsArray = new NativeArray<UploadOperation>();

            m_ThreadData = (ThreadedSparseUploaderData*)UnsafeUtility.Malloc(sizeof(ThreadedSparseUploaderData), UnsafeUtility.AlignOf<ThreadedSparseUploaderData>(), Allocator.Persistent);
            m_ThreadData->m_DataPtr = null;
            m_ThreadData->m_CopyOperationsPtr = null;
            m_ThreadData->m_CurrDataOffset = 0;
            m_ThreadData->m_CurrOperation = 0;
            m_ThreadData->m_MaxDataOffset = 0;
            m_ThreadData->m_MaxOperation = 0;

            m_SparseUploaderShader = Resources.Load<ComputeShader>("SparseUploader");
            m_KernelIndex = m_SparseUploaderShader.FindKernel("SparseUploader");
        }

        public void Dispose()
        {
            for (int i = 0; i < k_NumBufferedFrames; ++i)
            {
                if(m_DataBuffer[i] != null)
                    m_DataBuffer[i].Dispose();

                if (m_UploadOperationBuffer[i] != null)
                    m_UploadOperationBuffer[i].Dispose();
            }

            UnsafeUtility.Free(m_ThreadData, Allocator.Persistent);
        }

        public unsafe ThreadedSparseUploader Begin(int maxDataSizeInBytes, int maxOperationCount)
        {
            if (m_DataBuffer[m_CurrFrame] == null || m_DataBuffer[m_CurrFrame].count < maxDataSizeInBytes / 4)
            {
                if (m_DataBuffer[m_CurrFrame] != null)
                    m_DataBuffer[m_CurrFrame].Dispose();

                m_DataBuffer[m_CurrFrame] = new ComputeBuffer((maxDataSizeInBytes + 3) / 4,
                    4,
                    ComputeBufferType.Raw,
                    ComputeBufferMode.SubUpdates);
            }

            if (m_UploadOperationBuffer[m_CurrFrame] == null || m_UploadOperationBuffer[m_CurrFrame].count < maxOperationCount)
            {
                if (m_UploadOperationBuffer[m_CurrFrame] != null)
                    m_UploadOperationBuffer[m_CurrFrame].Dispose();

                m_UploadOperationBuffer[m_CurrFrame] = new ComputeBuffer(maxOperationCount,
                    UnsafeUtility.SizeOf<UploadOperation>(),
                    ComputeBufferType.Default,
                    ComputeBufferMode.SubUpdates);
            }

            m_DataArray = m_DataBuffer[m_CurrFrame].BeginWrite<byte>(0, maxDataSizeInBytes);
            m_UploadOperationsArray = m_UploadOperationBuffer[m_CurrFrame].BeginWrite<UploadOperation>(0, maxOperationCount);

            m_ThreadData->m_DataPtr = (byte*)m_DataArray.GetUnsafePtr();
            m_ThreadData->m_CopyOperationsPtr = (UploadOperation*)m_UploadOperationsArray.GetUnsafePtr();
            m_ThreadData->m_CurrDataOffset = 0;
            m_ThreadData->m_CurrOperation = 0;
            m_ThreadData->m_MaxDataOffset = maxDataSizeInBytes;
            m_ThreadData->m_MaxOperation = maxOperationCount;

            // TODO: set safety handle on thread data
            return new ThreadedSparseUploader
            {
                m_Data = m_ThreadData
            };
        }

        public void EndAndCommit(ThreadedSparseUploader tsu)
        {
            // TODO: release safety handle of thread data
            m_ThreadData->m_DataPtr = null;
            m_ThreadData->m_CopyOperationsPtr = null;
            int writtenData = math.min(m_ThreadData->m_CurrDataOffset, m_ThreadData->m_MaxDataOffset);
            int writtenOps  = math.min(m_ThreadData->m_CurrOperation, m_ThreadData->m_MaxOperation);
            m_DataBuffer[m_CurrFrame].EndWrite<byte>(writtenData);
            m_UploadOperationBuffer[m_CurrFrame].EndWrite<UploadOperation>(writtenOps);

            if (m_ThreadData->m_CurrOperation > 0)
            {
                m_SparseUploaderShader.SetBuffer(m_KernelIndex, "operations", m_UploadOperationBuffer[m_CurrFrame]);
                m_SparseUploaderShader.SetBuffer(m_KernelIndex, "src", m_DataBuffer[m_CurrFrame]);
                m_SparseUploaderShader.SetBuffer(m_KernelIndex, "dstAddr", m_DestinationBuffer);
                m_SparseUploaderShader.Dispatch(m_KernelIndex, m_ThreadData->m_CurrOperation, 1, 1);
            }

            m_CurrFrame += 1;
            if (m_CurrFrame >= k_NumBufferedFrames)
                m_CurrFrame = 0;
            m_ThreadData->m_CurrDataOffset = 0;
            m_ThreadData->m_CurrOperation = 0;
            m_ThreadData->m_MaxDataOffset = 0;
            m_ThreadData->m_MaxOperation = 0;
        }
    }
}

#endif // ENABLE_HYBRID_RENDERER_V2
