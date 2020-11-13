#define DEBUG_LOG_HYBRID_V2
// #define DEBUG_LOG_CHUNK_CHANGES
// #define DEBUG_LOG_TOP_LEVEL
// #define DEBUG_LOG_BATCHES
// #define DEBUG_LOG_BATCH_UPDATES
// #define DEBUG_LOG_CHUNKS
// #define DEBUG_LOG_INVALID_CHUNKS
// #define DEBUG_LOG_UPLOADS
// #define DEBUG_LOG_PROPERTIES
// #define DEBUG_LOG_OVERRIDES
// #define DEBUG_LOG_VISIBLE_INSTANCES
// #define DEBUG_LOG_MATERIAL_PROPERTIES
// #define DEBUG_LOG_MEMORY_USAGE
// #define PROFILE_BURST_JOB_INTERNALS

#if UNITY_EDITOR || DEBUG_LOG_OVERRIDES
#define USE_PROPERTY_ASSERTS
#endif

#if UNITY_EDITOR
#define USE_PICKING_MATRICES
#endif

// Define this to remove the performance overhead of error material usage
// #define DISABLE_HYBRID_ERROR_MATERIAL

// Assert that V2 requirements are met if it's enabled
#if ENABLE_HYBRID_RENDERER_V2
#if !UNITY_2020_1_OR_NEWER
#error Hybrid Renderer V2 requires Unity 2020.1 or newer.
#endif
#if !(HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
#error Hybrid Renderer V2 requires either HDRP 9.0.0 or URP 9.0.0 or newer.
#endif
#endif

#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
#define USE_UNITY_OCCLUSION
#endif

// TODO:
// - Minimize struct sizes to improve memory footprint and cache usage
// - What to do with FrozenRenderSceneTag / ForceLowLOD?
// - Precompute and optimize material property + chunk component matching as much as possible
// - Integrate new occlusion culling
// - PickableObject?

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
#if UNITY_2020_1_OR_NEWER
// This API only exists since 2020.1
using Unity.Rendering.HybridV2;
#endif

#if USE_UNITY_OCCLUSION
using Unity.Rendering.Occlusion;
#endif

namespace Unity.Rendering
{
    // Shared components that implement this interface and have a [MaterialProperty] attribute can override
    // DOTS instanced float4 material properties. When applicable (many entities have the same value), this
    // can have substantial memory savings over IComponentData overrides.
    // NOTE: Only float4 supported for now, this interface will be deprecated once unmanaged shared components
    // become available.
    public interface IHybridSharedComponentFloat4Override : ISharedComponentData
    {
        float4 GetFloat4OverrideData();
    }

    public struct HybridChunkInfo : IComponentData
    {
        public int InternalIndex;

        // Begin and end indices for component type metadata (modification version numbers, typeIndex values) in external arrays.
        public int ChunkTypesBegin;
        public int ChunkTypesEnd;

        public HybridChunkCullingData CullingData;
        public bool Valid;
    }

    // Entities with different PartitionValues will never be put into the same Hybrid Renderer batch
    // with each other. Allows forcing entities to separate batches for e.g. draw call sorting purposes.
    // Entities that have no PartitionValue are treated as having a PartitionValue of 0.
    public struct HybridBatchPartition : ISharedComponentData
    {
        public ulong PartitionValue;
    }

    // Describes a single set of data to be uploaded from the CPU to the GPU during this frame.
    // The operations are collected up front so their total size can be known for buffer allocation
    // purposes, and for effectively load balancing the upload memcpy work.
    public unsafe struct GpuUploadOperation
    {
        public enum UploadOperationKind
        {
            Memcpy, // raw upload of a byte block to the GPU
            SOAMatrixUpload3x4, // upload matrices from CPU, invert on GPU, write in SoA arrays, 3x4 destination
            SOAMatrixUpload4x4, // upload matrices from CPU, invert on GPU, write in SoA arrays, 4x4 destination
            // TwoMatrixUpload, // upload matrices from CPU, write them and their inverses to GPU (for transform sharing branch)
        }

        // Which kind of upload operation this is
        public UploadOperationKind Kind;
        // Pointer to source data, whether raw byte data or float4x4 matrices
        public void* Src;
        // GPU offset to start writing destination data in
        public int DstOffset;
        // GPU offset to start writing any inverse matrices in, if applicable
        public int DstOffsetInverse;
        // Size in bytes for raw operations, size in whole matrices for matrix operations
        public int Size;
        // Destination stride in bytes between matrices in matrix operations
        public int Stride;

        // Raw uploads require their size in bytes from the upload buffer.
        // Matrix operations require a single 48-byte matrix per matrix.
        public int BytesRequiredInUploadBuffer => (Kind == UploadOperationKind.Memcpy)
            ? Size
            : (Size * UnsafeUtility.SizeOf<float3x4>());
    }

    // Describes a GPU blitting operation (= same bytes replicated over a larger area) used
    // to write default values for Hybrid batches
    public struct DefaultValueBlitDescriptor
    {
        public float4x4 DefaultValue;
        public uint DestinationOffset;
        public uint ValueSizeBytes;
        public uint Count;

        public int BytesRequiredInUploadBuffer => (int)(ValueSizeBytes * Count);
    }

    public struct WorldToLocal_Tag : IComponentData {}

    // Burst currently does not support atomic AND and OR. Use compare-and-exchange based
    // workarounds until it does.
    internal struct AtomicHelpers
    {
        public const uint kNumBitsInLong = sizeof(long) * 8;

        public static void IndexToQwIndexAndMask(int index, out int qwIndex, out long mask)
        {
            uint i = (uint)index;
            uint qw = i / kNumBitsInLong;
            uint shift = i % kNumBitsInLong;

            qwIndex = (int)qw;
            mask = 1L << (int)shift;
        }

        public static unsafe long AtomicAnd(long* qwords, int index, long value)
        {
            // TODO: Replace this with atomic AND once it is available
            long currentValue = System.Threading.Interlocked.Read(ref qwords[index]);
            for (;;)
            {
                // If the AND wouldn't change any bits, no need to issue the atomic
                if ((currentValue & value) == currentValue)
                    return currentValue;

                long newValue = currentValue & value;
                long prevValue =
                    System.Threading.Interlocked.CompareExchange(ref qwords[index], newValue, currentValue);

                // If the value was equal to the expected value, we know that our atomic went through
                if (prevValue == currentValue)
                    return prevValue;

                currentValue = prevValue;
            }
        }

        public static unsafe long AtomicOr(long* qwords, int index, long value)
        {
            // TODO: Replace this with atomic OR once it is available
            long currentValue = System.Threading.Interlocked.Read(ref qwords[index]);
            for (;;)
            {
                // If the OR wouldn't change any bits, no need to issue the atomic
                if ((currentValue | value) == currentValue)
                    return currentValue;

                long newValue = currentValue | value;
                long prevValue =
                    System.Threading.Interlocked.CompareExchange(ref qwords[index], newValue, currentValue);

                // If the value was equal to the expected value, we know that our atomic went through
                if (prevValue == currentValue)
                    return prevValue;

                currentValue = prevValue;
            }
        }

        public static unsafe float AtomicMin(float* floats, int index, float value)
        {
            float currentValue = floats[index];

            // Never propagate NaNs to memory
            if (float.IsNaN(value))
                return currentValue;

            int* floatsAsInts = (int*) floats;
            int valueAsInt = math.asint(value);

            // Do the CAS operations as ints to avoid problems with NaNs
            for (;;)
            {
                // If currentValue is NaN, this comparison will fail
                if (currentValue <= value)
                    return currentValue;

                int currentValueAsInt = math.asint(currentValue);

                int newValue = valueAsInt;
                int prevValue = System.Threading.Interlocked.CompareExchange(ref floatsAsInts[index], newValue, currentValueAsInt);
                float prevValueAsFloat = math.asfloat(prevValue);

                // If the value was equal to the expected value, we know that our atomic went through
                // NOTE: This comparison MUST be an integer comparison, as otherwise NaNs
                // would result in an infinite loop
                if (prevValue == currentValueAsInt)
                    return prevValueAsFloat;

                currentValue = prevValueAsFloat;
            }
        }

        public static unsafe float AtomicMax(float* floats, int index, float value)
        {
            float currentValue = floats[index];

            // Never propagate NaNs to memory
            if (float.IsNaN(value))
                return currentValue;

            int* floatsAsInts = (int*) floats;
            int valueAsInt = math.asint(value);

            // Do the CAS operations as ints to avoid problems with NaNs
            for (;;)
            {
                // If currentValue is NaN, this comparison will fail
                if (currentValue >= value)
                    return currentValue;

                int currentValueAsInt = math.asint(currentValue);

                int newValue = valueAsInt;
                int prevValue = System.Threading.Interlocked.CompareExchange(ref floatsAsInts[index], newValue, currentValueAsInt);
                float prevValueAsFloat = math.asfloat(prevValue);

                // If the value was equal to the expected value, we know that our atomic went through
                // NOTE: This comparison MUST be an integer comparison, as otherwise NaNs
                // would result in an infinite loop
                if (prevValue == currentValueAsInt)
                    return prevValueAsFloat;

                currentValue = prevValueAsFloat;
            }
        }
    }

    public struct MaterialPropertyDefaultValue
    {
        public readonly float4x4 Value;
        public readonly bool Nonzero;

        public MaterialPropertyDefaultValue(float f)
        {
            Value = default;
            Value[0] = f;
            Nonzero = f != 0;
        }

        public MaterialPropertyDefaultValue(float4 v)
        {
            Value = default;
            Value.c0 = v;
            Nonzero = !v.Equals(float4.zero);
        }

        public MaterialPropertyDefaultValue(float4x4 m)
        {
            Value = m;
            Nonzero = !m.Equals(float4x4.zero);
        }

        public static implicit operator MaterialPropertyDefaultValue(float v) => new MaterialPropertyDefaultValue(v);
        public static implicit operator MaterialPropertyDefaultValue(float4 v) => new MaterialPropertyDefaultValue(v);
        public static implicit operator MaterialPropertyDefaultValue(float4x4 v) => new MaterialPropertyDefaultValue(v);
    }

    [BurstCompile]
    internal struct InitializeUnreferencedIndicesScatterJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> ExistingInternalIndices;
        public NativeArray<long> UnreferencedInternalIndices;

        public unsafe void Execute(int index)
        {
            int internalIndex = ExistingInternalIndices[index];

            AtomicHelpers.IndexToQwIndexAndMask(internalIndex, out int qw, out long mask);

            Debug.Assert(qw < UnreferencedInternalIndices.Length, "Batch index out of bounds");

            AtomicHelpers.AtomicOr((long*)UnreferencedInternalIndices.GetUnsafePtr(), qw, mask);
        }
    }

    [BurstCompile]
    internal struct HybridChunkUpdater
    {
        public const uint kFloatsPerAABB = 6;
        public const int kMinX = 0;
        public const int kMinY = 1;
        public const int kMinZ = 2;
        public const int kMaxX = 3;
        public const int kMaxY = 4;
        public const int kMaxZ = 5;

        public ComponentTypeCache.BurstCompatibleTypeArray ComponentTypes;

        [NativeDisableParallelForRestriction]
        public NativeArray<long> UnreferencedInternalIndices;
        [NativeDisableParallelForRestriction]
        public NativeArray<long> BatchRequiresUpdates;
        [NativeDisableParallelForRestriction]
        public NativeArray<long> BatchHadMovingEntities;

        [NativeDisableParallelForRestriction]
        public NativeArray<ArchetypeChunk> NewChunks;
        [NativeDisableParallelForRestriction]
        public NativeArray<int> NumNewChunks;

        [NativeDisableParallelForRestriction]
        [ReadOnly]
        public NativeArray<ChunkProperty> ChunkProperties;

        [NativeDisableParallelForRestriction]
        [ReadOnly]
        public NativeArray<BatchMotionInfo> BatchMotionInfos;

        [NativeDisableParallelForRestriction]
        public NativeArray<float> BatchAABBs;
        public MinMaxAABB ThreadLocalAABB;

#if USE_PICKING_MATRICES
        [NativeDisableParallelForRestriction]
        [ReadOnly]
        public NativeArray<IntPtr> BatchPickingMatrices;
#endif

        [NativeDisableParallelForRestriction]
        public NativeArray<GpuUploadOperation> GpuUploadOperations;
        public NativeArray<int> NumGpuUploadOperations;

        public uint LastSystemVersion;
        public int PreviousBatchIndex;

        public int LocalToWorldType;
        public int WorldToLocalType;
        public int PrevLocalToWorldType;
        public int PrevWorldToLocalType;

#if PROFILE_BURST_JOB_INTERNALS
        public ProfilerMarker ProfileAddUpload;
        public ProfilerMarker ProfilePickingMatrices;
#endif

        public unsafe void MarkBatchForUpdates(int internalIndex, bool entitiesMoved)
        {
            AtomicHelpers.IndexToQwIndexAndMask(internalIndex, out int qw, out long mask);
            Debug.Assert(qw < BatchRequiresUpdates.Length && qw < BatchHadMovingEntities.Length,
                "Batch index out of bounds");

            var motionInfo = BatchMotionInfos[internalIndex];
            bool mustDisableMotionVectors = motionInfo.MotionVectorFlagSet && !entitiesMoved;

            // If entities moved, we always update the batch since bounds must be updated.
            // If no entities moved, we only update the batch if it requires motion vector disable.
            if (entitiesMoved || mustDisableMotionVectors)
                AtomicHelpers.AtomicOr((long*)BatchRequiresUpdates.GetUnsafePtr(), qw, mask);

            if (entitiesMoved)
                AtomicHelpers.AtomicOr((long*)BatchHadMovingEntities.GetUnsafePtr(), qw, mask);
        }

        unsafe void MarkBatchAsReferenced(int internalIndex)
        {
            // If the batch is referenced, remove it from the unreferenced bitfield

            AtomicHelpers.IndexToQwIndexAndMask(internalIndex, out int qw, out long mask);

            Debug.Assert(qw < UnreferencedInternalIndices.Length, "Batch index out of bounds");

            AtomicHelpers.AtomicAnd(
                (long*)UnreferencedInternalIndices.GetUnsafePtr(),
                qw,
                ~mask);
        }

        public void ProcessChunk(in HybridChunkInfo chunkInfo, ArchetypeChunk chunk, ChunkWorldRenderBounds chunkBounds)
        {
#if DEBUG_LOG_CHUNKS
            Debug.Log($"HybridChunkUpdater.ProcessChunk(internalBatchIndex: {chunkInfo.InternalIndex}, valid: {chunkInfo.Valid}, count: {chunk.Count}, chunk: {chunk.GetHashCode()})");
#endif

            if (chunkInfo.Valid)
                ProcessValidChunk(chunkInfo, chunk, chunkBounds.Value, false);
            else
                DeferNewChunk(chunkInfo, chunk);
        }

        public unsafe void DeferNewChunk(in HybridChunkInfo chunkInfo, ArchetypeChunk chunk)
        {
            if (chunk.Archetype.Prefab || chunk.Archetype.Disabled)
                return;

            int* numNewChunks = (int*)NumNewChunks.GetUnsafePtr();
            int iPlus1 = System.Threading.Interlocked.Add(ref numNewChunks[0], 1);
            int i = iPlus1 - 1; // C# Interlocked semantics are weird
            Debug.Assert(i < NewChunks.Length, "Out of space in the NewChunks buffer");
            NewChunks[i] = chunk;
        }

        public unsafe void ProcessValidChunk(in HybridChunkInfo chunkInfo, ArchetypeChunk chunk,
            MinMaxAABB chunkAABB, bool isNewChunk)
        {
            if (!isNewChunk)
                MarkBatchAsReferenced(chunkInfo.InternalIndex);

            int internalIndex = chunkInfo.InternalIndex;
            UpdateBatchAABB(internalIndex, chunkAABB);

            bool structuralChanges = chunk.DidOrderChange(LastSystemVersion);

            var dstOffsetWorldToLocal = -1;
            var dstOffsetPrevWorldToLocal = -1;

            fixed(DynamicComponentTypeHandle* fixedT0 = &ComponentTypes.t0)
            {
                for (int i = chunkInfo.ChunkTypesBegin; i < chunkInfo.ChunkTypesEnd; ++i)
                {
                    var chunkProperty = ChunkProperties[i];
                    var type = chunkProperty.ComponentTypeIndex;
                    if (type == WorldToLocalType)
                        dstOffsetWorldToLocal = chunkProperty.GPUDataBegin;
                    else if (type == PrevWorldToLocalType)
                        dstOffsetPrevWorldToLocal = chunkProperty.GPUDataBegin;
                }

                for (int i = chunkInfo.ChunkTypesBegin; i < chunkInfo.ChunkTypesEnd; ++i)
                {
                    var chunkProperty = ChunkProperties[i];
                    var type = ComponentTypes.Type(fixedT0, chunkProperty.ComponentTypeIndex);

                    var chunkType = chunkProperty.ComponentTypeIndex;
                    var isLocalToWorld = chunkType == LocalToWorldType;
                    var isWorldToLocal = chunkType == WorldToLocalType;
                    var isPrevLocalToWorld = chunkType == PrevLocalToWorldType;
                    var isPrevWorldToLocal = chunkType == PrevWorldToLocalType;

                    var skipComponent = (isWorldToLocal || isPrevWorldToLocal);

                    bool componentChanged = chunk.DidChange(type, LastSystemVersion);
                    bool copyComponentData = (isNewChunk || structuralChanges || componentChanged) && !skipComponent;

                    if (copyComponentData)
                    {
#if DEBUG_LOG_PROPERTIES
                        Debug.Log($"UpdateChunkProperty(internalBatchIndex: {chunkInfo.InternalIndex}, property: {i}, elementSize: {chunkProperty.ValueSizeBytesCPU})");
#endif

                        var src = chunk.GetDynamicComponentDataArrayReinterpret<int>(type,
                            chunkProperty.ValueSizeBytesCPU);

#if PROFILE_BURST_JOB_INTERNALS
                        ProfileAddUpload.Begin();
#endif

                        int sizeBytes = (int)((uint)chunk.Count * (uint)chunkProperty.ValueSizeBytesCPU);
                        var srcPtr = src.GetUnsafeReadOnlyPtr();
                        var dstOffset = chunkProperty.GPUDataBegin;
                        if (isLocalToWorld || isPrevLocalToWorld)
                        {
                            var numMatrices = sizeBytes / sizeof(float4x4);
                            AddMatrixUpload(
                                srcPtr,
                                numMatrices,
                                dstOffset,
                                isLocalToWorld ? dstOffsetWorldToLocal : dstOffsetPrevWorldToLocal,
                                (chunkProperty.ValueSizeBytesCPU == 4 * 4 * 3)
                                ? ThreadedSparseUploader.MatrixType.MatrixType3x4
                                : ThreadedSparseUploader.MatrixType.MatrixType4x4,
                                (chunkProperty.ValueSizeBytesGPU == 4 * 4 * 3)
                                ? ThreadedSparseUploader.MatrixType.MatrixType3x4
                                : ThreadedSparseUploader.MatrixType.MatrixType4x4);

#if USE_PICKING_MATRICES
                            // If picking support is enabled, also copy the LocalToWorld matrices
                            // to the traditional instancing matrix array. This should be thread safe
                            // because the related Burst jobs run during DOTS system execution, and
                            // are guaranteed to have finished before rendering starts.
                            if (isLocalToWorld)
                            {
#if PROFILE_BURST_JOB_INTERNALS
                                ProfilePickingMatrices.Begin();
#endif
                                float4x4* batchPickingMatrices = (float4x4*)BatchPickingMatrices[internalIndex];
                                int chunkOffsetInBatch = chunkInfo.CullingData.BatchOffset;
                                UnsafeUtility.MemCpy(
                                    batchPickingMatrices + chunkOffsetInBatch,
                                    srcPtr,
                                    sizeBytes);
#if PROFILE_BURST_JOB_INTERNALS
                                ProfilePickingMatrices.End();
#endif
                            }
#endif
                        }
                        else
                        {
                            AddUpload(
                                srcPtr,
                                sizeBytes,
                                dstOffset);
                        }
#if PROFILE_BURST_JOB_INTERNALS
                        ProfileAddUpload.End();
#endif
                    }
                }
            }
        }

        private unsafe void AddUpload(void* srcPtr, int sizeBytes, int dstOffset)
        {
            int* numGpuUploadOperations = (int*) NumGpuUploadOperations.GetUnsafePtr();
            int index = System.Threading.Interlocked.Add(ref numGpuUploadOperations[0], 1) - 1;

            if (index < GpuUploadOperations.Length)
            {
                GpuUploadOperations[index] = new GpuUploadOperation
                {
                    Kind = GpuUploadOperation.UploadOperationKind.Memcpy,
                    Src = srcPtr,
                    DstOffset = dstOffset,
                    DstOffsetInverse = -1,
                    Size = sizeBytes,
                    Stride = 0,
                };
            }
            else
            {
                // Debug.Assert(false, "Maximum amount of GPU upload operations exceeded");
            }
        }

        private unsafe void AddMatrixUpload(
            void* srcPtr,
            int numMatrices,
            int dstOffset,
            int dstOffsetInverse,
            ThreadedSparseUploader.MatrixType matrixTypeCpu,
            ThreadedSparseUploader.MatrixType matrixTypeGpu)
        {
            int* numGpuUploadOperations = (int*) NumGpuUploadOperations.GetUnsafePtr();
            int index = System.Threading.Interlocked.Add(ref numGpuUploadOperations[0], 1) - 1;

            if (index < GpuUploadOperations.Length)
            {
                GpuUploadOperations[index] = new GpuUploadOperation
                {
                    Kind = (matrixTypeGpu == ThreadedSparseUploader.MatrixType.MatrixType3x4)
                        ? GpuUploadOperation.UploadOperationKind.SOAMatrixUpload3x4
                        : GpuUploadOperation.UploadOperationKind.SOAMatrixUpload4x4,
                    Src = srcPtr,
                    DstOffset = dstOffset,
                    DstOffsetInverse = dstOffsetInverse,
                    Size = numMatrices,
                    Stride = 0,
                };
            }
            else
            {
                // Debug.Assert(false, "Maximum amount of GPU upload operations exceeded");
            }
        }

        private void UpdateBatchAABB(int internalIndex, MinMaxAABB chunkAABB)
        {
            // As long as we keep processing chunks that belong to the same batch,
            // we can keep accumulating a thread local AABB cheaply.
            // Once we encounter a different batch, we need to "flush" the thread
            // local version to the global one with atomics.
            bool sameBatchAsPrevious = internalIndex == PreviousBatchIndex;

            if (sameBatchAsPrevious)
            {
                ThreadLocalAABB.Encapsulate(chunkAABB);
            }
            else
            {
                CommitBatchAABB();
                ThreadLocalAABB = chunkAABB;
                PreviousBatchIndex = internalIndex;
            }
        }

        private unsafe void CommitBatchAABB()
        {
            bool validThreadLocalAABB = PreviousBatchIndex >= 0;
            if (!validThreadLocalAABB)
                return;

            int internalIndex = PreviousBatchIndex;
            var aabb = ThreadLocalAABB;

            int aabbIndex = (int)(((uint)internalIndex) * kFloatsPerAABB);
            float* aabbFloats = (float*)BatchAABBs.GetUnsafePtr();
            AtomicHelpers.AtomicMin(aabbFloats, aabbIndex + kMinX, aabb.Min.x);
            AtomicHelpers.AtomicMin(aabbFloats, aabbIndex + kMinY, aabb.Min.y);
            AtomicHelpers.AtomicMin(aabbFloats, aabbIndex + kMinZ, aabb.Min.z);
            AtomicHelpers.AtomicMax(aabbFloats, aabbIndex + kMaxX, aabb.Max.x);
            AtomicHelpers.AtomicMax(aabbFloats, aabbIndex + kMaxY, aabb.Max.y);
            AtomicHelpers.AtomicMax(aabbFloats, aabbIndex + kMaxZ, aabb.Max.z);

            PreviousBatchIndex = -1;
        }

        public void FinishExecute()
        {
            CommitBatchAABB();
        }
    }

    [BurstCompile]
    internal struct UpdateAllHybridChunksJob : IJobChunk
    {
        [ReadOnly] public ComponentTypeHandle<HybridChunkInfo> HybridChunkInfo;
        [ReadOnly] public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkWorldRenderBounds;
        [ReadOnly] public ComponentTypeHandle<ChunkHeader> ChunkHeader;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorld;
        [ReadOnly] public ComponentTypeHandle<LODRange> LodRange;
        [ReadOnly] public ComponentTypeHandle<RootLODRange> RootLodRange;
        [ReadOnly] public SharedComponentTypeHandle<RenderMesh> RenderMesh;
        public HybridChunkUpdater HybridChunkUpdater;

        public void Execute(ArchetypeChunk metaChunk, int chunkIndex, int firstEntityIndex)
        {
            // metaChunk is the chunk which contains the meta entities (= entities holding the chunk components) for the actual chunks

            var hybridChunkInfos = metaChunk.GetNativeArray(HybridChunkInfo);
            var chunkHeaders = metaChunk.GetNativeArray(ChunkHeader);
            var chunkBoundsArray = metaChunk.GetNativeArray(ChunkWorldRenderBounds);

            for (int i = 0; i < metaChunk.Count; ++i)
            {
                var chunkInfo = hybridChunkInfos[i];
                var chunkHeader = chunkHeaders[i];

                var chunk = chunkHeader.ArchetypeChunk;

                // Skip chunks that for some reason have HybridChunkInfo, but don't have the
                // other required components. This should normally not happen, but can happen
                // if the user manually deletes some components after the fact.
                bool hasRenderMesh = chunk.Has(RenderMesh);
                bool hasLocalToWorld = chunk.Has(LocalToWorld);
                if (!hasRenderMesh || !hasLocalToWorld)
                    continue;

                ChunkWorldRenderBounds chunkBounds = chunkBoundsArray[i];

                bool isNewChunk = !chunkInfo.Valid;
                bool localToWorldChange = chunk.DidChange(LocalToWorld, HybridChunkUpdater.LastSystemVersion);

                // When LOD ranges change, we must reset the movement grace to avoid using stale data
                bool lodRangeChange =
                    chunk.DidOrderChange(HybridChunkUpdater.LastSystemVersion) |
                    chunk.DidChange(LodRange, HybridChunkUpdater.LastSystemVersion) |
                    chunk.DidChange(RootLodRange, HybridChunkUpdater.LastSystemVersion);

                if (lodRangeChange)
                    chunkInfo.CullingData.MovementGraceFixed16 = 0;

                // Don't mark new chunks for updates here, they will be handled later when they have valid batch indices.
                if (!isNewChunk)
                    HybridChunkUpdater.MarkBatchForUpdates(chunkInfo.InternalIndex, localToWorldChange);

                HybridChunkUpdater.ProcessChunk(chunkInfo, chunk, chunkBounds);
            }

            HybridChunkUpdater.FinishExecute();
        }
    }

    [BurstCompile]
    internal struct UpdateNewHybridChunksJob : IJobParallelFor
    {
        [ReadOnly] public ComponentTypeHandle<HybridChunkInfo> HybridChunkInfo;
        [ReadOnly] public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkWorldRenderBounds;

        public NativeArray<ArchetypeChunk> NewChunks;
        public HybridChunkUpdater HybridChunkUpdater;

        public void Execute(int index)
        {
            var chunk = NewChunks[index];
            var chunkInfo = chunk.GetChunkComponentData(HybridChunkInfo);

            ChunkWorldRenderBounds chunkBounds = chunk.GetChunkComponentData(ChunkWorldRenderBounds);

            Debug.Assert(chunkInfo.Valid, "Attempted to process a chunk with uninitialized Hybrid chunk info");
            HybridChunkUpdater.MarkBatchForUpdates(chunkInfo.InternalIndex, true);
            HybridChunkUpdater.ProcessValidChunk(chunkInfo, chunk, chunkBounds.Value, true);
            HybridChunkUpdater.FinishExecute();
        }
    }

    [BurstCompile]
    internal unsafe struct ExecuteGpuUploads : IJobParallelFor
    {
        [ReadOnly] public NativeArray<GpuUploadOperation> GpuUploadOperations;
        public ThreadedSparseUploader ThreadedSparseUploader;

        public void Execute(int index)
        {
            var uploadOperation = GpuUploadOperations[index];

            switch (uploadOperation.Kind)
            {
                case GpuUploadOperation.UploadOperationKind.Memcpy:
                    ThreadedSparseUploader.AddUpload(
                        uploadOperation.Src,
                        uploadOperation.Size,
                        uploadOperation.DstOffset);
                    break;
                case GpuUploadOperation.UploadOperationKind.SOAMatrixUpload3x4:
                case GpuUploadOperation.UploadOperationKind.SOAMatrixUpload4x4:
                    ThreadedSparseUploader.AddMatrixUpload(
                        uploadOperation.Src,
                        uploadOperation.Size,
                        uploadOperation.DstOffset,
                        uploadOperation.DstOffsetInverse,
                        ThreadedSparseUploader.MatrixType.MatrixType4x4,
                        (uploadOperation.Kind == GpuUploadOperation.UploadOperationKind.SOAMatrixUpload3x4)
                        ? ThreadedSparseUploader.MatrixType.MatrixType3x4
                        : ThreadedSparseUploader.MatrixType.MatrixType4x4);
                    break;
                default:
                    break;
            }
        }
    }

    [BurstCompile]
    internal unsafe struct UploadBlitJob : IJobParallelFor
    {
        [ReadOnly] public NativeList<DefaultValueBlitDescriptor> BlitList;
        public ThreadedSparseUploader ThreadedSparseUploader;

        public void Execute(int index)
        {
            DefaultValueBlitDescriptor blit = BlitList[index];
            ThreadedSparseUploader.AddUpload(
                &blit.DefaultValue,
                (int)blit.ValueSizeBytes,
                (int)blit.DestinationOffset,
                (int)blit.Count);
        }
    }


    internal struct ChunkProperty
    {
        public int ComponentTypeIndex;
        public int ValueSizeBytesCPU;
        public int ValueSizeBytesGPU;
        public int GPUDataBegin;
    }

    public struct HybridChunkCullingData
    {
        public const int kFlagHasLodData = 1 << 0;

        public const int kFlagInstanceCulling = 1 << 1;
        // size  // start - end offset
        public short BatchOffset; //  2     2 - 4
        public short StartIndex; // 2     4 - 6
        public short Visible; // 2     6 - 8
        public ushort MovementGraceFixed16; //  2     8 - 10
        public byte Flags; //  1     10 - 11
        public byte ForceLowLODPrevious; //  1     11 - 12
        public ChunkInstanceLodEnabled InstanceLodEnableds; // 16     12 - 20
    }

    // Helper to only call GetDynamicComponentTypeHandle once per type per frame
    internal struct ComponentTypeCache
    {
        internal NativeHashMap<int, int> UsedTypes;

        // Re-populated each frame with fresh objects for each used type.
        // Use C# array so we can hold SafetyHandles without problems.
        internal DynamicComponentTypeHandle[] TypeDynamics;
        internal int MaxIndex;

        public ComponentTypeCache(int initialCapacity) : this()
        {
            Reset(initialCapacity);
        }

        public void Reset(int capacity = 0)
        {
            Dispose();
            UsedTypes = new NativeHashMap<int, int>(capacity, Allocator.Persistent);
            MaxIndex = 0;
        }

        public void Dispose()
        {
            if (UsedTypes.IsCreated) UsedTypes.Dispose();
            TypeDynamics = null;
        }

        public int UsedTypeCount => UsedTypes.Count();

        public void UseType(int typeIndex)
        {
            // Use indices without flags so we have a nice compact range
            int i = GetArrayIndex(typeIndex);
            Debug.Assert(!UsedTypes.ContainsKey(i) || UsedTypes[i] == typeIndex,
                "typeIndex is not consistent with its stored array index");
            UsedTypes[i] = typeIndex;
            MaxIndex = math.max(i, MaxIndex);
        }

        public void FetchTypeHandles(ComponentSystemBase componentSystem)
        {
            var types = UsedTypes.GetKeyValueArrays(Allocator.Temp);

            if (TypeDynamics == null || TypeDynamics.Length < MaxIndex + 1)
                // Allocate according to Capacity so we grow with the same geometric formula as NativeList
                TypeDynamics = new DynamicComponentTypeHandle[MaxIndex + 1];

            ref var keys = ref types.Keys;
            ref var values = ref types.Values;
            int numTypes = keys.Length;
            for (int i = 0; i < numTypes; ++i)
            {
                int arrayIndex = keys[i];
                int typeIndex = values[i];
                TypeDynamics[arrayIndex] = componentSystem.GetDynamicComponentTypeHandle(
                    ComponentType.ReadOnly(typeIndex));
            }

            types.Dispose();
        }

        public static int GetArrayIndex(int typeIndex) => typeIndex & TypeManager.ClearFlagsMask;

        public DynamicComponentTypeHandle Type(int typeIndex)
        {
            return TypeDynamics[GetArrayIndex(typeIndex)];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BurstCompatibleTypeArray
        {
            public const int kMaxTypes = 128;

            [NativeDisableParallelForRestriction] public NativeArray<int> TypeIndexToArrayIndex;

            [ReadOnly] public DynamicComponentTypeHandle t0;
            [ReadOnly] public DynamicComponentTypeHandle t1;
            [ReadOnly] public DynamicComponentTypeHandle t2;
            [ReadOnly] public DynamicComponentTypeHandle t3;
            [ReadOnly] public DynamicComponentTypeHandle t4;
            [ReadOnly] public DynamicComponentTypeHandle t5;
            [ReadOnly] public DynamicComponentTypeHandle t6;
            [ReadOnly] public DynamicComponentTypeHandle t7;
            [ReadOnly] public DynamicComponentTypeHandle t8;
            [ReadOnly] public DynamicComponentTypeHandle t9;
            [ReadOnly] public DynamicComponentTypeHandle t10;
            [ReadOnly] public DynamicComponentTypeHandle t11;
            [ReadOnly] public DynamicComponentTypeHandle t12;
            [ReadOnly] public DynamicComponentTypeHandle t13;
            [ReadOnly] public DynamicComponentTypeHandle t14;
            [ReadOnly] public DynamicComponentTypeHandle t15;
            [ReadOnly] public DynamicComponentTypeHandle t16;
            [ReadOnly] public DynamicComponentTypeHandle t17;
            [ReadOnly] public DynamicComponentTypeHandle t18;
            [ReadOnly] public DynamicComponentTypeHandle t19;
            [ReadOnly] public DynamicComponentTypeHandle t20;
            [ReadOnly] public DynamicComponentTypeHandle t21;
            [ReadOnly] public DynamicComponentTypeHandle t22;
            [ReadOnly] public DynamicComponentTypeHandle t23;
            [ReadOnly] public DynamicComponentTypeHandle t24;
            [ReadOnly] public DynamicComponentTypeHandle t25;
            [ReadOnly] public DynamicComponentTypeHandle t26;
            [ReadOnly] public DynamicComponentTypeHandle t27;
            [ReadOnly] public DynamicComponentTypeHandle t28;
            [ReadOnly] public DynamicComponentTypeHandle t29;
            [ReadOnly] public DynamicComponentTypeHandle t30;
            [ReadOnly] public DynamicComponentTypeHandle t31;
            [ReadOnly] public DynamicComponentTypeHandle t32;
            [ReadOnly] public DynamicComponentTypeHandle t33;
            [ReadOnly] public DynamicComponentTypeHandle t34;
            [ReadOnly] public DynamicComponentTypeHandle t35;
            [ReadOnly] public DynamicComponentTypeHandle t36;
            [ReadOnly] public DynamicComponentTypeHandle t37;
            [ReadOnly] public DynamicComponentTypeHandle t38;
            [ReadOnly] public DynamicComponentTypeHandle t39;
            [ReadOnly] public DynamicComponentTypeHandle t40;
            [ReadOnly] public DynamicComponentTypeHandle t41;
            [ReadOnly] public DynamicComponentTypeHandle t42;
            [ReadOnly] public DynamicComponentTypeHandle t43;
            [ReadOnly] public DynamicComponentTypeHandle t44;
            [ReadOnly] public DynamicComponentTypeHandle t45;
            [ReadOnly] public DynamicComponentTypeHandle t46;
            [ReadOnly] public DynamicComponentTypeHandle t47;
            [ReadOnly] public DynamicComponentTypeHandle t48;
            [ReadOnly] public DynamicComponentTypeHandle t49;
            [ReadOnly] public DynamicComponentTypeHandle t50;
            [ReadOnly] public DynamicComponentTypeHandle t51;
            [ReadOnly] public DynamicComponentTypeHandle t52;
            [ReadOnly] public DynamicComponentTypeHandle t53;
            [ReadOnly] public DynamicComponentTypeHandle t54;
            [ReadOnly] public DynamicComponentTypeHandle t55;
            [ReadOnly] public DynamicComponentTypeHandle t56;
            [ReadOnly] public DynamicComponentTypeHandle t57;
            [ReadOnly] public DynamicComponentTypeHandle t58;
            [ReadOnly] public DynamicComponentTypeHandle t59;
            [ReadOnly] public DynamicComponentTypeHandle t60;
            [ReadOnly] public DynamicComponentTypeHandle t61;
            [ReadOnly] public DynamicComponentTypeHandle t62;
            [ReadOnly] public DynamicComponentTypeHandle t63;
            [ReadOnly] public DynamicComponentTypeHandle t64;
            [ReadOnly] public DynamicComponentTypeHandle t65;
            [ReadOnly] public DynamicComponentTypeHandle t66;
            [ReadOnly] public DynamicComponentTypeHandle t67;
            [ReadOnly] public DynamicComponentTypeHandle t68;
            [ReadOnly] public DynamicComponentTypeHandle t69;
            [ReadOnly] public DynamicComponentTypeHandle t70;
            [ReadOnly] public DynamicComponentTypeHandle t71;
            [ReadOnly] public DynamicComponentTypeHandle t72;
            [ReadOnly] public DynamicComponentTypeHandle t73;
            [ReadOnly] public DynamicComponentTypeHandle t74;
            [ReadOnly] public DynamicComponentTypeHandle t75;
            [ReadOnly] public DynamicComponentTypeHandle t76;
            [ReadOnly] public DynamicComponentTypeHandle t77;
            [ReadOnly] public DynamicComponentTypeHandle t78;
            [ReadOnly] public DynamicComponentTypeHandle t79;
            [ReadOnly] public DynamicComponentTypeHandle t80;
            [ReadOnly] public DynamicComponentTypeHandle t81;
            [ReadOnly] public DynamicComponentTypeHandle t82;
            [ReadOnly] public DynamicComponentTypeHandle t83;
            [ReadOnly] public DynamicComponentTypeHandle t84;
            [ReadOnly] public DynamicComponentTypeHandle t85;
            [ReadOnly] public DynamicComponentTypeHandle t86;
            [ReadOnly] public DynamicComponentTypeHandle t87;
            [ReadOnly] public DynamicComponentTypeHandle t88;
            [ReadOnly] public DynamicComponentTypeHandle t89;
            [ReadOnly] public DynamicComponentTypeHandle t90;
            [ReadOnly] public DynamicComponentTypeHandle t91;
            [ReadOnly] public DynamicComponentTypeHandle t92;
            [ReadOnly] public DynamicComponentTypeHandle t93;
            [ReadOnly] public DynamicComponentTypeHandle t94;
            [ReadOnly] public DynamicComponentTypeHandle t95;
            [ReadOnly] public DynamicComponentTypeHandle t96;
            [ReadOnly] public DynamicComponentTypeHandle t97;
            [ReadOnly] public DynamicComponentTypeHandle t98;
            [ReadOnly] public DynamicComponentTypeHandle t99;
            [ReadOnly] public DynamicComponentTypeHandle t100;
            [ReadOnly] public DynamicComponentTypeHandle t101;
            [ReadOnly] public DynamicComponentTypeHandle t102;
            [ReadOnly] public DynamicComponentTypeHandle t103;
            [ReadOnly] public DynamicComponentTypeHandle t104;
            [ReadOnly] public DynamicComponentTypeHandle t105;
            [ReadOnly] public DynamicComponentTypeHandle t106;
            [ReadOnly] public DynamicComponentTypeHandle t107;
            [ReadOnly] public DynamicComponentTypeHandle t108;
            [ReadOnly] public DynamicComponentTypeHandle t109;
            [ReadOnly] public DynamicComponentTypeHandle t110;
            [ReadOnly] public DynamicComponentTypeHandle t111;
            [ReadOnly] public DynamicComponentTypeHandle t112;
            [ReadOnly] public DynamicComponentTypeHandle t113;
            [ReadOnly] public DynamicComponentTypeHandle t114;
            [ReadOnly] public DynamicComponentTypeHandle t115;
            [ReadOnly] public DynamicComponentTypeHandle t116;
            [ReadOnly] public DynamicComponentTypeHandle t117;
            [ReadOnly] public DynamicComponentTypeHandle t118;
            [ReadOnly] public DynamicComponentTypeHandle t119;
            [ReadOnly] public DynamicComponentTypeHandle t120;
            [ReadOnly] public DynamicComponentTypeHandle t121;
            [ReadOnly] public DynamicComponentTypeHandle t122;
            [ReadOnly] public DynamicComponentTypeHandle t123;
            [ReadOnly] public DynamicComponentTypeHandle t124;
            [ReadOnly] public DynamicComponentTypeHandle t125;
            [ReadOnly] public DynamicComponentTypeHandle t126;
            [ReadOnly] public DynamicComponentTypeHandle t127;

            // Need to accept &t0 as input, because 'fixed' must be in the callsite.
            public unsafe DynamicComponentTypeHandle Type(DynamicComponentTypeHandle* fixedT0,
                int typeIndex)
            {
                return fixedT0[TypeIndexToArrayIndex[GetArrayIndex(typeIndex)]];
            }

            public void Dispose(JobHandle disposeDeps)
            {
                if (TypeIndexToArrayIndex.IsCreated) TypeIndexToArrayIndex.Dispose(disposeDeps);
            }
        }

        public unsafe BurstCompatibleTypeArray ToBurstCompatible(Allocator allocator)
        {
            BurstCompatibleTypeArray typeArray = default;

            Debug.Assert(UsedTypeCount > 0, "No types have been registered");
            Debug.Assert(UsedTypeCount <= BurstCompatibleTypeArray.kMaxTypes, "Maximum supported amount of types exceeded");

            typeArray.TypeIndexToArrayIndex = new NativeArray<int>(
                MaxIndex + 1,
                allocator,
                NativeArrayOptions.UninitializedMemory);
            ref var toArrayIndex = ref typeArray.TypeIndexToArrayIndex;

            // Use an index guaranteed to cause a crash on invalid indices
            uint GuaranteedCrashOffset = 0x80000000;
            for (int i = 0; i < toArrayIndex.Length; ++i)
                toArrayIndex[i] = (int)GuaranteedCrashOffset;

            var typeIndices = UsedTypes.GetValueArray(Allocator.Temp);
            int numTypes = math.min(typeIndices.Length, BurstCompatibleTypeArray.kMaxTypes);
            var fixedT0 = &typeArray.t0;

            for (int i = 0; i < numTypes; ++i)
            {
                int typeIndex = typeIndices[i];
                fixedT0[i] = Type(typeIndex);
                toArrayIndex[GetArrayIndex(typeIndex)] = i;
            }

            // TODO: Is there a way to avoid this?
            // We need valid type objects in each field.
            {
                var someType = Type(typeIndices[0]);
                for (int i = numTypes; i < BurstCompatibleTypeArray.kMaxTypes; ++i)
                    fixedT0[i] = someType;
            }

            typeIndices.Dispose();

            return typeArray;
        }
    }

    // Split these into a separate struct, since they must be updated, and it's hard
    // to efficiently update structs that you can't take pointers to
    struct BatchMotionInfo
    {
        public bool RequiresMotionVectorUpdates;
        public bool MotionVectorFlagSet;
    }

    /// <summary>
    /// Renders all Entities containing both RenderMesh and LocalToWorld components.
    /// </summary>
#if ENABLE_HYBRID_RENDERER_V2
    [ExecuteAlways]
    //@TODO: Necessary due to empty component group. When Component group and archetype chunks are unified this should be removed
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(UpdatePresentationSystemGroup))]
#else
    [DisableAutoCreation]
#endif
    public unsafe class HybridRendererSystem : JobComponentSystem
    {
        private ulong m_PersistentInstanceDataSize;

        private EntityQuery m_CullingJobDependencyGroup;
        private EntityQuery m_CullingGroup;
        private EntityQuery m_HybridRenderedQuery;

        private EntityQuery m_LodSelectGroup;

#if UNITY_EDITOR
        private EditorRenderData m_DefaultEditorRenderData = new EditorRenderData
        {SceneCullingMask = UnityEditor.SceneManagement.EditorSceneManager.DefaultSceneCullingMask};
#else
        private EditorRenderData m_DefaultEditorRenderData = new EditorRenderData { SceneCullingMask = ~0UL };
#endif

        const int kMaxBatchCount = 64 * 1024;
        const int kMaxEntitiesPerBatch = 1023; // C++ code is restricted to a certain maximum size
        const int kNumNewChunksPerThread = 1; // TODO: Tune this
        const int kNumScatteredIndicesPerThread = 8; // TODO: Tune this
        const int kNumGatheredIndicesPerThread = 128 * 8; // Two cache lines per thread
        const int kBuiltinCbufferIndex = 0;

        const int kMaxChunkMetadata = 1 * 1024 * 1024;
        const ulong kMaxGPUAllocatorMemory = 1024 * 1024 * 1024; // 1GiB of potential memory space
        const ulong kGPUBufferSizeInitial = 32 * 1024 * 1024;
        const ulong kGPUBufferSizeMax = 1023 * 1024 * 1024;
        const int kGPUUploaderChunkSize = 4 * 1024 * 1024;

        private enum BatchFlags
        {
            NeedMotionVectorPassFlag = 0x1
        };

        private JobHandle m_CullingJobDependency;
        private JobHandle m_LODDependency;
        private BatchRendererGroup m_BatchRendererGroup;

        private ComputeBuffer m_GPUPersistentInstanceData;
        private SparseUploader m_GPUUploader;
        private ThreadedSparseUploader m_ThreadedGPUUploader;
        private HeapAllocator m_GPUPersistentAllocator;
        private HeapBlock m_SharedZeroAllocation;

        private HeapAllocator m_ChunkMetadataAllocator;

        private NativeArray<BatchInfo> m_BatchInfos;
        private NativeArray<BatchMotionInfo> m_BatchMotionInfos;
#if USE_PICKING_MATRICES
        private NativeArray<IntPtr> m_BatchPickingMatrices;
#endif
        private NativeArray<ChunkProperty> m_ChunkProperties;
        private NativeHashMap<int, int> m_ExistingBatchInternalIndices;
        private ComponentTypeCache m_ComponentTypeCache;

        private NativeArray<float> m_BatchAABBs;

        private NativeArray<int> m_InternalToExternalIds;
        private NativeArray<int> m_ExternalToInternalIds;
        private NativeList<int> m_InternalIdFreelist;
        private int m_ExternalBatchCount;
        private SortedSet<int> m_SortedInternalIds;

        private EntityQuery m_MetaEntitiesForHybridRenderableChunks;

        private NativeList<DefaultValueBlitDescriptor> m_DefaultValueBlits;

        private JobHandle m_AABBsCleared;
        private bool m_AABBClearKicked;

        // These arrays are parallel and allocated up to kMaxBatchCount. They are indexed by batch indices.
        // NativeArray<FrozenRenderSceneTag> m_Tags;
        NativeArray<byte> m_ForceLowLOD;

#if UNITY_EDITOR
        float m_CamMoveDistance;
#endif

#if UNITY_EDITOR
        private CullingStats* m_CullingStats = null;

        public CullingStats ComputeCullingStats()
        {
            var result = default(CullingStats);
            for (int i = 0; i < JobsUtility.MaxJobThreadCount; ++i)
            {
                ref var s = ref m_CullingStats[i];

                for (int f = 0; f < (int)CullingStats.kCount; ++f)
                {
                    result.Stats[f] += s.Stats[f];
                }
            }

            result.CameraMoveDistance = m_CamMoveDistance;
            return result;
        }

#endif

        private bool m_ResetLod;

        LODGroupExtensions.LODParams m_PrevLODParams;
        float3 m_PrevCameraPos;
        float m_PrevLodDistanceScale;

        struct MaterialPropertyType
        {
            public int TypeIndex;
            public short SizeBytesCPU;
            public short SizeBytesGPU;
            public bool OverriddenDefault;
        };

        struct PropertyMapping
        {
            public string Name;
            public short SizeCPU;
            public short SizeGPU;
            public MaterialPropertyDefaultValue DefaultValue;
            public bool IsShared;
        }

        NativeMultiHashMap<int, MaterialPropertyType> m_MaterialPropertyTypes;
        NativeMultiHashMap<int, MaterialPropertyType> m_MaterialPropertyTypesShared;
        NativeHashSet<int> m_SharedComponentOverrideTypeIndices;

        // When extra debugging is enabled, store mappings from NameIDs to property names,
        // and from type indices to type names.
        Dictionary<int, string> m_MaterialPropertyNames;
        Dictionary<int, string> m_MaterialPropertyTypeNames;
        Dictionary<int, float4x4> m_MaterialPropertyDefaultValues;
        static Dictionary<Type, PropertyMapping> s_TypeToPropertyMappings = new Dictionary<Type, PropertyMapping>();

#if USE_UNITY_OCCLUSION
        private OcclusionCulling m_OcclusionCulling;
#endif

        private bool m_FirstFrameAfterInit;
        private Shader m_BuiltinErrorShader;
        private Material m_ErrorMaterial;
#if UNITY_EDITOR
        private TrackShaderReflectionChangesSystem m_ShaderReflectionChangesSystem;
        private Dictionary<Shader, bool> m_ShaderHasCompileErrors;
#endif

        NativeHashMap<EntityArchetype, SharedComponentOverridesInfo> m_ArchetypeSharedOverrideInfos;
        public struct SharedComponentOverridesInfo
        {
            public UnsafeList SharedOverrideTypeIndices;
            public ulong SharedOverrideHash;
        }

        // Contains the immutable properties that are set
        // upon batch creation. Only chunks with identical BatchCreateInfo
        // can be combined in a single batch.
        private struct BatchCreateInfo : IEquatable<BatchCreateInfo>
        {
            public static readonly Bounds BigBounds =
                new Bounds(new Vector3(0, 0, 0), new Vector3(1048576.0f, 1048576.0f, 1048576.0f));

            public RenderMesh RenderMesh;
            public EditorRenderData EditorRenderData;
            public Bounds Bounds;
            public bool FlippedWinding;
            public ulong PartitionValue;

            public bool Valid => RenderMesh.mesh != null &&
                                 RenderMesh.material != null &&
                                 RenderMesh.material.shader != null;

            public bool Equals(BatchCreateInfo other)
            {
                return RenderMesh.Equals(other.RenderMesh) &&
                       EditorRenderData.Equals(other.EditorRenderData) &&
                       Bounds.Equals(other.Bounds) &&
                       FlippedWinding == other.FlippedWinding &&
                       PartitionValue == other.PartitionValue;
            }

            public override bool Equals(object obj)
            {
                return obj is BatchCreateInfo other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = RenderMesh.GetHashCode();
                    hashCode = (hashCode * 397) ^ EditorRenderData.GetHashCode();
                    hashCode = (hashCode * 397) ^ Bounds.GetHashCode();
                    hashCode = (hashCode * 397) ^ FlippedWinding.GetHashCode();
                    hashCode = (hashCode * 397) ^ PartitionValue.GetHashCode();
                    return hashCode;
                }
            }
        }

        private class BatchCreateInfoFactory
        {
            public EntityManager EntityManager;
            public SharedComponentTypeHandle<RenderMesh> RenderMeshTypeHandle;
            public SharedComponentTypeHandle<EditorRenderData> EditorRenderDataTypeHandle;
            public SharedComponentTypeHandle<HybridBatchPartition> HybridBatchPartitionHandle;
            public ComponentTypeHandle<RenderMeshFlippedWindingTag> RenderMeshFlippedWindingTagTypeHandle;
            public EditorRenderData DefaultEditorRenderData;

            public BatchCreateInfo CreateInfoForChunk(ArchetypeChunk chunk)
            {
                return new BatchCreateInfo
                {
                    RenderMesh = chunk.GetSharedComponentData(RenderMeshTypeHandle, EntityManager),
                    EditorRenderData = chunk.Has(EditorRenderDataTypeHandle)
                        ? chunk.GetSharedComponentData(EditorRenderDataTypeHandle, EntityManager)
                        : DefaultEditorRenderData,
                    Bounds = BatchCreateInfo.BigBounds,
                    FlippedWinding = chunk.Has(RenderMeshFlippedWindingTagTypeHandle),
                    PartitionValue = chunk.Has(HybridBatchPartitionHandle)
                        ? chunk.GetSharedComponentData(HybridBatchPartitionHandle, EntityManager).PartitionValue
                        : 0,
                };
            }
        }

        private struct BatchCompatibility : IComparer<int>, IDisposable
        {
            public struct BatchSortEntry
            {
                // We can't store BatchCreateInfo itself here easily, because it's a managed
                // type and those cannot be stored in native collections, so we store just the
                // hash and recreate the actual CreateInfo just-in-time when needed.
                public int CreateInfoHash;
                public bool CreateInfoValid;
                public ArchetypeChunk Chunk;
                public SharedComponentOverridesInfo SharedOverrides;
                public ulong SortKey;

                public void CalculateSortKey()
                {
                    SortKey = ((ulong) (CreateInfoHash * 397)) ^ SharedOverrides.SharedOverrideHash;
                }

                public bool IsCompatibleWith(EntityManager entityManager,
                    BatchCreateInfoFactory createInfoFactory,
                    BatchSortEntry e)
                {
                    // NOTE: This archetype property does not seem to hold, do more expensive testing for now
                    // bool sameArchetype = Chunk.Archetype == e.Chunk.Archetype;
                    // // If the archetypes are the same, all values of all shared components must be the same.
                    // if (sameArchetype)
                    //     return true;

                    // If the batch settings are not the same, the chunks are never compatible
                    // We can test the hash first to avoid instantiating the CreateInfo.
                    bool sameBatchHash = CreateInfoHash == e.CreateInfoHash;
                    if (!sameBatchHash)
                        return false;

                    var ci0 = createInfoFactory.CreateInfoForChunk(Chunk);
                    var ci1 = createInfoFactory.CreateInfoForChunk(e.Chunk);
                    bool compatibleBatchSettings = ci0.Equals(ci1);
                    if (!compatibleBatchSettings)
                        return false;

                    // If the batch settings are compatible but the archetypes are different, the
                    // chunks are compatible if and only if they have identical shared component overrides.

                    // If the override hash is different, the overrides cannot be the same.
                    bool sameOverrideHash = SharedOverrides.SharedOverrideHash == e.SharedOverrides.SharedOverrideHash;
                    if (!sameOverrideHash)
                        return false;

                    // If the override hash is the same, then either the overrides are the same or
                    // there is a hash collision. Must do full comparison of shared component values to
                    // know for sure.

                    // NOTE: This test is slightly overconservative. It would be enough that the overrides
                    // used by the shader are the same, but the mapping between shader properties and components
                    // is only done later.

                    var os0 = SharedOverrides.SharedOverrideTypeIndices;
                    var os1 = e.SharedOverrides.SharedOverrideTypeIndices;

                    // If either has no overrides, we are done
                    if (!os0.IsCreated || !os1.IsCreated)
                        return os0.IsCreated == os1.IsCreated;

                    if (os0.Length != os1.Length)
                        return false;

                    for (int i = 0; i < os0.Length; ++i)
                    {
                        int typeIndex = ((int*)os0.Ptr)[i];
                        int ti1 = ((int*) os1.Ptr)[i];
                        if (typeIndex != ti1)
                            return false;

                        var handle = entityManager.GetDynamicSharedComponentTypeHandle(ComponentType.ReadOnly(typeIndex));
                        var c0 = Chunk.GetSharedComponentDataBoxed(handle, entityManager) as IHybridSharedComponentFloat4Override;
                        var c1 = e.Chunk.GetSharedComponentDataBoxed(handle, entityManager) as IHybridSharedComponentFloat4Override;
                        var v0 = c0.GetFloat4OverrideData();
                        var v1 = c1.GetFloat4OverrideData();
                        if (!math.all(v0 == v1))
                            return false;
                    }

                    return true;
                }
            }

            private BatchCreateInfoFactory m_CreateInfoFactory;
            private NativeArray<ArchetypeChunk> m_Chunks;
            private NativeArray<int> m_SortIndices;
            private NativeArray<BatchSortEntry> m_SortEntries;

            public BatchCompatibility(
                HybridRendererSystem system,
                BatchCreateInfoFactory createInfoFactory,
                NativeArray<ArchetypeChunk> chunks)
            {
                m_CreateInfoFactory = createInfoFactory;
                m_Chunks = chunks;
                m_SortIndices = new NativeArray<int>(
                    m_Chunks.Length,
                    Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory);
                m_SortEntries = new NativeArray<BatchSortEntry>(
                    m_Chunks.Length,
                    Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory);

                for (int i = 0; i < m_Chunks.Length; ++i)
                {
                    m_SortIndices[i] = i;
                    m_SortEntries[i] = CreateSortEntry(system, m_Chunks[i]);
                }
            }

            private BatchSortEntry CreateSortEntry(
                HybridRendererSystem system,
                ArchetypeChunk chunk)
            {
                var ci = m_CreateInfoFactory.CreateInfoForChunk(chunk);
                var entry = new BatchSortEntry
                {
                    CreateInfoHash = ci.GetHashCode(),
                    CreateInfoValid = ci.Valid,
                    Chunk = chunk,
                    SharedOverrides = system.SharedComponentOverridesForChunk(chunk),
                };
                entry.CalculateSortKey();
                return entry;
            }

            public NativeArray<ArchetypeChunk> SortChunks()
            {
                // Key-value sort all chunks according to compatibility
                m_SortIndices.Sort(this);
                var sortedChunks = new NativeArray<ArchetypeChunk>(
                    m_Chunks.Length,
                    Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory);

                for (int i = 0; i < m_Chunks.Length; ++i)
                    sortedChunks[i] = m_Chunks[SortedIndex(i)];

                return sortedChunks;
            }

            public int SortedIndex(int i) => m_SortIndices[i];

            public BatchCreateInfo CreateInfoFor(int sortedIndex) =>
                m_CreateInfoFactory.CreateInfoForChunk(m_Chunks[SortedIndex(sortedIndex)]);

            public bool IsCompatible(EntityManager entityManager, int sortedI, int sortedJ)
            {
                return m_SortEntries[SortedIndex(sortedI)].IsCompatibleWith(entityManager, m_CreateInfoFactory, m_SortEntries[SortedIndex(sortedJ)]);
            }

            public int Compare(int x, int y)
            {
                // Sort according to CreateInfo validity and hash based sort key.
                // Hash collisions can result in bad batching (consecutive incompatible chunks),
                // but not incorrect rendering (compatibility is always checked explicitly).

                var sx = m_SortEntries[x];
                var sy = m_SortEntries[y];

                bool vx = sx.CreateInfoValid;
                bool vy = sy.CreateInfoValid;

                // Always sort invalid chunks last, so they can be skipped by shortening the array.
                if (!vx || !vy)
                {
                    if (vx)
                        return -1;
                    else if (vy)
                        return 1;
                    else
                        return 0;
                }

                var hx = sx.SortKey;
                var hy = sy.SortKey;

                if (hx < hy)
                    return -1;
                else if (hx > hy)
                    return 1;
                else
                    return 0;
            }

            public void Dispose()
            {
                m_SortIndices.Dispose();
                m_SortEntries.Dispose();
            }
        }

        private SharedComponentOverridesInfo SharedComponentOverridesForChunk(ArchetypeChunk chunk)
        {
            // Since archetypes are immutable, we can memoize the override infos which are a bit
            // costly to create.
            if (m_ArchetypeSharedOverrideInfos.TryGetValue(chunk.Archetype, out var existingInfo))
                return existingInfo;

            var componentTypes = chunk.Archetype.GetComponentTypes();
            int numOverrides = 0;

            // First, count the amount of overrides so we know how much to allocate
            for (int i = 0; i < componentTypes.Length; ++i)
            {
                int typeIndex = componentTypes[i].TypeIndex;

                // If the type is not in this hash map, it's not a shared component override
                if (!m_SharedComponentOverrideTypeIndices.Contains(typeIndex))
                    continue;

                ++numOverrides;
            }

            // If there are no overrides, no need to allocate or hash anything
            if (numOverrides == 0)
            {
                var nullInfo = new SharedComponentOverridesInfo
                {
                    SharedOverrideTypeIndices = default,
                    SharedOverrideHash = 0,
                };
                m_ArchetypeSharedOverrideInfos[chunk.Archetype] = nullInfo;
                return nullInfo;
            }

            var overridesArray = new UnsafeList(
                sizeof(int),
                UnsafeUtility.AlignOf<int>(),
                numOverrides,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            overridesArray.Resize<int>(numOverrides, NativeArrayOptions.UninitializedMemory);
            var overrides = (int*) overridesArray.Ptr;

            int j = 0;
            for (int i = 0; i < componentTypes.Length; ++i)
            {
                int typeIndex = componentTypes[i].TypeIndex;

                // If the type is not in this hash map, it's not a shared component override
                if (!m_SharedComponentOverrideTypeIndices.Contains(typeIndex))
                    continue;

                overrides[j] = typeIndex;
                ++j;
            }
            componentTypes.Dispose();

            // Sort the type indices so every archetype has them in the same order
            overridesArray.Sort<int>();

            // Finally, hash the actual contents
            xxHash3.StreamingState hash = new xxHash3.StreamingState(true);
            for (int i = 0; i < overridesArray.Length; ++i)
            {
                int typeIndex = overrides[i];
                var handle = GetDynamicSharedComponentTypeHandle(ComponentType.ReadOnly(typeIndex));
                var c = chunk.GetSharedComponentDataBoxed(handle, EntityManager) as IHybridSharedComponentFloat4Override;
                var v = c.GetFloat4OverrideData();
                hash.Update(&v, UnsafeUtility.SizeOf<float4>());
            }
            uint2 h64 = hash.DigestHash64();

            var info = new SharedComponentOverridesInfo
            {
                SharedOverrideTypeIndices = overridesArray,
                SharedOverrideHash = (ulong)h64.x | (((ulong)h64.y) << 32),
            };
            m_ArchetypeSharedOverrideInfos[chunk.Archetype] = info;
            return info;
        }

        // Status enums are ordered so that numerically larger enums always take priority
        // if at least one chunk in the batch requires that.
        public enum BatchPropertyOverrideStatus
        {
            Uninitialized, // Override status has not been initialized
            SharedZeroDefault, // Property uses a zero bit pattern value for all entities
            SharedNonzeroDefault, // Property uses a shared value for all entities, but it's not zero
            SharedComponentOverride, // Property uses a shared value that is read from a shared component
            PerEntityOverride, // Property uses per-entity unique values
        }

        private static bool PropertyIsZero(BatchPropertyOverrideStatus status) =>
            status == BatchPropertyOverrideStatus.SharedZeroDefault;

        private static bool PropertyRequiresAllocation(BatchPropertyOverrideStatus status) =>
            !PropertyIsZero(status);

        private static bool PropertyRequiresBlit(BatchPropertyOverrideStatus status) =>
            status == BatchPropertyOverrideStatus.SharedNonzeroDefault ||
            status == BatchPropertyOverrideStatus.SharedComponentOverride;

        private static bool PropertyHasPerEntityValues(BatchPropertyOverrideStatus status) =>
            status == BatchPropertyOverrideStatus.PerEntityOverride;

        private static int PropertyValuesPerAllocation(BatchPropertyOverrideStatus status, int numInstances) =>
            PropertyHasPerEntityValues(status)
                ? numInstances
                : 1;

        private struct BatchInfo
        {
            // There is one BatchProperty per shader property, which can be different from
            // the amount of overriding components.
            // TODO: Most of this data is no longer needed after the batch has been created, and could be
            // allocated from temp memory and freed after the batch has been created.

            #pragma warning disable CS0649
            // CS0649: Field is never assigned to, and will always have its default value 0
            internal struct BatchProperty
            {
                public int MetadataOffset;
                public short SizeBytesCPU;
                public short SizeBytesGPU;
                public int CbufferIndex;
                public int OverrideComponentsIndex;
                public int OverrideSharedComponentsIndex;
#if USE_PROPERTY_ASSERTS
                public int NameID;
#endif
                public BatchPropertyOverrideStatus OverrideStatus;
                public HeapBlock GPUAllocation;
                public float4x4 DefaultValue;
            }

            // There is one BatchOverrideComponent for each component type that can possibly
            // override any of the BatchProperty entries. Some entries might have zero,
            // some entries might have multiples. Each chunk is only allowed a single overriding component.
            // This list is allocated from temporary memory and is freed after the batch has been fully created.
            internal struct BatchOverrideComponent
            {
                public int BatchPropertyIndex;
                public int TypeIndex;
            }
            #pragma warning restore CS0649

            public UnsafeList<BatchProperty> Properties;
            public UnsafeList<BatchOverrideComponent> OverrideComponents;
            public UnsafeList<BatchOverrideComponent> OverrideSharedComponents;
            public UnsafeList<HeapBlock> ChunkMetadataAllocations;

            public void Dispose()
            {
                if (Properties.IsCreated) Properties.Dispose();
                if (OverrideComponents.IsCreated) OverrideComponents.Dispose();
                if (OverrideSharedComponents.IsCreated) OverrideSharedComponents.Dispose();
                if (ChunkMetadataAllocations.IsCreated) ChunkMetadataAllocations.Dispose();
            }
        }

        protected override void OnCreate()
        {
            m_PersistentInstanceDataSize = kGPUBufferSizeInitial;

            //@TODO: Support SetFilter with EntityQueryDesc syntax
            // This component group must include all types that are being used by the culling job
            m_CullingJobDependencyGroup = GetEntityQuery(
                ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                ComponentType.ReadOnly<RootLODRange>(),
                ComponentType.ReadOnly<RootLODWorldReferencePoint>(),
                ComponentType.ReadOnly<LODRange>(),
                ComponentType.ReadOnly<LODWorldReferencePoint>(),
                ComponentType.ReadOnly<WorldRenderBounds>(),
                ComponentType.ReadOnly<ChunkHeader>(),
                ComponentType.ChunkComponentReadOnly<HybridChunkInfo>()
            );

            m_HybridRenderedQuery = GetEntityQuery(HybridUtils.GetHybridRenderedQueryDesc());

            m_LodSelectGroup = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadWrite<HybridChunkInfo>(),
                    ComponentType.ReadOnly<ChunkHeader>()
                },
            });

            m_CullingGroup = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ChunkWorldRenderBounds>(),
                    ComponentType.ReadOnly<ChunkHeader>(),
                    ComponentType.ReadOnly<HybridChunkInfo>()
                },
            });

            m_BatchRendererGroup = new BatchRendererGroup(this.OnPerformCulling);
            // m_Tags = new NativeArray<FrozenRenderSceneTag>(kMaxBatchCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_ForceLowLOD = new NativeArray<byte>(kMaxBatchCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            m_ResetLod = true;

            m_GPUPersistentAllocator = new HeapAllocator(kMaxGPUAllocatorMemory, 16);
            m_ChunkMetadataAllocator = new HeapAllocator(kMaxChunkMetadata);

            m_BatchInfos = new NativeArray<BatchInfo>(kMaxBatchCount, Allocator.Persistent);
            m_BatchMotionInfos = new NativeArray<BatchMotionInfo>(kMaxBatchCount, Allocator.Persistent);
#if USE_PICKING_MATRICES
            m_BatchPickingMatrices = new NativeArray<IntPtr>(kMaxBatchCount, Allocator.Persistent);
#endif
            m_ChunkProperties = new NativeArray<ChunkProperty>(kMaxChunkMetadata, Allocator.Persistent);
            m_ExistingBatchInternalIndices = new NativeHashMap<int, int>(128, Allocator.Persistent);
            m_ComponentTypeCache = new ComponentTypeCache(128);

            m_BatchAABBs = new NativeArray<float>(kMaxBatchCount * (int)HybridChunkUpdater.kFloatsPerAABB, Allocator.Persistent);

            m_DefaultValueBlits = new NativeList<DefaultValueBlitDescriptor>(Allocator.Persistent);

            m_AABBsCleared = new JobHandle();
            m_AABBClearKicked = false;

            // Globally allocate a single zero matrix and reuse that for all default values that are pure zero
            m_SharedZeroAllocation = m_GPUPersistentAllocator.Allocate((ulong)sizeof(float4x4));
            Debug.Assert(!m_SharedZeroAllocation.Empty, "Allocation of constant-zero data failed");
            // Make sure the global zero is actually zero.
            m_DefaultValueBlits.Add(new DefaultValueBlitDescriptor
            {
                DefaultValue = float4x4.zero,
                DestinationOffset = (uint)m_SharedZeroAllocation.begin,
                ValueSizeBytes = (uint)sizeof(float4x4),
                Count = 1,
            });

            ResetIds();

            m_MetaEntitiesForHybridRenderableChunks = EntityManager.CreateEntityQuery(
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadWrite<HybridChunkInfo>(),
                        ComponentType.ReadOnly<ChunkHeader>(),
                        ComponentType.ReadOnly<ChunkWorldRenderBounds>(),
                    },
                });

#if UNITY_EDITOR
            m_CullingStats = (CullingStats*)Memory.Unmanaged.Allocate(JobsUtility.MaxJobThreadCount * sizeof(CullingStats),
                64, Allocator.Persistent);
#endif

            // Collect all components with [MaterialProperty] attribute
            m_MaterialPropertyTypes = new NativeMultiHashMap<int, MaterialPropertyType>(256, Allocator.Persistent);
            m_MaterialPropertyTypesShared = new NativeMultiHashMap<int, MaterialPropertyType>(256, Allocator.Persistent);
            m_SharedComponentOverrideTypeIndices = new NativeHashSet<int>(256, Allocator.Persistent);
            m_MaterialPropertyNames = new Dictionary<int, string>();
            m_MaterialPropertyTypeNames = new Dictionary<int, string>();
            m_MaterialPropertyDefaultValues = new Dictionary<int, float4x4>();
            m_ArchetypeSharedOverrideInfos =
                new NativeHashMap<EntityArchetype, SharedComponentOverridesInfo>(256, Allocator.Persistent);

            // Some hardcoded mappings to avoid dependencies to Hybrid from DOTS
#if SRP_10_0_0_OR_NEWER
            RegisterMaterialPropertyType<LocalToWorld>("unity_ObjectToWorld", 4 * 4 * 3);
            RegisterMaterialPropertyType<WorldToLocal_Tag>("unity_WorldToObject", overrideTypeSizeGPU: 4 * 4 * 3);
#else
            RegisterMaterialPropertyType<LocalToWorld>("unity_ObjectToWorld", 4 * 4 * 4);
            RegisterMaterialPropertyType<WorldToLocal_Tag>("unity_WorldToObject", 4 * 4 * 4);
#endif

            // Ifdef guard registering types that might not exist if V2 is disabled.
#if ENABLE_HYBRID_RENDERER_V2
            // Explicitly use a default of all ones for probe occlusion, so stuff doesn't render as black if this isn't set.
            RegisterMaterialPropertyType<BuiltinMaterialPropertyUnity_ProbesOcclusion>(
                "unity_ProbesOcclusion",
                defaultValue: new float4(1, 1, 1, 1));
#endif

            foreach (var typeInfo in TypeManager.AllTypes)
            {
                var type = typeInfo.Type;

                bool isComponent = typeof(IComponentData).IsAssignableFrom(type);
                bool isSharedComponent = typeof(ISharedComponentData).IsAssignableFrom(type);
                if (isComponent || isSharedComponent)
                {
                    var attributes = type.GetCustomAttributes(typeof(MaterialPropertyAttribute), false);
                    if (attributes.Length > 0)
                    {
                        var propertyAttr = (MaterialPropertyAttribute)attributes[0];

                        if (isSharedComponent)
                        {
                            Debug.Assert(typeof(IHybridSharedComponentFloat4Override).IsAssignableFrom(type),
                                $"Hybrid Renderer ISharedComponentData overrides must implement IHybridSharedComponentOverride. Type \"{type.Name}\" does not.");
                            Debug.Assert(propertyAttr.Format == MaterialPropertyFormat.Float4,
                                $"Hybrid Renderer ISharedComponentData overrides must have format Float4. Type \"{type.Name}\" had format {propertyAttr.Format} instead.");
                        }

                        RegisterMaterialPropertyType(type, propertyAttr.Name, propertyAttr.OverrideSizeGPU);
                    }
                }
            }

            m_GPUPersistentInstanceData = new ComputeBuffer(
                (int)m_PersistentInstanceDataSize / 4,
                4,
                ComputeBufferType.Raw);
            m_GPUUploader = new SparseUploader(m_GPUPersistentInstanceData, kGPUUploaderChunkSize);

#if USE_UNITY_OCCLUSION
            m_OcclusionCulling = new OcclusionCulling();
            m_OcclusionCulling.Create(EntityManager);
#endif

            m_FirstFrameAfterInit = true;

            // Hybrid Renderer cannot use the internal error shader because it doesn't support
            // DOTS instancing, but we can check if it's set as a fallback, and use a supported
            // error shader instead.

            m_BuiltinErrorShader = Shader.Find("Hidden/InternalErrorShader");
#if UNITY_EDITOR
            m_ShaderHasCompileErrors = new Dictionary<Shader, bool>();
#endif

            Shader hybridErrorShader = null;

#if HDRP_9_0_0_OR_NEWER
            hybridErrorShader = Shader.Find("Hidden/HDRP/MaterialError");
#endif

#if URP_9_0_0_OR_NEWER
            hybridErrorShader = Shader.Find("Hidden/Universal Render Pipeline/MaterialError");
#endif

            // TODO: What about custom SRPs? Is it enough to just throw an error, or should
            // we search for a custom shader with a specific name?

            if (hybridErrorShader != null)
                m_ErrorMaterial = new Material(hybridErrorShader);
        }

        public static void RegisterMaterialPropertyType(Type type, string propertyName, short overrideTypeSizeGPU = -1, MaterialPropertyDefaultValue defaultValue = default)
        {
            Debug.Assert(type != null, "type must be non-null");
            Debug.Assert(!string.IsNullOrEmpty(propertyName), "Property name must be valid");

            short typeSizeCPU = (short)UnsafeUtility.SizeOf(type);
            if (overrideTypeSizeGPU == -1)
                overrideTypeSizeGPU = typeSizeCPU;

            // For now, we only support overriding one material property with one type.
            // Several types can override one property, but not the other way around.
            // If necessary, this restriction can be lifted in the future.
            if (s_TypeToPropertyMappings.ContainsKey(type))
            {
                string prevPropertyName = s_TypeToPropertyMappings[type].Name;
                Debug.Assert(propertyName.Equals(prevPropertyName),
                    $"Attempted to register type {type.Name} with multiple different property names. Registered with \"{propertyName}\", previously registered with \"{prevPropertyName}\".");
            }
            else
            {
                var pm = new PropertyMapping();
                pm.Name = propertyName;
                pm.SizeCPU = typeSizeCPU;
                pm.SizeGPU = overrideTypeSizeGPU;
                pm.DefaultValue = defaultValue;
                pm.IsShared = typeof(ISharedComponentData).IsAssignableFrom(type);
                s_TypeToPropertyMappings[type] = pm;
            }
        }

        public static void RegisterMaterialPropertyType<T>(string propertyName, short overrideTypeSizeGPU = -1, MaterialPropertyDefaultValue defaultValue = default)
            where T : IComponentData
        {
            RegisterMaterialPropertyType(typeof(T), propertyName, overrideTypeSizeGPU, defaultValue);
        }

        private void InitializeMaterialProperties()
        {
            m_MaterialPropertyTypes.Clear();
            m_MaterialPropertyTypesShared.Clear();
            m_MaterialPropertyDefaultValues.Clear();

            foreach (var kv in s_TypeToPropertyMappings)
            {
                Type type = kv.Key;
                string propertyName = kv.Value.Name;

                short sizeBytesCPU = kv.Value.SizeCPU;
                short sizeBytesGPU = kv.Value.SizeGPU;
                int typeIndex = TypeManager.GetTypeIndex(type);
                int nameID = Shader.PropertyToID(propertyName);
                var defaultValue = kv.Value.DefaultValue;
                bool isShared = kv.Value.IsShared;

                if (isShared)
                {
                    m_MaterialPropertyTypesShared.Add(nameID,
                        new MaterialPropertyType
                        {
                            TypeIndex = typeIndex,
                            SizeBytesCPU = sizeBytesCPU,
                            SizeBytesGPU = sizeBytesGPU,
                            OverriddenDefault = defaultValue.Nonzero,
                        });
                    m_SharedComponentOverrideTypeIndices.Add(typeIndex);
                }
                else
                {
                    m_MaterialPropertyTypes.Add(nameID,
                        new MaterialPropertyType
                        {
                            TypeIndex = typeIndex,
                            SizeBytesCPU = sizeBytesCPU,
                            SizeBytesGPU = sizeBytesGPU,
                            OverriddenDefault = defaultValue.Nonzero,
                        });
                }

                if (defaultValue.Nonzero)
                    m_MaterialPropertyDefaultValues[typeIndex] = defaultValue.Value;

#if USE_PROPERTY_ASSERTS
                m_MaterialPropertyNames[nameID] = propertyName;
                m_MaterialPropertyTypeNames[typeIndex] = type.Name;
#endif

#if DEBUG_LOG_MATERIAL_PROPERTIES
                Debug.Log($"Type \"{type.Name}\" ({sizeBytesCPU} bytes) overrides material property \"{propertyName}\" (nameID: {nameID}, typeIndex: {typeIndex})");
#endif

                // We cache all IComponentData types that we know are capable of overriding properties
                if (!isShared)
                    m_ComponentTypeCache.UseType(typeIndex);
            }

            s_TypeToPropertyMappings.Clear();
        }

        protected override void OnDestroy()
        {
            CompleteJobs();
            Dispose();
        }

        bool HasShaderReflectionChanged
        {
            get
            {
#if UNITY_EDITOR
                return m_ShaderReflectionChangesSystem != null ? m_ShaderReflectionChangesSystem.HasReflectionChanged : false;
#else
                return false;
#endif
            }
        }

        void AlignWithShaderReflectionChanges()
        {
#if UNITY_EDITOR
            // Reflection changing can imply that shader compile error status
            // has also changed, so flush our cache.
            if (HasShaderReflectionChanged)
            {
                m_ShaderHasCompileErrors = new Dictionary<Shader, bool>();
            }
#endif
        }

        JobHandle UpdateHybridV2Batches(JobHandle inputDependencies)
        {
            if (m_FirstFrameAfterInit)
            {
                OnFirstFrame();
                m_FirstFrameAfterInit = false;
            }

            AlignWithShaderReflectionChanges();

            JobHandle done = default;
            Profiler.BeginSample("UpdateAllBatches");
            using (var hybridChunks =
                       m_HybridRenderedQuery.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                done = UpdateAllBatches(inputDependencies);
            }

            Profiler.EndSample();

            return done;
        }

        private void OnFirstFrame()
        {
#if UNITY_EDITOR
            m_ShaderReflectionChangesSystem = World.GetExistingSystem<TrackShaderReflectionChangesSystem>();
#endif

            InitializeMaterialProperties();

#if DEBUG_LOG_HYBRID_V2
            Debug.Log(
                $"Hybrid Renderer V2 active, MaterialProperty component type count {m_ComponentTypeCache.UsedTypeCount} / {ComponentTypeCache.BurstCompatibleTypeArray.kMaxTypes}");
#endif
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            Profiler.BeginSample("CompleteJobs");
            inputDeps.Complete(); // #todo
            CompleteJobs();
            ResetLod();
            Profiler.EndSample();

            var done = new JobHandle();
            try
            {

                Profiler.BeginSample("UpdateHybridV2Batches");
                done = UpdateHybridV2Batches(inputDeps);
                Profiler.EndSample();

                Profiler.BeginSample("EndUpdate");
                EndUpdate();
                Profiler.EndSample();
            }
            finally
            {
                m_GPUUploader.FrameCleanup();
            }

            HybridEditorTools.EndFrame();

            return done;
        }

        private void ResetIds()
        {
            if (m_InternalToExternalIds.IsCreated) m_InternalToExternalIds.Dispose();
            m_InternalToExternalIds = new NativeArray<int>(kMaxBatchCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            if (m_ExternalToInternalIds.IsCreated) m_ExternalToInternalIds.Dispose();
            m_ExternalToInternalIds = new NativeArray<int>(kMaxBatchCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < m_InternalToExternalIds.Length; ++i) m_InternalToExternalIds[i] = -1;
            for (int i = 0; i < m_ExternalToInternalIds.Length; ++i) m_ExternalToInternalIds[i] = -1;

            m_ExternalBatchCount = 0;
            m_SortedInternalIds = new SortedSet<int>();

            if (m_InternalIdFreelist.IsCreated) m_InternalIdFreelist.Dispose();
            m_InternalIdFreelist = new NativeList<int>(kMaxBatchCount, Allocator.Persistent);

            for (int i = m_InternalToExternalIds.Length - 1; i >= 0; --i)
                m_InternalIdFreelist.Add(i);
        }

        internal int AllocateInternalId()
        {
            if (!(m_InternalIdFreelist.Length > 0)) Debug.Assert(false, $"Maximum Hybrid Renderer batch count ({kMaxBatchCount}) exceeded.");
            int id = m_InternalIdFreelist[m_InternalIdFreelist.Length - 1];
            m_InternalIdFreelist.Resize(m_InternalIdFreelist.Length - 1, NativeArrayOptions.UninitializedMemory);
            Debug.Assert(!m_SortedInternalIds.Contains(id), "Freshly allocated batch id found in list of used ids");
            m_SortedInternalIds.Add(id);
            return id;
        }

        internal void ReleaseInternalId(int id)
        {
            if (!(id >= 0 && id < m_InternalToExternalIds.Length)) Debug.Assert(false, $"Attempted to release invalid batch id {id}");
            if (!m_SortedInternalIds.Contains(id)) Debug.Assert(false, $"Attempted to release an unused id {id}");
            m_SortedInternalIds.Remove(id);
            m_InternalIdFreelist.Add(id);
        }

        internal void RemoveExternalIdSwapWithBack(int externalId)
        {
            // Mimic the swap back and erase that BatchRendererGroup does

            int internalIdOfRemoved = m_ExternalToInternalIds[externalId];
            int lastExternalId = m_ExternalBatchCount - 1;

            if (lastExternalId != externalId)
            {
                int internalIdOfLast = m_ExternalToInternalIds[lastExternalId];
                int newExternalIdOfLast = externalId;

                m_InternalToExternalIds[internalIdOfLast] = newExternalIdOfLast;
                m_ExternalToInternalIds[newExternalIdOfLast] = internalIdOfLast;

                m_InternalToExternalIds[internalIdOfRemoved] = -1;
                m_ExternalToInternalIds[lastExternalId] = -1;
            }
            else
            {
                m_InternalToExternalIds[internalIdOfRemoved] = -1;
                m_ExternalToInternalIds[externalId] = -1;
            }
        }

        internal int AddBatchIndex(int externalId)
        {
            int internalId = AllocateInternalId();
            m_InternalToExternalIds[internalId] = externalId;
            m_ExternalToInternalIds[externalId] = internalId;
            m_ExistingBatchInternalIndices[internalId] = internalId;
            ++m_ExternalBatchCount;
            return internalId;
        }

        internal void RemoveBatchIndex(int internalId, int externalId)
        {
            if (!(m_ExternalBatchCount > 0)) Debug.Assert(false, $"Attempted to release an invalid BatchRendererGroup id {externalId}");
            m_ExistingBatchInternalIndices.Remove(internalId);
            RemoveExternalIdSwapWithBack(externalId);
            ReleaseInternalId(internalId);
            --m_ExternalBatchCount;
        }

        internal int InternalIndexRange => m_SortedInternalIds.Max + 1;

        public void Dispose()
        {
            m_GPUUploader.Dispose();
            m_GPUPersistentInstanceData.Dispose();

#if UNITY_EDITOR
            Memory.Unmanaged.Free(m_CullingStats, Allocator.Persistent);

            m_CullingStats = null;
#endif
            m_BatchRendererGroup.Dispose();
            // m_Tags.Dispose();
            m_ForceLowLOD.Dispose();
            m_ResetLod = true;
            m_MaterialPropertyTypes.Dispose();
            m_MaterialPropertyTypesShared.Dispose();
            m_SharedComponentOverrideTypeIndices.Dispose();
            m_GPUPersistentAllocator.Dispose();
            m_ChunkMetadataAllocator.Dispose();

            m_BatchInfos.Dispose();
            m_BatchMotionInfos.Dispose();
#if USE_PICKING_MATRICES
            m_BatchPickingMatrices.Dispose();
#endif
            m_ChunkProperties.Dispose();
            m_ExistingBatchInternalIndices.Dispose();
            m_DefaultValueBlits.Dispose();
            m_ComponentTypeCache.Dispose();

            m_BatchAABBs.Dispose();

            m_MetaEntitiesForHybridRenderableChunks.Dispose();

            if (m_InternalToExternalIds.IsCreated) m_InternalToExternalIds.Dispose();
            if (m_ExternalToInternalIds.IsCreated) m_ExternalToInternalIds.Dispose();
            if (m_InternalIdFreelist.IsCreated) m_InternalIdFreelist.Dispose();
            m_ExternalBatchCount = 0;
            m_SortedInternalIds = null;

#if USE_UNITY_OCCLUSION
            m_OcclusionCulling.Dispose();
#endif

            DisposeSharedOverrideCache();

            m_AABBsCleared = new JobHandle();
            m_AABBClearKicked = false;
        }

        private void DisposeSharedOverrideCache()
        {
            var infos = m_ArchetypeSharedOverrideInfos.GetValueArray(Allocator.Temp);
            for (int i = 0; i < infos.Length; ++i)
            {
                var indices  = infos[i].SharedOverrideTypeIndices;
                if (indices.IsCreated) indices.Dispose();
            }

            infos.Dispose();
            m_ArchetypeSharedOverrideInfos.Dispose();
        }

        public void ResetLod()
        {
            m_PrevLODParams = new LODGroupExtensions.LODParams();
            m_ResetLod = true;
        }

        public JobHandle OnPerformCulling(BatchRendererGroup rendererGroup, BatchCullingContext cullingContext)
        {
            var batchCount = cullingContext.batchVisibility.Length;
            if (batchCount == 0)
                return new JobHandle();
            ;

            var lodParams = LODGroupExtensions.CalculateLODParams(cullingContext.lodParameters);

            Profiler.BeginSample("OnPerformCulling");

            int cullingPlaneCount = cullingContext.cullingPlanes.Length;
            var planes = FrustumPlanes.BuildSOAPlanePackets(cullingContext.cullingPlanes, Allocator.TempJob);

            JobHandle cullingDependency;
            var resetLod = m_ResetLod || (!lodParams.Equals(m_PrevLODParams));
            if (resetLod)
            {
                // Depend on all component ata we access + previous jobs since we are writing to a single
                // m_ChunkInstanceLodEnableds array.
                var lodJobDependency = JobHandle.CombineDependencies(m_CullingJobDependency,
                    m_CullingJobDependencyGroup.GetDependency());

                float cameraMoveDistance = math.length(m_PrevCameraPos - lodParams.cameraPos);
                var lodDistanceScaleChanged = lodParams.distanceScale != m_PrevLodDistanceScale;

#if UNITY_EDITOR
                // Record this separately in the editor for stats display
                m_CamMoveDistance = cameraMoveDistance;
#endif

                var selectLodEnabledJob = new SelectLodEnabled
                {
                    ForceLowLOD = m_ForceLowLOD,
                    LODParams = lodParams,
                    RootLODRanges = GetComponentTypeHandle<RootLODRange>(true),
                    RootLODReferencePoints = GetComponentTypeHandle<RootLODWorldReferencePoint>(true),
                    LODRanges = GetComponentTypeHandle<LODRange>(true),
                    LODReferencePoints = GetComponentTypeHandle<LODWorldReferencePoint>(true),
                    HybridChunkInfo = GetComponentTypeHandle<HybridChunkInfo>(),
                    ChunkHeader = GetComponentTypeHandle<ChunkHeader>(),
                    CameraMoveDistanceFixed16 =
                        Fixed16CamDistance.FromFloatCeil(cameraMoveDistance * lodParams.distanceScale),
                    DistanceScale = lodParams.distanceScale,
                    DistanceScaleChanged = lodDistanceScaleChanged,
#if UNITY_EDITOR
                    Stats = m_CullingStats,
#endif
                };

                cullingDependency = m_LODDependency = selectLodEnabledJob.Schedule(m_LodSelectGroup, lodJobDependency);

                m_PrevLODParams = lodParams;
                m_PrevLodDistanceScale = lodParams.distanceScale;
                m_PrevCameraPos = lodParams.cameraPos;
                m_ResetLod = false;
#if UNITY_EDITOR
                UnsafeUtility.MemClear(m_CullingStats, sizeof(CullingStats) * JobsUtility.MaxJobThreadCount);
#endif
            }
            else
            {
                // Depend on all component data we access + previous m_LODDependency job
                cullingDependency = JobHandle.CombineDependencies(
                    m_LODDependency,
                    m_CullingJobDependency,
                    m_CullingJobDependencyGroup.GetDependency());
            }

            var threadLocalIndexLists = new NativeArray<int>(
                (int)(JobsUtility.MaxJobThreadCount * SimpleCullingJob.kMaxEntitiesPerChunk),
                Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            var zeroCountsJob = new ZeroVisibleCounts
            {
                Batches = cullingContext.batchVisibility,
            };
            cullingDependency = JobHandle.CombineDependencies(
                cullingDependency,
                zeroCountsJob.Schedule(cullingContext.batchVisibility.Length, 16));

            var simpleCullingJob = new SimpleCullingJob
            {
                Planes = planes,
                InternalToExternalRemappingTable = m_InternalToExternalIds,
                HybridChunkInfo = GetComponentTypeHandle<HybridChunkInfo>(),
                ChunkHeader = GetComponentTypeHandle<ChunkHeader>(true),
                ChunkWorldRenderBounds = GetComponentTypeHandle<ChunkWorldRenderBounds>(true),
                BoundsComponent = GetComponentTypeHandle<WorldRenderBounds>(true),
                IndexList = cullingContext.visibleIndices,
                Batches = cullingContext.batchVisibility,
                ThreadLocalIndexLists = threadLocalIndexLists,
#if UNITY_EDITOR
                Stats = m_CullingStats,
#endif
            };

            var simpleCullingJobHandle = simpleCullingJob.Schedule(m_CullingGroup, cullingDependency);
            threadLocalIndexLists.Dispose(simpleCullingJobHandle);

#if DEBUG_LOG_VISIBLE_INSTANCES
            {
                simpleCullingJobHandle.Complete();
                int numTotal = 0;
                int numVisible = 0;
                for (int i = 0; i < cullingContext.batchVisibility.Length; ++i)
                {
                    var v = cullingContext.batchVisibility[i];
                    numTotal += v.instancesCount;
                    numVisible += v.visibleCount;
                }

                Debug.Log($"Culling: {numVisible} / {numTotal} visible ({(double) numVisible * 100.0 / numTotal:F2}%)");
            }
#endif

            DidScheduleCullingJob(simpleCullingJobHandle);

#if USE_UNITY_OCCLUSION
            var occlusionCullingDependency = m_OcclusionCulling.Cull(EntityManager, m_InternalToExternalIds, cullingContext, m_CullingJobDependency
#if UNITY_EDITOR
                    , m_CullingStats
#endif
                    );
            DidScheduleCullingJob(occlusionCullingDependency);
#endif

            Profiler.EndSample();
            return m_CullingJobDependency;
        }

        public JobHandle UpdateAllBatches(JobHandle inputDependencies)
        {
            m_DefaultValueBlits.Clear();

            Profiler.BeginSample("GetComponentTypes");
            var hybridRenderedChunkType =
                GetComponentTypeHandle<HybridChunkInfo>();
            m_ComponentTypeCache.FetchTypeHandles(this);
            Profiler.EndSample();

            int numNewChunks = 0;
            JobHandle hybridCompleted = new JobHandle();

            const int kNumBitsPerLong = sizeof(long) * 8;
            var unreferencedInternalIndices = new NativeArray<long>(
                (InternalIndexRange + kNumBitsPerLong) / kNumBitsPerLong,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);

            // Allocate according to max batch count for these, so they can also handle potential
            // new batches during the frame.
            var batchRequiresUpdates = new NativeArray<long>(
                kMaxBatchCount / kNumBitsPerLong,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);

            var batchHadMovingEntities = new NativeArray<long>(
                kMaxBatchCount / kNumBitsPerLong,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);

            JobHandle initializedUnreferenced = default;
            var existingKeys = m_ExistingBatchInternalIndices.GetKeyArray(Allocator.TempJob);
            initializedUnreferenced = new InitializeUnreferencedIndicesScatterJob
            {
                ExistingInternalIndices = existingKeys,
                UnreferencedInternalIndices = unreferencedInternalIndices,
            }.Schedule(existingKeys.Length, kNumScatteredIndicesPerThread);
            existingKeys.Dispose(initializedUnreferenced);

            inputDependencies = JobHandle.CombineDependencies(inputDependencies, initializedUnreferenced);

            int totalChunks = m_HybridRenderedQuery.CalculateChunkCount();
            var newChunks = new NativeArray<ArchetypeChunk>(
                totalChunks,
                Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            var numNewChunksArray = new NativeArray<int>(
                1,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);

            // Conservative estimate is that every known type is in every chunk. There will be
            // at most one operation per type per chunk, which will be either an actual
            // chunk data upload, or a default value blit (a single type should not have both).
            int conservativeMaximumGpuUploads = totalChunks * m_ComponentTypeCache.UsedTypeCount;
            var gpuUploadOperations = new NativeArray<GpuUploadOperation>(
                conservativeMaximumGpuUploads,
                Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            var numGpuUploadOperationsArray = new NativeArray<int>(
                1,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);

            uint lastSystemVersion = LastSystemVersion;

            if (HybridEditorTools.DebugSettings.ForceInstanceDataUpload)
            {
                Debug.Log("Reuploading all Hybrid Renderer instance data to GPU");
                lastSystemVersion = 0;
            }

            var hybridChunkUpdater = new HybridChunkUpdater
            {
                ComponentTypes = m_ComponentTypeCache.ToBurstCompatible(Allocator.TempJob),
                UnreferencedInternalIndices = unreferencedInternalIndices,
                BatchRequiresUpdates = batchRequiresUpdates,
                BatchHadMovingEntities = batchHadMovingEntities,
                NewChunks = newChunks,
                NumNewChunks = numNewChunksArray,
                ChunkProperties = m_ChunkProperties,
                BatchMotionInfos = m_BatchMotionInfos,
                BatchAABBs = m_BatchAABBs,
                LastSystemVersion = lastSystemVersion,
                PreviousBatchIndex = -1,

                GpuUploadOperations = gpuUploadOperations,
                NumGpuUploadOperations = numGpuUploadOperationsArray,

                LocalToWorldType = TypeManager.GetTypeIndex<LocalToWorld>(),
                WorldToLocalType = TypeManager.GetTypeIndex<WorldToLocal_Tag>(),
                PrevLocalToWorldType = TypeManager.GetTypeIndex<BuiltinMaterialPropertyUnity_MatrixPreviousM>(),
                PrevWorldToLocalType = TypeManager.GetTypeIndex<BuiltinMaterialPropertyUnity_MatrixPreviousMI_Tag>(),

#if PROFILE_BURST_JOB_INTERNALS
                ProfileAddUpload = new ProfilerMarker("AddUpload"),
                ProfilePickingMatrices = new ProfilerMarker("EditorPickingMatrices"),
#endif
#if USE_PICKING_MATRICES
                BatchPickingMatrices = m_BatchPickingMatrices,
#endif
            };

            var updateAllJob = new UpdateAllHybridChunksJob
            {
                HybridChunkInfo = GetComponentTypeHandle<HybridChunkInfo>(true),
                ChunkWorldRenderBounds = GetComponentTypeHandle<ChunkWorldRenderBounds>(true),
                ChunkHeader = GetComponentTypeHandle<ChunkHeader>(true),
                LocalToWorld = GetComponentTypeHandle<LocalToWorld>(true),
                LodRange = GetComponentTypeHandle<LODRange>(true),
                RootLodRange = GetComponentTypeHandle<RootLODRange>(true),
                RenderMesh = GetSharedComponentTypeHandle<RenderMesh>(),
                HybridChunkUpdater = hybridChunkUpdater,
            };

            JobHandle updateAllDependencies = JobHandle.CombineDependencies(inputDependencies, EnsureAABBsCleared());

            // We need to wait for the job to complete here so we can process the new chunks
            updateAllJob.Schedule(m_MetaEntitiesForHybridRenderableChunks, updateAllDependencies).Complete();

            // Garbage collect deleted batches before adding new ones to minimize peak memory use.
            Profiler.BeginSample("GarbageCollectUnreferencedBatches");
            int numRemoved = GarbageCollectUnreferencedBatches(unreferencedInternalIndices);
            Profiler.EndSample();

            numNewChunks = numNewChunksArray[0];

            if (numNewChunks > 0)
            {
                Profiler.BeginSample("AddNewChunks");
                int numValidNewChunks = AddNewChunks(newChunks.GetSubArray(0, numNewChunks));
                Profiler.EndSample();

                // Must make a new array so the arrays are valid and don't alias.
                hybridChunkUpdater.NewChunks = new NativeArray<ArchetypeChunk>(1, Allocator.TempJob);
                hybridChunkUpdater.PreviousBatchIndex = -1;

                var updateNewChunksJob = new UpdateNewHybridChunksJob
                {
                    NewChunks = newChunks,
                    HybridChunkInfo = GetComponentTypeHandle<HybridChunkInfo>(true),
                    ChunkWorldRenderBounds = GetComponentTypeHandle<ChunkWorldRenderBounds>(true),
                    HybridChunkUpdater = hybridChunkUpdater,
                };

#if DEBUG_LOG_INVALID_CHUNKS
                if (numValidNewChunks != numNewChunks)
                    Debug.Log($"Tried to add {numNewChunks} new chunks, but only {numValidNewChunks} were valid, {numNewChunks - numValidNewChunks} were invalid");
#endif

                hybridCompleted = updateNewChunksJob.Schedule(numValidNewChunks, kNumNewChunksPerThread);
                hybridChunkUpdater.NewChunks.Dispose(hybridCompleted);
            }

            hybridChunkUpdater.ComponentTypes.Dispose(hybridCompleted);
            newChunks.Dispose(hybridCompleted);
            numNewChunksArray.Dispose(hybridCompleted);

            // TODO: Need to wait for new chunk updating to complete, so there are no more jobs writing to the bitfields.
            // This could be optimized by splitting the memcpy (time consuming part) out from the jobs, because this
            // part would only need to wait for the metadata checking, not the memcpys.
            hybridCompleted.Complete();

            int numGpuUploadOperations = numGpuUploadOperationsArray[0];
            Debug.Assert(numGpuUploadOperations <= gpuUploadOperations.Length, "Maximum GPU upload operation count exceeded");

            ComputeUploadSizeRequirements(
                numGpuUploadOperations, gpuUploadOperations,
                out int numOperations, out int totalUploadBytes, out int biggestUploadBytes);

#if DEBUG_LOG_UPLOADS
            if (numOperations > 0)
            {
                Debug.Log($"GPU upload operations: {numOperations}, GPU upload bytes: {totalUploadBytes}");
            }
#endif
            Profiler.BeginSample("StartUpdate");
            StartUpdate(numOperations, totalUploadBytes, biggestUploadBytes);
            Profiler.EndSample();

            var uploadsExecuted = new ExecuteGpuUploads
            {
                GpuUploadOperations = gpuUploadOperations,
                ThreadedSparseUploader = m_ThreadedGPUUploader,
            }.Schedule(numGpuUploadOperations, 1);
            numGpuUploadOperationsArray.Dispose();
            gpuUploadOperations.Dispose(uploadsExecuted);

            Profiler.BeginSample("UpdateBatchProperties");
            UpdateBatchProperties(batchRequiresUpdates, batchHadMovingEntities);
            Profiler.EndSample();

            Profiler.BeginSample("UploadAllBlits");
            UploadAllBlits();
            Profiler.EndSample();

#if DEBUG_LOG_CHUNK_CHANGES
            if (numNewChunks > 0 || numRemoved > 0)
                Debug.Log($"Chunks changed, new chunks: {numNewChunks}, removed batches: {numRemoved}, batch count: {m_ExistingBatchInternalIndices.Count()}, chunk count: {m_MetaEntitiesForHybridRenderableChunks.CalculateEntityCount()}");
#endif

            // Kick the job that clears the batch AABBs for the next frame, so it
            // will be done by the time we update on the next frame.
            KickAABBClear();

            unreferencedInternalIndices.Dispose();
            batchRequiresUpdates.Dispose();
            batchHadMovingEntities.Dispose();

            uploadsExecuted.Complete();

            return uploadsExecuted;
        }

        private void ComputeUploadSizeRequirements(
            int numGpuUploadOperations, NativeArray<GpuUploadOperation> gpuUploadOperations,
            out int numOperations, out int totalUploadBytes, out int biggestUploadBytes)
        {
            numOperations = numGpuUploadOperations + m_DefaultValueBlits.Length;
            totalUploadBytes = 0;
            biggestUploadBytes = 0;

            for (int i = 0; i < numGpuUploadOperations; ++i)
            {
                var numBytes = gpuUploadOperations[i].BytesRequiredInUploadBuffer;
                totalUploadBytes += numBytes;
                biggestUploadBytes = math.max(biggestUploadBytes, numBytes);
            }

            for (int i = 0; i < m_DefaultValueBlits.Length; ++i)
            {
                var numBytes = m_DefaultValueBlits[i].BytesRequiredInUploadBuffer;
                totalUploadBytes += numBytes;
                biggestUploadBytes = math.max(biggestUploadBytes, numBytes);
            }
        }

        private int GarbageCollectUnreferencedBatches(NativeArray<long> unreferencedInternalIndices)
        {
            int numRemoved = 0;

            int firstInQw = 0;
            for (int i = 0; i < unreferencedInternalIndices.Length; ++i)
            {
                long qw = unreferencedInternalIndices[i];
                while (qw != 0)
                {
                    int setBit = math.tzcnt(qw);
                    long mask = ~(1L << setBit);
                    int internalIndex = firstInQw + setBit;

                    RemoveBatch(internalIndex);
                    ++numRemoved;

                    qw &= mask;
                }

                firstInQw += (int)AtomicHelpers.kNumBitsInLong;
            }

#if DEBUG_LOG_TOP_LEVEL
            Debug.Log($"GarbageCollectUnreferencedBatches(removed: {numRemoved})");
#endif

            return numRemoved;
        }

        struct BatchUpdateStatistics
        {
            // Ifdef struct contents to avoid warnings when ifdef is disabled.
#if DEBUG_LOG_BATCH_UPDATES
            public int BatchesWithChangedBounds;
            public int BatchesNeedingMotionVectors;
            public int BatchesWithoutMotionVectors;

            public bool NonZero =>
                BatchesWithChangedBounds > 0 ||
                BatchesNeedingMotionVectors > 0 ||
                BatchesWithoutMotionVectors > 0;
#endif
        }

        private void UpdateBatchProperties(
            NativeArray<long> batchRequiresUpdates,
            NativeArray<long> batchHadMovingEntities)
        {
            BatchUpdateStatistics updateStatistics = default;

            int firstInQw = 0;
            for (int i = 0; i < batchRequiresUpdates.Length; ++i)
            {
                long qw = batchRequiresUpdates[i];
                while (qw != 0)
                {
                    int setBit = math.tzcnt(qw);
                    long mask = (1L << setBit);
                    int internalIndex = firstInQw + setBit;

                    bool entitiesMoved = (batchHadMovingEntities[i] & mask) != 0;
                    var batchMotionInfo = (BatchMotionInfo*)m_BatchMotionInfos.GetUnsafePtr() + internalIndex;
                    int externalBatchIndex = m_InternalToExternalIds[internalIndex];

                    UpdateBatchMotionVectors(externalBatchIndex, batchMotionInfo, entitiesMoved, ref updateStatistics);

                    if (entitiesMoved)
                        UpdateBatchBounds(internalIndex, externalBatchIndex, ref updateStatistics);

                    qw &= ~mask;
                }

                firstInQw += (int)AtomicHelpers.kNumBitsInLong;
            }

#if DEBUG_LOG_BATCH_UPDATES
            if (updateStatistics.NonZero)
                Debug.Log($"Updating batch properties. Enabled motion vectors: {updateStatistics.BatchesNeedingMotionVectors}, disabled motion vectors: {updateStatistics.BatchesWithoutMotionVectors}, updated bounds: {updateStatistics.BatchesWithChangedBounds}");
#endif
        }

        private void UpdateBatchBounds(int internalIndex, int externalBatchIndex, ref BatchUpdateStatistics updateStatistics)
        {
            int aabbIndex = (int)(((uint)internalIndex) * HybridChunkUpdater.kFloatsPerAABB);
            float minX = m_BatchAABBs[aabbIndex + HybridChunkUpdater.kMinX];
            float minY = m_BatchAABBs[aabbIndex + HybridChunkUpdater.kMinY];
            float minZ = m_BatchAABBs[aabbIndex + HybridChunkUpdater.kMinZ];
            float maxX = m_BatchAABBs[aabbIndex + HybridChunkUpdater.kMaxX];
            float maxY = m_BatchAABBs[aabbIndex + HybridChunkUpdater.kMaxY];
            float maxZ = m_BatchAABBs[aabbIndex + HybridChunkUpdater.kMaxZ];

            var aabb = new MinMaxAABB
            {
                Min = new float3(minX, minY, minZ),
                Max = new float3(maxX, maxY, maxZ),
            };

            var batchBounds = (AABB)aabb;
            var batchCenter = batchBounds.Center;
            var batchSize = batchBounds.Size;

            m_BatchRendererGroup.SetBatchBounds(
                externalBatchIndex,
                new Bounds(
                    new Vector3(batchCenter.x, batchCenter.y, batchCenter.z),
                    new Vector3(batchSize.x, batchSize.y, batchSize.z)));

#if DEBUG_LOG_BATCH_UPDATES
            ++updateStatistics.BatchesWithChangedBounds;
#endif
        }

        private void UpdateBatchMotionVectors(int externalBatchIndex,
            BatchMotionInfo* batchMotionInfo,
            bool entitiesMoved,
            ref BatchUpdateStatistics updateStatistics)
        {
            if (batchMotionInfo->RequiresMotionVectorUpdates &&
                entitiesMoved != batchMotionInfo->MotionVectorFlagSet)
            {
#if UNITY_2020_1_OR_NEWER
                if (entitiesMoved)
                {
#if DEBUG_LOG_BATCH_UPDATES
                    ++updateStatistics.BatchesNeedingMotionVectors;
#endif
                    m_BatchRendererGroup.SetBatchFlags(
                        externalBatchIndex,
                        (int)BatchFlags.NeedMotionVectorPassFlag);

                    batchMotionInfo->MotionVectorFlagSet = true;
                }
                else
                {
#if DEBUG_LOG_BATCH_UPDATES
                    ++updateStatistics.BatchesWithoutMotionVectors;
#endif
                    m_BatchRendererGroup.SetBatchFlags(
                        externalBatchIndex,
                        0);

                    batchMotionInfo->MotionVectorFlagSet = false;
                }
#endif
            }
        }

        private void RemoveBatch(int internalBatchIndex)
        {
            int externalBatchIndex = m_InternalToExternalIds[internalBatchIndex];

            var batchInfo = m_BatchInfos[internalBatchIndex];
            m_BatchInfos[internalBatchIndex] = default;
            m_BatchMotionInfos[internalBatchIndex] = default;

#if USE_PICKING_MATRICES
            m_BatchPickingMatrices[internalBatchIndex] = IntPtr.Zero;
#endif

#if DEBUG_LOG_BATCHES
            Debug.Log($"RemoveBatch(internalBatchIndex: {internalBatchIndex}, externalBatchIndex: {externalBatchIndex})");
#endif

            m_BatchRendererGroup.RemoveBatch(externalBatchIndex);
            RemoveBatchIndex(internalBatchIndex, externalBatchIndex);

            ref var properties = ref batchInfo.Properties;
            for (int i = 0; i < properties.Length; ++i)
            {
                var gpuAllocation = (properties.Ptr + i)->GPUAllocation;
                if (!gpuAllocation.Empty)
                {
                    m_GPUPersistentAllocator.Release(gpuAllocation);
#if DEBUG_LOG_MEMORY_USAGE
                    Debug.Log($"RELEASE; {gpuAllocation.Length}");
#endif
                }
            }

            ref var metadataAllocations = ref batchInfo.ChunkMetadataAllocations;
            for (int i = 0; i < metadataAllocations.Length; ++i)
            {
                var metadataAllocation = metadataAllocations.Ptr[i];
                if (!metadataAllocation.Empty)
                {
                    for (ulong j = metadataAllocation.begin; j < metadataAllocation.end; ++j)
                        m_ChunkProperties[(int)j] = default;

                    m_ChunkMetadataAllocator.Release(metadataAllocation);
                }
            }

            batchInfo.Dispose();
        }

        private int AddNewChunks(NativeArray<ArchetypeChunk> newChunksX)
        {
            int numValidNewChunks = 0;

            Debug.Assert(newChunksX.Length > 0, "Attempted to add new chunks, but list of new chunks was empty");

            var hybridChunkInfoType = GetComponentTypeHandle<HybridChunkInfo>();
            // Sort new chunks by RenderMesh so we can put
            // all compatible chunks inside one batch.
            var batchCreateInfoFactory = new BatchCreateInfoFactory
            {
                EntityManager = EntityManager,
                RenderMeshTypeHandle = GetSharedComponentTypeHandle<RenderMesh>(),
                EditorRenderDataTypeHandle = GetSharedComponentTypeHandle<EditorRenderData>(),
                HybridBatchPartitionHandle = GetSharedComponentTypeHandle<HybridBatchPartition>(),
                RenderMeshFlippedWindingTagTypeHandle = GetComponentTypeHandle<RenderMeshFlippedWindingTag>(),
#if UNITY_EDITOR
                DefaultEditorRenderData = new EditorRenderData
                {SceneCullingMask = UnityEditor.SceneManagement.EditorSceneManager.DefaultSceneCullingMask},
#else
                DefaultEditorRenderData = new EditorRenderData { SceneCullingMask = ~0UL },
#endif
            };
            var batchCompatibility = new BatchCompatibility(this, batchCreateInfoFactory, newChunksX);
            // This also sorts invalid chunks to the back.
            var sortedNewChunks = batchCompatibility.SortChunks();

            int batchBegin = 0;
            int numInstances = sortedNewChunks[0].Capacity;

            for (int i = 1; i <= sortedNewChunks.Length; ++i)
            {
                int instancesInChunk = 0;
                bool breakBatch = false;

                if (i < sortedNewChunks.Length)
                {
                    var chunk = sortedNewChunks[i];
                    breakBatch = !batchCompatibility.IsCompatible(EntityManager, batchBegin, i);
                    instancesInChunk = chunk.Capacity;
                }
                else
                {
                    breakBatch = true;
                }

                if (numInstances + instancesInChunk > kMaxEntitiesPerBatch)
                    breakBatch = true;

                if (breakBatch)
                {
                    int numChunks = i - batchBegin;

                    var createInfo = batchCompatibility.CreateInfoFor(batchBegin);
                    bool valid = AddNewBatch(ref createInfo, ref hybridChunkInfoType,
                        sortedNewChunks.GetSubArray(batchBegin, numChunks), numInstances);

                    // As soon as we encounter an invalid chunk, we know that all the rest are invalid
                    // too.
                    if (valid)
                        numValidNewChunks += numChunks;
                    else
                        return numValidNewChunks;

                    batchBegin = i;
                    numInstances = instancesInChunk;
                }
                else
                {
                    numInstances += instancesInChunk;
                }
            }

            batchCompatibility.Dispose();
            sortedNewChunks.Dispose();

            return numValidNewChunks;
        }

        private BatchInfo CreateBatchInfo(ref BatchCreateInfo createInfo, NativeArray<ArchetypeChunk> chunks,
            int numInstances, Material material = null)
        {
            BatchInfo batchInfo = default;

            if (material == null)
                material = createInfo.RenderMesh.material;

            if (material == null || material.shader == null)
                return batchInfo;

#if UNITY_2020_1_OR_NEWER
            var shaderProperties = HybridV2ShaderReflection.GetDOTSInstancingProperties(material.shader);

            ref var properties = ref batchInfo.Properties;
            ref var overrideComponents = ref batchInfo.OverrideComponents;
            ref var sharedOverrideComponents = ref batchInfo.OverrideSharedComponents;
            // TODO: This can be made a Temp allocation if the to-be-released GPU allocations are stored separately
            // and preferably batched into one allocation
            properties = new UnsafeList<BatchInfo.BatchProperty>(
                shaderProperties.Length,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            overrideComponents = new UnsafeList<BatchInfo.BatchOverrideComponent>(
                shaderProperties.Length,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory);
            sharedOverrideComponents = new UnsafeList<BatchInfo.BatchOverrideComponent>(
                shaderProperties.Length,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory);
            batchInfo.ChunkMetadataAllocations = new UnsafeList<HeapBlock>(
                shaderProperties.Length,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);

            bool zeroDefault = true;
            float4x4 defaultValue = default;

            for (int i = 0; i < shaderProperties.Length; ++i)
            {
                var shaderProperty = shaderProperties[i];
                int nameID = shaderProperty.ConstantNameID;

                bool isBuiltin = shaderProperty.CbufferIndex == kBuiltinCbufferIndex;

                short sizeCPU = 0;
                int overridesStartIndex = -1;
                int sharedOverridesStartIndex = -1;

                {
                    bool foundMaterialPropertyType = m_MaterialPropertyTypes.TryGetFirstValue(
                        nameID,
                        out var materialPropertyType,
                        out var it);

                    while (foundMaterialPropertyType)
                    {
                        // There can be multiple components that override some particular NameID, so add
                        // entries for all of them.
                        if (materialPropertyType.SizeBytesGPU == shaderProperty.SizeBytes
                            || materialPropertyType.SizeBytesCPU == shaderProperty.SizeBytes
                        ) // TODO: hack to work around the property being the real size after load
                        {
                            if (overridesStartIndex < 0)
                                overridesStartIndex = overrideComponents.Length;

                            overrideComponents.Add(new BatchInfo.BatchOverrideComponent
                            {
                                BatchPropertyIndex = i,
                                TypeIndex = materialPropertyType.TypeIndex,
                            });

                            sizeCPU = materialPropertyType.SizeBytesCPU;

                            // We cannot ask default values for builtins from the material, that causes errors.
                            // Instead, check whether one was registered manually when the overriding type
                            // was registered. In case there are several overriding types, we use the first
                            // one with a registered value.
                            if (isBuiltin && zeroDefault && materialPropertyType.OverriddenDefault)
                            {
                                defaultValue = m_MaterialPropertyDefaultValues[materialPropertyType.TypeIndex];
                                zeroDefault = false;
                            }
                        }
                        else
                        {
#if USE_PROPERTY_ASSERTS
                            Debug.Log(
                                $"Shader expects property \"{m_MaterialPropertyNames[nameID]}\" to have size {shaderProperty.SizeBytes}, but overriding component \"{m_MaterialPropertyTypeNames[materialPropertyType.TypeIndex]}\" has size {materialPropertyType.SizeBytesGPU} instead.");
#endif
                        }

                        foundMaterialPropertyType =
                            m_MaterialPropertyTypes.TryGetNextValue(out materialPropertyType, ref it);
                    }
                }

                {
                    bool foundSharedMaterialPropertyType = m_MaterialPropertyTypesShared.TryGetFirstValue(
                        nameID,
                        out var sharedMaterialPropertyType,
                        out var it);

                    while (foundSharedMaterialPropertyType)
                    {
                        if (sharedMaterialPropertyType.SizeBytesGPU == shaderProperty.SizeBytes
                            || sharedMaterialPropertyType.SizeBytesCPU == shaderProperty.SizeBytes
                        ) // TODO: hack to work around the property being the real size after load
                        {
                            if (sharedOverridesStartIndex < 0)
                                sharedOverridesStartIndex = sharedOverrideComponents.Length;

                            if (sizeCPU == 0)
                            {
                                sizeCPU = sharedMaterialPropertyType.SizeBytesCPU;
                            }
                            else if (sizeCPU == sharedMaterialPropertyType.SizeBytesCPU)
                            {
                                sharedOverrideComponents.Add(new BatchInfo.BatchOverrideComponent
                                {
                                    BatchPropertyIndex = i,
                                    TypeIndex = sharedMaterialPropertyType.TypeIndex,
                                });
                            }
                            else
                            {
#if USE_PROPERTY_ASSERTS
                                Debug.Log(
                                    $"Shader expects property \"{m_MaterialPropertyNames[nameID]}\" to have size {sizeCPU}, but overriding shared component \"{m_MaterialPropertyTypeNames[sharedMaterialPropertyType.TypeIndex]}\" has size {sharedMaterialPropertyType.SizeBytesGPU} instead.");
#endif
                            }
                        }
                        else
                        {
#if USE_PROPERTY_ASSERTS
                            Debug.Log(
                                $"Shader expects property \"{m_MaterialPropertyNames[nameID]}\" to have size {shaderProperty.SizeBytes}, but overriding component \"{m_MaterialPropertyTypeNames[sharedMaterialPropertyType.TypeIndex]}\" has size {sharedMaterialPropertyType.SizeBytesGPU} instead.");
#endif
                        }

                        foundSharedMaterialPropertyType =
                            m_MaterialPropertyTypesShared.TryGetNextValue(out sharedMaterialPropertyType, ref it);
                    }
                }

                // For non-builtin properties, we can always ask the material for defaults.
                if (!isBuiltin)
                {
                    var propertyDefault = DefaultValueFromMaterial(material, nameID, shaderProperty.SizeBytes);
                    defaultValue = propertyDefault.Value;
                    zeroDefault = !propertyDefault.Nonzero;
                }

                properties.Add(new BatchInfo.BatchProperty
                {
                    MetadataOffset = shaderProperty.MetadataOffset,
                    SizeBytesCPU = sizeCPU,
                    SizeBytesGPU = (short)shaderProperty.SizeBytes,
                    CbufferIndex = shaderProperty.CbufferIndex,
                    OverrideComponentsIndex = overridesStartIndex,
                    OverrideStatus = zeroDefault
                        ? BatchPropertyOverrideStatus.SharedZeroDefault
                        : BatchPropertyOverrideStatus.SharedNonzeroDefault,
                    DefaultValue = defaultValue,
#if USE_PROPERTY_ASSERTS
                    NameID = nameID,
#endif
                });
            }

            // Check which properties have static component overrides in at least one chunk.
            for (int i = 0; i < sharedOverrideComponents.Length; ++i)
            {
                var componentType = sharedOverrideComponents.Ptr + i;
                var property = properties.Ptr + componentType->BatchPropertyIndex;

                var type = GetDynamicSharedComponentTypeHandle(ComponentType.ReadOnly(componentType->TypeIndex));

                for (int j = 0; j < chunks.Length; ++j)
                {
                    if (chunks[j].Has(type))
                    {
                        property->OverrideStatus = BatchPropertyOverrideStatus.SharedComponentOverride;
                        var propertyDefault = DefaultValueFromSharedComponent(chunks[j], type);
                        property->DefaultValue = propertyDefault.Value;
                        break;
                    }
                }
            }

            // Check which properties have overrides in at least one chunk.
            for (int i = 0; i < overrideComponents.Length; ++i)
            {
                var componentType = overrideComponents.Ptr + i;
                var property = properties.Ptr + componentType->BatchPropertyIndex;

                var type = m_ComponentTypeCache.Type(componentType->TypeIndex);

                for (int j = 0; j < chunks.Length; ++j)
                {
                    if (chunks[j].Has(type))
                    {
                        property->OverrideStatus = BatchPropertyOverrideStatus.PerEntityOverride;
                        break;
                    }
                }
            }

            for (int i = 0; i < properties.Length; ++i)
            {
                var property = properties.Ptr + i;

                if (property->OverrideStatus == BatchPropertyOverrideStatus.Uninitialized) Debug.Assert(false, "Batch property override status not initialized");

                // If the property has a default value of all zeros and isn't overridden,
                // we can use the global offset which contains zero bytes, so we don't need
                // to upload a huge amount of unnecessary zeros.
                bool needsDedicatedAllocation = PropertyRequiresAllocation(property->OverrideStatus);
                if (needsDedicatedAllocation)
                {
                    // If the property is not overridden, we only need space for a single element, the default value.
                    uint sizeBytes = (uint)PropertyValuesPerAllocation(property->OverrideStatus, numInstances) * (uint)property->SizeBytesGPU;

#if DEBUG_LOG_MEMORY_USAGE
                    Debug.Log($"ALLOCATE; {m_MaterialPropertyNames[property->NameID]}; {PropertyValuesPerAllocation(property->OverrideStatus, numInstances)}; {property->SizeBytesGPU}; {sizeBytes}");
#endif

                    property->GPUAllocation = m_GPUPersistentAllocator.Allocate(sizeBytes);
                    if (property->GPUAllocation.Empty) Debug.Assert(false, $"Out of memory in the Hybrid Renderer GPU instance data buffer. Attempted to allocate {sizeBytes}, buffer size: {m_GPUPersistentAllocator.Size}, free size left: {m_GPUPersistentAllocator.FreeSpace}.");
                }
            }
#endif

            return batchInfo;
        }

        private MaterialPropertyDefaultValue DefaultValueFromSharedComponent(ArchetypeChunk chunk, DynamicSharedComponentTypeHandle type)
        {
            return new MaterialPropertyDefaultValue(
                (chunk.GetSharedComponentDataBoxed(type, EntityManager) as IHybridSharedComponentFloat4Override)
                .GetFloat4OverrideData());
        }

        private int FindPropertyFromNameID(Shader shader, int nameID)
        {
            // TODO: this linear search should go away, but serialized property in shader is all string based so we can't use regular nameID sadly
            var count = shader.GetPropertyCount();
            for (int i = 0; i < count; ++i)
            {
                var id = shader.GetPropertyNameId(i);
                if (id == nameID)
                    return i;
            }

            return -1;
        }

        private MaterialPropertyDefaultValue DefaultValueFromMaterial(
            Material material, int nameID, int sizeBytes)
        {
            MaterialPropertyDefaultValue propertyDefaultValue = default;

            switch (sizeBytes)
            {
                case 4:
                    propertyDefaultValue = new MaterialPropertyDefaultValue(material.GetFloat(nameID));
                    break;
                // float2 and float3 are handled as float4 here
                case 8:
                case 12:
                case 16:
                    var shader = material.shader;
                    var i = FindPropertyFromNameID(shader, nameID);
                    Debug.Assert(i != -1, "Could not find property in shader");
                    var type = shader.GetPropertyType(i);
                    var flags = shader.GetPropertyFlags(i);
                    // HDR colors should never be converted, they are always linear
                    if (type == ShaderPropertyType.Color && (flags & ShaderPropertyFlags.HDR) == 0)
                    {
                        if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                            propertyDefaultValue =
                                new MaterialPropertyDefaultValue((Vector4) material.GetColor(nameID).linear);
                        else
                            propertyDefaultValue =
                                new MaterialPropertyDefaultValue((Vector4) material.GetColor(nameID).gamma);
                    }
                    else
                    {
                        propertyDefaultValue = new MaterialPropertyDefaultValue(material.GetVector(nameID));
                    }
                    break;
                case 64:
                    propertyDefaultValue = new MaterialPropertyDefaultValue((float4x4)material.GetMatrix(nameID));
                    break;
                default:
                    Debug.LogWarning($"Unsupported size for a material property with nameID {nameID}");
                    break;
            }

            return propertyDefaultValue;
        }

        private NativeList<ChunkProperty> ChunkOverriddenProperties(ref BatchInfo batchInfo, ArchetypeChunk chunk,
            int chunkStart, Allocator allocator)
        {
            ref var properties = ref batchInfo.Properties;
            ref var overrideComponents = ref batchInfo.OverrideComponents;

            var overriddenProperties = new NativeList<ChunkProperty>(properties.Length, allocator);

            int prevPropertyIndex = -1;
            int numOverridesForProperty = 0;
            int overrideIsFromIndex = -1;

            for (int i = 0; i < overrideComponents.Length; ++i)
            {
                var componentType = overrideComponents.Ptr + i;
                int propertyIndex = componentType->BatchPropertyIndex;
                var property = properties.Ptr + propertyIndex;

                if (property->OverrideStatus != BatchPropertyOverrideStatus.PerEntityOverride)
                    continue;

                if (propertyIndex != prevPropertyIndex)
                    numOverridesForProperty = 0;

                prevPropertyIndex = propertyIndex;

                if (property->GPUAllocation.Empty)
                {
                    Debug.Assert(false,
#if USE_PROPERTY_ASSERTS
                        $"No valid GPU instance data buffer allocation for property {m_MaterialPropertyNames[property->NameID]}");
#else
                        "No valid GPU instance data buffer allocation for property");
#endif
                }

                int typeIndex = componentType->TypeIndex;
                var type = m_ComponentTypeCache.Type(typeIndex);

                if (chunk.Has(type))
                {
                    // If a chunk has multiple separate overrides for a property, it is not
                    // well defined and we ignore all but one of them and possibly issue an error.
                    if (numOverridesForProperty == 0)
                    {
                        uint sizeBytes = (uint)property->SizeBytesGPU;
                        uint batchBeginOffset = (uint)property->GPUAllocation.begin;
                        uint chunkBeginOffset = batchBeginOffset + (uint)chunkStart * sizeBytes;

                        overriddenProperties.Add(new ChunkProperty
                        {
                            ComponentTypeIndex = typeIndex,
                            ValueSizeBytesCPU = property->SizeBytesCPU,
                            ValueSizeBytesGPU = property->SizeBytesGPU,
                            GPUDataBegin = (int)chunkBeginOffset,
                        });

                        overrideIsFromIndex = i;

#if DEBUG_LOG_OVERRIDES
                        Debug.Log($"Property {m_MaterialPropertyNames[property->NameID]} overridden by component {m_MaterialPropertyTypeNames[componentType->TypeIndex]}");
#endif
                    }
                    else
                    {
#if USE_PROPERTY_ASSERTS
                        Debug.Log(
                            $"Chunk has multiple overriding components for property \"{m_MaterialPropertyNames[property->NameID]}\". Override from component \"{m_MaterialPropertyTypeNames[overrideComponents.Ptr[overrideIsFromIndex].TypeIndex]}\" used, value from component \"{m_MaterialPropertyTypeNames[componentType->TypeIndex]}\" ignored.");
#endif
                    }

                    ++numOverridesForProperty;
                }
            }

            return overriddenProperties;
        }

        private bool UseErrorMaterial(ref BatchCreateInfo createInfo)
        {
#if DISABLE_HYBRID_ERROR_MATERIAL
            // If the error material is disabled, skip all checking.
            return false;
#endif

            ref var renderMesh = ref createInfo.RenderMesh;
            var material = renderMesh.material;

            // If there is no mesh, we can't use an error material at all
            if (renderMesh.mesh == null)
                return false;

            // If there is a mesh, but there is no material, then we use the error material
            if (material == null)
                return true;

            // If there is a material, and it somehow doesn't have a shader, use the error material.
            if (material.shader == null)
                return true;

            // If the shader being used has compile errors, always use the error material.
            if (ShaderHasError(material.shader))
                return true;

            // If there is a material, check whether it's using the internal error shader,
            // in that case we also use the error material.
            if (material.shader == m_BuiltinErrorShader)
                return true;

            // Otherwise, don't use the error material.
            return false;
        }

        private bool ShaderHasError(Shader shader)
        {
#if UNITY_EDITOR
            bool hasErrors = false;

            // ShaderUtil.ShaderHasError is very expensive, check if we have already called
            // it for this shader.
            if (m_ShaderHasCompileErrors.TryGetValue(shader, out hasErrors))
                return hasErrors;

            // If not, we check that status once and cache the result.
            hasErrors = ShaderUtil.ShaderHasError(shader);
            m_ShaderHasCompileErrors[shader] = hasErrors;
            return hasErrors;
#else
            // ShaderUtil is an Editor only API
            return false;
#endif
        }

        private bool AddNewBatch(ref BatchCreateInfo createInfo,
            ref ComponentTypeHandle<HybridChunkInfo> hybridChunkInfoTypeHandle,
            NativeArray<ArchetypeChunk> batchChunks,
            int numInstances)
        {
            var material = createInfo.RenderMesh.material;

            if (UseErrorMaterial(ref createInfo))
            {
                material = m_ErrorMaterial;

                if (material != null)
                    Debug.LogWarning($"WARNING: Hybrid Renderer using error shader for batch with an erroneous material. Mesh: {createInfo.RenderMesh.mesh}, Material: {createInfo.RenderMesh.material}");
            }
            else if (!createInfo.Valid)
            {
                Debug.LogWarning($"WARNING: Hybrid Renderer skipping a batch due to invalid RenderMesh. Mesh: {createInfo.RenderMesh.mesh}, Material: {createInfo.RenderMesh.material}");
                return false;
            }

            // Double check for null material, this can happen if there is a problem with
            // the error material.
            if (material == null)
            {
                Debug.LogWarning($"WARNING: Hybrid Renderer skipping a batch due to no valid material. Mesh: {createInfo.RenderMesh.mesh}, Material: {createInfo.RenderMesh.material}");
                return false;
            }

            ref var renderMesh = ref createInfo.RenderMesh;

            int externalBatchIndex = m_BatchRendererGroup.AddBatch(
                renderMesh.mesh,
                renderMesh.subMesh,
                material,
                renderMesh.layer,
                renderMesh.castShadows,
                renderMesh.receiveShadows,
                createInfo.FlippedWinding,
                createInfo.Bounds,
                numInstances,
                null,
                createInfo.EditorRenderData.PickableObject,
                createInfo.EditorRenderData.SceneCullingMask);
            int internalBatchIndex = AddBatchIndex(externalBatchIndex);

#if UNITY_2020_1_OR_NEWER
            if (renderMesh.needMotionVectorPass)
                m_BatchRendererGroup.SetBatchFlags(externalBatchIndex, (int)BatchFlags.NeedMotionVectorPassFlag);
#endif

            var batchInfo = CreateBatchInfo(ref createInfo, batchChunks, numInstances, material);
            var batchMotionInfo = new BatchMotionInfo
            {
                RequiresMotionVectorUpdates = renderMesh.needMotionVectorPass,
                MotionVectorFlagSet = renderMesh.needMotionVectorPass,
            };

#if DEBUG_LOG_BATCHES
            Debug.Log($"AddBatch(internalBatchIndex: {internalBatchIndex}, externalBatchIndex: {externalBatchIndex}, properties: {batchInfo.Properties.Length}, chunks: {batchChunks.Length}, numInstances: {numInstances}, mesh: {renderMesh.mesh}, material: {material})");
#endif

            SetBatchMetadata(externalBatchIndex, ref batchInfo, material);
            AddBlitsForSharedDefaults(ref batchInfo);

#if USE_PICKING_MATRICES
            // Picking currently uses a built-in shader that renders using traditional instancing,
            // and expects matrices in an instancing array, which is how Hybrid V1 always works.
            // To support picking, we cache a pointer into the instancing matrix array of each
            // batch, and refresh the contents whenever the DOTS side matrices change.
            // This approach relies on the instancing matrices being permanently allocated (i.e.
            // not temp allocated), which is the case at the time of writing.
            var matrixArray = m_BatchRendererGroup.GetBatchMatrices(externalBatchIndex);
            m_BatchPickingMatrices[internalBatchIndex] = (IntPtr)matrixArray.GetUnsafePtr();
#endif

            CullingComponentTypes batchCullingComponentTypes = new CullingComponentTypes
            {
                RootLODRanges = GetComponentTypeHandle<RootLODRange>(true),
                RootLODWorldReferencePoints = GetComponentTypeHandle<RootLODWorldReferencePoint>(true),
                LODRanges = GetComponentTypeHandle<LODRange>(true),
                LODWorldReferencePoints = GetComponentTypeHandle<LODWorldReferencePoint>(true),
                PerInstanceCullingTag = GetComponentTypeHandle<PerInstanceCullingTag>(true)
            };

            ref var metadataAllocations = ref batchInfo.ChunkMetadataAllocations;

            int chunkStart = 0;
            for (int i = 0; i < batchChunks.Length; ++i)
            {
                var chunk = batchChunks[i];
                AddBlitsForNotOverriddenProperties(ref batchInfo, chunk, chunkStart);
                var overriddenProperties = ChunkOverriddenProperties(ref batchInfo, chunk, chunkStart, Allocator.Temp);
                HeapBlock metadataAllocation = default;
                if (overriddenProperties.Length > 0)
                {
                    metadataAllocation = m_ChunkMetadataAllocator.Allocate((ulong)overriddenProperties.Length);
                    Debug.Assert(!metadataAllocation.Empty, "Failed to allocate space for chunk property metadata");
                    metadataAllocations.Add(metadataAllocation);
                }

                var chunkInfo = new HybridChunkInfo
                {
                    InternalIndex = internalBatchIndex,
                    ChunkTypesBegin = (int)metadataAllocation.begin,
                    ChunkTypesEnd = (int)metadataAllocation.end,
                    CullingData = ComputeChunkCullingData(ref batchCullingComponentTypes, chunk, chunkStart),
                    Valid = true,
                };

                if (overriddenProperties.Length > 0)
                {
                    UnsafeUtility.MemCpy(
                        (ChunkProperty*)m_ChunkProperties.GetUnsafePtr() + chunkInfo.ChunkTypesBegin,
                        overriddenProperties.GetUnsafeReadOnlyPtr(),
                        overriddenProperties.Length * sizeof(ChunkProperty));
                }

                chunk.SetChunkComponentData(hybridChunkInfoTypeHandle, chunkInfo);

#if DEBUG_LOG_CHUNKS
                Debug.Log($"AddChunk(chunk: {chunk.Count}, chunkStart: {chunkStart}, overriddenProperties: {overriddenProperties.Length})");
#endif


                chunkStart += chunk.Capacity;
            }

            batchInfo.OverrideComponents.Dispose();
            batchInfo.OverrideComponents = default;
            batchInfo.OverrideSharedComponents.Dispose();
            batchInfo.OverrideSharedComponents = default;

            m_BatchInfos[internalBatchIndex] = batchInfo;
            m_BatchMotionInfos[internalBatchIndex] = batchMotionInfo;

            return true;
        }

        private void SetBatchMetadata(int externalBatchIndex, ref BatchInfo batchInfo, Material material)
        {
#if UNITY_2020_1_OR_NEWER
            var metadataCbuffers = HybridV2ShaderReflection.GetDOTSInstancingCbuffers(material.shader);

            var metadataCbufferStarts = new NativeArray<int>(
                metadataCbuffers.Length,
                Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);
            var metadataCbufferLengths = new NativeArray<int>(
                metadataCbuffers.Length,
                Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);

            int totalSizeInts = 0;

            for (int i = 0; i < metadataCbuffers.Length; ++i)
            {
                int sizeInts = (int)((uint)metadataCbuffers[i].SizeBytes / sizeof(int));
                metadataCbufferStarts[i] = totalSizeInts;
                metadataCbufferLengths[i] = sizeInts;
                totalSizeInts += sizeInts;
            }

            var metadataCbufferStorage = new NativeArray<int>(
                totalSizeInts,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory);

            ref var properties = ref batchInfo.Properties;
            for (int i = 0; i < properties.Length; ++i)
            {
                var property = properties.Ptr + i;
                int offsetInInts = property->MetadataOffset / sizeof(int);
                int metadataIndex = metadataCbufferStarts[property->CbufferIndex] + offsetInInts;

                HeapBlock allocation = property->GPUAllocation;
                if (PropertyIsZero(property->OverrideStatus))
                    allocation = m_SharedZeroAllocation;

                uint metadataForProperty = PropertyHasPerEntityValues(property->OverrideStatus)
                    ? 0x80000000
                    : 0;
                metadataForProperty |= (uint)allocation.begin & 0x7fffffff;
                metadataCbufferStorage[metadataIndex] = (int)metadataForProperty;

#if DEBUG_LOG_PROPERTIES
                Debug.Log($"Property(internalBatchIndex: {m_ExternalToInternalIds[externalBatchIndex]}, externalBatchIndex: {externalBatchIndex}, property: {i}, elementSize: {property->SizeBytesCPU}, cbuffer: {property->CbufferIndex}, metadataOffset: {property->MetadataOffset}, metadata: {metadataForProperty:x8})");
#endif
            }

#if DEBUG_LOG_BATCHES
            Debug.Log($"SetBatchPropertyMetadata(internalBatchIndex: {m_ExternalToInternalIds[externalBatchIndex]}, externalBatchIndex: {externalBatchIndex}, numCbuffers: {metadataCbufferLengths.Length}, numMetadataInts: {metadataCbufferStorage.Length})");
#endif

            m_BatchRendererGroup.SetBatchPropertyMetadata(externalBatchIndex, metadataCbufferLengths,
                metadataCbufferStorage);
#endif
        }

        private HybridChunkCullingData ComputeChunkCullingData(
            ref CullingComponentTypes cullingComponentTypes,
            ArchetypeChunk chunk, int chunkStart)
        {
            var hasLodData = chunk.Has(cullingComponentTypes.RootLODRanges) &&
                chunk.Has(cullingComponentTypes.LODRanges);
            var hasPerInstanceCulling = !hasLodData || chunk.Has(cullingComponentTypes.PerInstanceCullingTag);

            return new HybridChunkCullingData
            {
                Flags = (byte)
                    ((hasLodData ? HybridChunkCullingData.kFlagHasLodData : 0) |
                        (hasPerInstanceCulling ? HybridChunkCullingData.kFlagInstanceCulling : 0)),
                BatchOffset = (short)chunkStart,
                InstanceLodEnableds = default
            };
        }

        private void AddBlitsForSharedDefaults(ref BatchInfo batchInfo)
        {
            ref var properties = ref batchInfo.Properties;
            for (int i = 0; i < properties.Length; ++i)
            {
                var property = properties.Ptr + i;

                // If the property is overridden, the batch cannot use a single shared default
                // value, as there is only a single pointer for the entire batch.
                // If the default value can be shared, but is known to be zero, we will use the
                // global offset zero, so no need to upload separately for each property.
                if (!PropertyRequiresBlit(property->OverrideStatus))
                    continue;

#if DEBUG_LOG_OVERRIDES
                Debug.Log($"Property {m_MaterialPropertyNames[property->NameID]} not overridden in batch, OverrideStatus: {property->OverrideStatus}");
#endif

                uint sizeBytes = (uint)property->SizeBytesGPU;
                uint batchBeginOffset = (uint)property->GPUAllocation.begin;

                m_DefaultValueBlits.Add(new DefaultValueBlitDescriptor
                {
                    DefaultValue = property->DefaultValue,
                    DestinationOffset = batchBeginOffset,
                    Count = 1,
                    ValueSizeBytes = sizeBytes,
                });
            }
        }

        private void AddBlitsForNotOverriddenProperties(ref BatchInfo batchInfo, ArchetypeChunk chunk, int chunkStart)
        {
            ref var properties = ref batchInfo.Properties;
            ref var overrideComponents = ref batchInfo.OverrideComponents;

            for (int i = 0; i < properties.Length; ++i)
            {
                var property = properties.Ptr + i;

                // If the property is not overridden in the batch at all, it is handled by
                // AddBlitsForSharedDefaults().
                if (property->OverrideStatus != BatchPropertyOverrideStatus.PerEntityOverride)
                    continue;

                // Loop through all components that could potentially override this property, which
                // are guaranteed to be contiguous in the array.
                int overrideIndex = property->OverrideComponentsIndex;
                bool isOverridden = false;

                if (property->GPUAllocation.Empty)
                {
                    Debug.Assert(false,
#if USE_PROPERTY_ASSERTS
                        $"No valid GPU instance data buffer allocation for property {m_MaterialPropertyNames[property->NameID]}");
#else
                        "No valid GPU instance data buffer allocation for property");
#endif
                }

                Debug.Assert(overrideIndex >= 0, "Expected a valid array index");

                while (overrideIndex < overrideComponents.Length)
                {
                    var componentType = overrideComponents.Ptr + overrideIndex;
                    if (componentType->BatchPropertyIndex != i)
                        break;

                    int typeIndex = componentType->TypeIndex;
                    var type = m_ComponentTypeCache.Type(typeIndex);

                    if (chunk.Has(type))
                    {
#if DEBUG_LOG_OVERRIDES
                        Debug.Log($"Property {m_MaterialPropertyNames[property->NameID]} IS overridden in chunk, NOT uploading default");
#endif

                        isOverridden = true;
                        break;
                    }

                    ++overrideIndex;
                }

                if (!isOverridden)
                {
#if DEBUG_LOG_OVERRIDES
                    Debug.Log($"Property {m_MaterialPropertyNames[property->NameID]} NOT overridden in chunk, uploading default");
#endif

                    uint sizeBytes = (uint)property->SizeBytesGPU;
                    uint batchBeginOffset = (uint)property->GPUAllocation.begin;
                    uint chunkBeginOffset = (uint)chunkStart * sizeBytes;

                    m_DefaultValueBlits.Add(new DefaultValueBlitDescriptor
                    {
                        DefaultValue = property->DefaultValue,
                        DestinationOffset = batchBeginOffset + chunkBeginOffset,
                        Count = (uint)chunk.Count,
                        ValueSizeBytes = sizeBytes,
                    });
                }
            }
        }

        private struct CullingComponentTypes
        {
            public ComponentTypeHandle<RootLODRange> RootLODRanges;
            public ComponentTypeHandle<RootLODWorldReferencePoint> RootLODWorldReferencePoints;
            public ComponentTypeHandle<LODRange> LODRanges;
            public ComponentTypeHandle<LODWorldReferencePoint> LODWorldReferencePoints;
            public ComponentTypeHandle<PerInstanceCullingTag> PerInstanceCullingTag;
        }

        private void UploadAllBlits()
        {
            UploadBlitJob uploadJob = new UploadBlitJob()
            {
                BlitList = m_DefaultValueBlits,
                ThreadedSparseUploader = m_ThreadedGPUUploader
            };

            JobHandle handle = uploadJob.Schedule(m_DefaultValueBlits.Length, 1);
            handle.Complete();
        }

        [BurstCompile]
        private struct AABBClearJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] public NativeArray<float> BatchAABBs;
            public void Execute(int index)
            {
                int aabbIndex = (int)(((uint)index) * HybridChunkUpdater.kFloatsPerAABB);
                BatchAABBs[aabbIndex + HybridChunkUpdater.kMinX] = float.MaxValue;
                BatchAABBs[aabbIndex + HybridChunkUpdater.kMinY] = float.MaxValue;
                BatchAABBs[aabbIndex + HybridChunkUpdater.kMinZ] = float.MaxValue;
                BatchAABBs[aabbIndex + HybridChunkUpdater.kMaxX] = float.MinValue;
                BatchAABBs[aabbIndex + HybridChunkUpdater.kMaxY] = float.MinValue;
                BatchAABBs[aabbIndex + HybridChunkUpdater.kMaxZ] = float.MinValue;
            }
        }

        // Return a JobHandle that completes when the AABBs are clear. If the job
        // hasn't been kicked (i.e. it's the first frame), then do it now.
        private JobHandle EnsureAABBsCleared()
        {
            if (!m_AABBClearKicked)
                KickAABBClear();

            return m_AABBsCleared;
        }

        private void KickAABBClear()
        {
            m_AABBsCleared = new AABBClearJob
            {
                BatchAABBs = m_BatchAABBs,
            }.Schedule(kMaxBatchCount, 64);

            m_AABBClearKicked = true;
        }

        public void CompleteJobs()
        {
            m_AABBsCleared.Complete();
            m_CullingJobDependency.Complete();
            m_CullingJobDependencyGroup.CompleteDependency();
        }

        void DidScheduleCullingJob(JobHandle job)
        {
            m_CullingJobDependency = JobHandle.CombineDependencies(job, m_CullingJobDependency);
            m_CullingJobDependencyGroup.AddDependency(job);
        }

        public void StartUpdate(int numOperations, int totalUploadBytes, int biggestUploadBytes)
        {
            var persistanceBytes = m_GPUPersistentAllocator.OnePastHighestUsedAddress;
            if (persistanceBytes > m_PersistentInstanceDataSize)
            {
                while (m_PersistentInstanceDataSize < persistanceBytes)
                {
                    m_PersistentInstanceDataSize *= 2;
                }

                if (m_PersistentInstanceDataSize > kGPUBufferSizeMax)
                {
                    m_PersistentInstanceDataSize = kGPUBufferSizeMax; // Some backends fails at loading 1024 MiB, but 1023 is fine... This should ideally be a device cap.
                }

                if(persistanceBytes > kGPUBufferSizeMax)
                    Debug.LogError("Hybrid Renderer: Current loaded scenes need more than 1GiB of persistent GPU memory. This is more than some GPU backends can allocate. Try to reduce amount of loaded data.");

                var newBuffer = new ComputeBuffer(
                    (int)m_PersistentInstanceDataSize / 4,
                    4,
                    ComputeBufferType.Raw);
                m_GPUUploader.ReplaceBuffer(newBuffer, true);

                if(m_GPUPersistentInstanceData != null)
                    m_GPUPersistentInstanceData.Dispose();
                m_GPUPersistentInstanceData = newBuffer;
            }

            m_ThreadedGPUUploader =
                m_GPUUploader.Begin(totalUploadBytes, biggestUploadBytes, numOperations);
        }

#if DEBUG_LOG_MEMORY_USAGE
        private static ulong PrevUsedSpace = 0;
#endif

        public void EndUpdate()
        {
            m_GPUUploader.EndAndCommit(m_ThreadedGPUUploader);
            // Bind compute buffer here globally
            // TODO: Bind it once to the shader of the batch!
            Shader.SetGlobalBuffer("unity_DOTSInstanceData", m_GPUPersistentInstanceData);

#if DEBUG_LOG_MEMORY_USAGE
            if (m_GPUPersistentAllocator.UsedSpace != PrevUsedSpace)
            {
                Debug.Log($"GPU memory: {m_GPUPersistentAllocator.UsedSpace / 1024.0 / 1024.0:F4} / {m_GPUPersistentAllocator.Size / 1024.0 / 1024.0:F4}");
                PrevUsedSpace = m_GPUPersistentAllocator.UsedSpace;
            }
#endif
        }
    }
}
