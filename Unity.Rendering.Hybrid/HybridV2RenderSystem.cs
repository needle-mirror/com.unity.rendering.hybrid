#define DEBUG_LOG_HYBRID_V2
// #define DEBUG_LOG_FORCED_RECREATE
// #define DEBUG_LOG_CHUNK_CHANGES
// #define DEBUG_LOG_TOP_LEVEL
// #define DEBUG_LOG_BATCHES
// #define DEBUG_LOG_BATCH_FLAG_UPDATES
// #define DEBUG_LOG_CHUNKS
// #define DEBUG_LOG_UPLOADS
// #define DEBUG_LOG_PROPERTIES
// #define DEBUG_LOG_OVERRIDES
// #define DEBUG_LOG_VISIBLE_INSTANCES
// #define DEBUG_LOG_MATERIAL_PROPERTIES
#define USE_BURST_CHUNK_UPDATES
#define USE_BURST_BLIT_UPLOAD
#define USE_FRAGMENTATION_WORKAROUND_WITH_PREFAB_CHECK
// #define USE_GATHER_TO_INIT_UNREFERENCED
// #define PROFILE_BURST_JOB_INTERNALS
#if UNITY_EDITOR || DEBUG_LOG_OVERRIDES
#define USE_PROPERTY_ASSERTS
#endif

// TODO:
// - Minimize struct sizes to improve memory footprint and cache usage
// - What to do with FrozenRenderSceneTag / ForceLowLOD?
// - Precompute and optimize material property + chunk component matching as much as possible
// - Integrate new occlusion culling
// - PickableObject?

#if ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_1_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Core;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Unity.Rendering.HybridV2;

namespace Unity.Rendering
{
    public unsafe struct CullingStats
    {
        public const int kChunkTotal = 0;
        public const int kChunkCountAnyLod = 1;
        public const int kChunkCountInstancesProcessed = 2;
        public const int kChunkCountFullyIn = 3;
        public const int kInstanceTests = 4;
        public const int kLodTotal = 5;
        public const int kLodNoRequirements = 6;
        public const int kLodChanged = 7;
        public const int kLodChunksTested = 8;
        public const int kCountRootLodsSelected = 9;
        public const int kCountRootLodsFailed = 10;
        public const int kCount = 11;
        public fixed int Stats[kCount];
        public float CameraMoveDistance;
        public fixed int CacheLinePadding[15 - kCount];
    }
    
    public struct HybridChunkInfo : IComponentData
    {
        public int InternalIndex;
        // Begin and end indices for component type metadata (modification version numbers, typeIndex values) in external arrays.
        public int ChunkTypesBegin;
        public int ChunkTypesEnd;
        // TODO: Remove this once GetComponentVersion responds to entity deletion
        public int PrevCount;
        public HybridChunkCullingData CullingData;
        public bool Valid;
        public bool NeededMotionVectors;
    }
    
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
            long currentValue = qwords[index];
            for (;;)
            {
                // If the AND wouldn't change any bits, no need to issue the atomic
                if ((currentValue & value) == currentValue)
                    return currentValue;
                
                long newValue = currentValue & value;
                long prevValue = System.Threading.Interlocked.CompareExchange(ref qwords[index], newValue, currentValue);
                
                // If the value was equal to the expected value, we know that our atomic went through
                if (prevValue == currentValue)
                    return prevValue;
                
                currentValue = prevValue;
            }
        }
        
        public static unsafe long AtomicOr(long* qwords, int index, long value)
        {
            // TODO: Replace this with atomic OR once it is available
            long currentValue = qwords[index];
            for (;;)
            {
                // If the OR wouldn't change any bits, no need to issue the atomic
                if ((currentValue | value) == currentValue)
                    return currentValue;
                
                long newValue = currentValue | value;
                long prevValue = System.Threading.Interlocked.CompareExchange(ref qwords[index], newValue, currentValue);
                
                // If the value was equal to the expected value, we know that our atomic went through
                if (prevValue == currentValue)
                    return prevValue;
                
                currentValue = prevValue;
            }
        }
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
            
            Debug.Assert(qw < UnreferencedInternalIndices.Length);
            
            AtomicHelpers.AtomicOr((long*)UnreferencedInternalIndices.GetUnsafePtr(), qw, mask);
        }
    }
    
    [BurstCompile]
    internal struct InitializeUnreferencedIndicesGatherJob : IJobParallelFor
    {
        [ReadOnly] public NativeHashMap<int, int> ExistingInternalIndices;
        public NativeArray<long> UnreferencedInternalIndices;
        
        public void Execute(int qw)
        {
            int first = (int)((uint)qw * AtomicHelpers.kNumBitsInLong);
            
            long value = 0;
            
            for (int i = 0; i < AtomicHelpers.kNumBitsInLong; ++i)
            {
                long mask = 1L << i;
                int internalIndex = first + i;
                
                if (ExistingInternalIndices.ContainsKey(internalIndex))
                    value |= mask;
            }
            
            Debug.Assert(qw < UnreferencedInternalIndices.Length);
            
            UnreferencedInternalIndices[qw] = value;
        }
    }
    
    [BurstCompile]
    internal struct HybridChunkUpdater
    {
        public const int kReferencedCountsPerThread = JobsUtility.CacheLineSize / sizeof(int);
        
        public ComponentTypeCache.BurstCompatibleTypeArray ComponentTypes;
        public ThreadedSparseUploader ThreadedSparseUploader;
        public NativeArray<long> UnreferencedInternalIndices;
        public NativeArray<long> BatchRequiresFlagUpdate;
        public NativeArray<long> BatchMotionVectorsEnabled;
        
        public NativeArray<ArchetypeChunk> NewChunks;
        public NativeArray<int> NumNewChunks;
        
        [NativeDisableParallelForRestriction]
        public NativeArray<ChunkProperty> ChunkProperties;
        
        public uint LastSystemVersion;
        
#if PROFILE_BURST_JOB_INTERNALS
        public ProfilerMarker ProfileAddUpload;
#endif
        
        public unsafe void MarkBatchForFlagUpdate(int internalIndex, bool requireMotionVectors)
        {
            AtomicHelpers.IndexToQwIndexAndMask(internalIndex, out int qw, out long mask);
            Debug.Assert(qw < BatchRequiresFlagUpdate.Length && qw < BatchMotionVectorsEnabled.Length);
            AtomicHelpers.AtomicOr((long*)BatchRequiresFlagUpdate.GetUnsafePtr(), qw, mask);
            if(requireMotionVectors)
                AtomicHelpers.AtomicOr((long*)BatchMotionVectorsEnabled.GetUnsafePtr(), qw, mask);
        }
        
        unsafe void MarkBatchAsReferenced(int internalIndex)
        {
            // If the batch is referenced, remove it from the unreferenced bitfield
            
            AtomicHelpers.IndexToQwIndexAndMask(internalIndex, out int qw, out long mask);
            
            Debug.Assert(qw < UnreferencedInternalIndices.Length);
            
            AtomicHelpers.AtomicAnd(
                (long*)UnreferencedInternalIndices.GetUnsafePtr(),
                qw,
                ~mask);
        }
        
        public void ProcessChunk(ref HybridChunkInfo chunkInfo, ArchetypeChunk chunk)
        {
#if DEBUG_LOG_CHUNKS
            Debug.Log($"HybridChunkUpdater.ProcessChunk(internalBatchIndex: {chunkInfo.InternalIndex}, valid: {chunkInfo.Valid}, count: {chunk.Count}, chunk: {chunk.GetHashCode()})");
#endif
            
            if (chunkInfo.Valid)
                ProcessExistingChunk(ref chunkInfo, chunk, false);
            else
                ProcessNewChunk(ref chunkInfo, chunk);
        }

        public unsafe void ProcessNewChunk(ref HybridChunkInfo chunkInfo, ArchetypeChunk chunk)
        {
            #if USE_FRAGMENTATION_WORKAROUND_WITH_PREFAB_CHECK
            if (chunk.Archetype.Prefab || chunk.Archetype.Disabled)
                return;
            #endif

            int* numNewChunks = (int*) NumNewChunks.GetUnsafePtr();
            int iPlus1 = System.Threading.Interlocked.Add(ref numNewChunks[0], 1);
            int i = iPlus1 - 1; // C# Interlocked semantics are weird
            Debug.Assert(i < NewChunks.Length);
            NewChunks[i] = chunk;
        }

        public unsafe void ProcessExistingChunk(ref HybridChunkInfo chunkInfo, ArchetypeChunk chunk, bool isNewChunk)
        {
            if (!isNewChunk)
                MarkBatchAsReferenced(chunkInfo.InternalIndex);
            
            // TODO: Remove entityCountChanged once GetComponentVersion responds to entity deletion
            bool entityCountChanged = chunkInfo.PrevCount != chunk.Count;
            chunkInfo.PrevCount = chunk.Count;

            fixed (ArchetypeChunkComponentTypeDynamic* fixedT0 = &ComponentTypes.t0)
            {
                for (int i = chunkInfo.ChunkTypesBegin; i < chunkInfo.ChunkTypesEnd; ++i)
                {
                    var chunkProperty = ChunkProperties[i];
                    var type = ComponentTypes.Type(fixedT0, chunkProperty.ComponentTypeIndex);

                    bool componentChanged = chunk.DidChange(type, LastSystemVersion);
                    bool copyComponentData = isNewChunk || entityCountChanged || componentChanged;

                    if (copyComponentData)
                    {
#if DEBUG_LOG_PROPERTIES
                        Debug.Log($"UpdateChunkProperty(internalBatchIndex: {chunkInfo.InternalIndex}, property: {i}, elementSize: {chunkProperty->ValueSizeBytes}, prevChangeVersion: {chunkProperty->PrevChangeVersion}, componentVersion: {componentVersion})");
#endif
                
                        var src = chunk.GetDynamicComponentDataArrayReinterpret<int>(type, chunkProperty.ValueSizeBytes);
                        
#if PROFILE_BURST_JOB_INTERNALS
                        ProfileAddUpload.Begin();
#endif
                        int sizeBytes = (int)((uint)chunk.Count * (uint)chunkProperty.ValueSizeBytes);
                        var srcPtr = src.GetUnsafeReadOnlyPtr();
                        var dstOffset = chunkProperty.GPUDataBegin;
                        ThreadedSparseUploader.AddUpload(
                            srcPtr,
                            sizeBytes,
                            dstOffset);
#if PROFILE_BURST_JOB_INTERNALS
                        ProfileAddUpload.End();
#endif
                    }
                }
            }
        }
    }
    
    [BurstCompile]
    internal struct UpdateAllHybridChunksJob : IJobChunk
    {
        public ArchetypeChunkComponentType<HybridChunkInfo> HybridChunkInfo;
        [ReadOnly] public ArchetypeChunkComponentType<ChunkHeader> ChunkHeader;
        [ReadOnly] public ArchetypeChunkComponentType<LocalToWorld> LocalToWorld;
        public HybridChunkUpdater HybridChunkUpdater;
        
        public void Execute(ArchetypeChunk metaChunk, int chunkIndex, int firstEntityIndex)
        {
            // metaChunk is the chunk which contains the meta entities (= entities holding the chunk components) for the actual chunks
            
            var hybridChunkInfos = metaChunk.GetNativeArray(HybridChunkInfo);
            var chunkHeaders = metaChunk.GetNativeArray(ChunkHeader);
            
            for (int i = 0; i < metaChunk.Count; ++i)
            {
                var chunkInfo = hybridChunkInfos[i];
                var chunkHeader = chunkHeaders[i];
                
                bool localToWorldChange = chunkHeader.ArchetypeChunk.DidChange<LocalToWorld>(LocalToWorld, HybridChunkUpdater.LastSystemVersion);
                if (chunkInfo.NeededMotionVectors != localToWorldChange)
                {
                    chunkInfo.NeededMotionVectors = localToWorldChange;
                    HybridChunkUpdater.MarkBatchForFlagUpdate(chunkInfo.InternalIndex, chunkInfo.NeededMotionVectors);
                }
                
                HybridChunkUpdater.ProcessChunk(ref chunkInfo, chunkHeader.ArchetypeChunk);
                hybridChunkInfos[i] = chunkInfo;
            }
        }
    }
    
    [BurstCompile]
    internal struct UpdateNewHybridChunksJob : IJobParallelFor
    {
        public ArchetypeChunkComponentType<HybridChunkInfo> HybridChunkInfo;
        public NativeArray<ArchetypeChunk> NewChunks;
        public HybridChunkUpdater HybridChunkUpdater;
        
        public void Execute(int index)
        {
            var chunk = NewChunks[index];
            var chunkInfo = chunk.GetChunkComponentData(HybridChunkInfo);
            chunkInfo.NeededMotionVectors = true;
            Debug.Assert(chunkInfo.Valid);
            HybridChunkUpdater.ProcessExistingChunk(ref chunkInfo, chunk, true);
            chunk.SetChunkComponentData(HybridChunkInfo, chunkInfo);
        }
    }
        
    internal struct ChunkProperty
    {
        public int ComponentTypeIndex;
        public int ValueSizeBytes;
        public int GPUDataBegin;
    }

    public struct HybridChunkCullingData
    {
        public const int kFlagHasLodData = 1 << 0;
        public const int kFlagInstanceCulling = 1 << 1;
                                                               // size  // start - end offset
        public short BatchOffset;                              //  2     2 - 4
        public ushort MovementGraceFixed16;                    //  2     4 - 6
        public byte Flags;                                     //  1     6 - 7
        public byte ForceLowLODPrevious;                       //  1     7 - 8
        public ChunkInstanceLodEnabled InstanceLodEnableds;    // 16     8 - 16
    }
    
    // Helper to only call GetArchetypeChunkComponentTypeDynamic once per type per frame
    internal struct ComponentTypeCache
    {
        internal NativeHashMap<int, int> UsedTypes;
        // Re-populated each frame with fresh objects for each used type.
        // Use C# array so we can hold SafetyHandles without problems.
        internal ArchetypeChunkComponentTypeDynamic[] TypeDynamics;
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
            Debug.Assert(!UsedTypes.ContainsKey(i) || UsedTypes[i] == typeIndex);
            UsedTypes[i] = typeIndex;
            MaxIndex = math.max(i, MaxIndex);
        }
        
        public void FetchTypeHandles(ComponentSystemBase componentSystem)
        {
            var types = UsedTypes.GetKeyValueArrays(Allocator.Temp);
            
            if (TypeDynamics == null || TypeDynamics.Length < MaxIndex + 1)
                // Allocate according to Capacity so we grow with the same geometric formula as NativeList
                TypeDynamics = new ArchetypeChunkComponentTypeDynamic[MaxIndex + 1];
            
            ref var keys = ref types.Keys;
            ref var values = ref types.Values;
            int numTypes = keys.Length;
            for (int i = 0; i < numTypes; ++i)
            {
                int arrayIndex = keys[i];
                int typeIndex = values[i];
                TypeDynamics[arrayIndex] = componentSystem.GetArchetypeChunkComponentTypeDynamic(
                    ComponentType.ReadOnly(typeIndex));
            }
            
            types.Dispose();
        }
        
        public static int GetArrayIndex(int typeIndex) => typeIndex & TypeManager.ClearFlagsMask;
        
        public ArchetypeChunkComponentTypeDynamic Type(int typeIndex)
        {
            return TypeDynamics[GetArrayIndex(typeIndex)];
        }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct BurstCompatibleTypeArray
        {
            public const int kMaxTypes = 128;
            
            [NativeDisableParallelForRestriction]
            public NativeArray<int> TypeIndexToArrayIndex;
            
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t0;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t1;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t2;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t3;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t4;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t5;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t6;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t7;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t8;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t9;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t10;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t11;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t12;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t13;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t14;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t15;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t16;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t17;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t18;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t19;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t20;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t21;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t22;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t23;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t24;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t25;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t26;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t27;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t28;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t29;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t30;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t31;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t32;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t33;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t34;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t35;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t36;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t37;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t38;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t39;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t40;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t41;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t42;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t43;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t44;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t45;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t46;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t47;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t48;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t49;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t50;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t51;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t52;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t53;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t54;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t55;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t56;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t57;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t58;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t59;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t60;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t61;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t62;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t63;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t64;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t65;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t66;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t67;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t68;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t69;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t70;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t71;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t72;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t73;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t74;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t75;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t76;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t77;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t78;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t79;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t80;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t81;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t82;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t83;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t84;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t85;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t86;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t87;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t88;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t89;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t90;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t91;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t92;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t93;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t94;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t95;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t96;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t97;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t98;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t99;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t100;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t101;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t102;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t103;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t104;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t105;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t106;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t107;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t108;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t109;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t110;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t111;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t112;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t113;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t114;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t115;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t116;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t117;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t118;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t119;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t120;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t121;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t122;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t123;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t124;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t125;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t126;
            [ReadOnly] public ArchetypeChunkComponentTypeDynamic t127;
            
            // Need to accept &t0 as input, because 'fixed' must be in the callsite.
            public unsafe ArchetypeChunkComponentTypeDynamic Type(ArchetypeChunkComponentTypeDynamic* fixedT0, int typeIndex)
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

            Debug.Assert(UsedTypeCount > 0);
            Debug.Assert(UsedTypeCount <= BurstCompatibleTypeArray.kMaxTypes);
            
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

    /// <summary>
    /// Renders all Entities containing both RenderMesh & LocalToWorld components.
    /// </summary>
    [ExecuteAlways]
    //@TODO: Necessary due to empty component group. When Component group and archetype chunks are unified this should be removed
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(UpdatePresentationSystemGroup))]
    public unsafe class HybridRendererSystem : JobComponentSystem
    {
        private EntityQuery m_CullingJobDependencyGroup;
        private EntityQuery m_CullingGroup;
        private EntityQuery m_MissingHybridChunkInfo;
        private EntityQuery m_HybridRenderedQuery;
        private EntityQuery m_DisabledRenderingQuery;

        private EntityQuery m_LodSelectGroup;

#if UNITY_EDITOR
        private EditorRenderData m_DefaultEditorRenderData = new EditorRenderData
            {SceneCullingMask = UnityEditor.SceneManagement.EditorSceneManager.DefaultSceneCullingMask};

        private uint m_PreviousDOTSReflectionVersionNumber = 0;
#else
        private EditorRenderData m_DefaultEditorRenderData = new EditorRenderData { SceneCullingMask = ~0UL };
#endif

        const int kMaxBatchCount = 64 * 1024;
        const int kMaxEntitiesPerBatch = 1023; // C++ code is restricted to a certain maximum size
        const int kNumNewChunksPerThread = 1; // TODO: Tune this
        const int kNumScatteredIndicesPerThread = 8; // TODO: Tune this
        const int kNumGatheredIndicesPerThread = 128 * 8; // Two cache lines per thread
        const int kBuiltinCbufferIndex = 0;

        const int kMaxGPUPersistentInstanceDataSize = 1024 * 1024 * 64; // 64 MB
        const int kMaxChunkMetadata = 256 * 1024;

        private enum BatchFlags
        {
            NeedMotionVectorPassFlag = 0x1
        };
    
        private const int kNeedMotionVectorPassFlag = 0x1;
        
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
        private NativeArray<ChunkProperty> m_ChunkProperties;
        private NativeHashMap<int, int> m_ExistingBatchInternalIndices;
        private ComponentTypeCache m_ComponentTypeCache;
        
        private NativeArray<int> m_InternalToExternalIds;
        private NativeArray<int> m_ExternalToInternalIds;
        private NativeList<int> m_InternalIdFreelist;
        private int m_ExternalBatchCount;
        private SortedSet<int> m_SortedInternalIds;
        
        private EntityQuery m_MetaEntitiesForHybridRenderableChunks;
        
        private NativeList<ChunkComponentCopyDescriptor> m_ChunkComponentCopyDescriptors;
        private NativeList<DefaultValueBlitDescriptor> m_DefaultValueBlits;

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
            public int SizeBytes;
        };
        NativeMultiHashMap<int, MaterialPropertyType> m_MaterialPropertyTypes;
        
        // When extra debugging is enabled, store mappings from NameIDs to property names,
        // and from type indices to type names.
        Dictionary<int, string> m_MaterialPropertyNames;
        Dictionary<int, string> m_MaterialPropertyTypeNames;
        static Dictionary<Type, string> s_TypeToPropertyMappings = new Dictionary<Type, string>();

        private bool m_FirstFrameAfterInit;

        private struct BatchCreateInfo : IEquatable<BatchCreateInfo>
        {
            public static readonly Bounds BigBounds = new Bounds(new Vector3(0, 0, 0), new Vector3(1048576.0f, 1048576.0f, 1048576.0f));
            
            public RenderMesh RenderMesh;
            public EditorRenderData EditorRenderData;
            public Bounds Bounds;
            public bool FlippedWinding;
            
            public bool Valid => RenderMesh.mesh != null && RenderMesh.material != null  && RenderMesh.material.shader != null;

            public bool Equals(BatchCreateInfo other)
            {
                return RenderMesh.Equals(other.RenderMesh) && EditorRenderData.Equals(other.EditorRenderData) && Bounds.Equals(other.Bounds) && FlippedWinding == other.FlippedWinding;
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
                    return hashCode;
                }
            }
        }
        
        private class BatchCreateInfoFactory
        {
            public EntityManager EntityManager;
            public ArchetypeChunkSharedComponentType<RenderMesh> RenderMeshType;
            public ArchetypeChunkSharedComponentType<EditorRenderData> EditorRenderDataType;
            public ArchetypeChunkComponentType<RenderMeshFlippedWindingTag> RenderMeshFlippedWindingTagType;
            public EditorRenderData DefaultEditorRenderData;
            
            public BatchCreateInfo CreateInfoForChunk(ArchetypeChunk chunk)
            {
                return new BatchCreateInfo
                {
                    RenderMesh = chunk.GetSharedComponentData(RenderMeshType, EntityManager),
                    EditorRenderData = chunk.Has(EditorRenderDataType)
                        ? chunk.GetSharedComponentData(EditorRenderDataType, EntityManager)
                        : DefaultEditorRenderData,
                    Bounds = BatchCreateInfo.BigBounds,
                    FlippedWinding = chunk.Has(RenderMeshFlippedWindingTagType),
                };
            }
        }
        
        private struct SortByBatchCompatibility : IComparer<ArchetypeChunk>
        {
            public BatchCreateInfoFactory BatchCreateInfoFactory;
            
            public int Compare(ArchetypeChunk x, ArchetypeChunk y)
            {
                var hx = BatchCreateInfoFactory.CreateInfoForChunk(x).GetHashCode();
                var hy = BatchCreateInfoFactory.CreateInfoForChunk(y).GetHashCode();
                
                if (hx < hy)
                    return -1;
                else if (hx > hy)
                    return 1;
                else
                    return 0;
            }
        }
        
        private struct BatchInfo
        {
            // There is one BatchProperty per shader property, which can be different from
            // the amount of overriding components.
            // TODO: Most of this data is no longer needed after the batch has been created, and could be
            // allocated from temp memory and freed after the batch has been created.
            internal struct BatchProperty
            {
                public int MetadataOffset;
                public int SizeBytes;
                public int CbufferIndex;
                public int OverrideComponentsIndex;
                #if USE_PROPERTY_ASSERTS
                public int NameID;
                #endif
                public bool OverriddenInBatch;
                public bool ZeroDefaultValue;
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
            
            public UnsafeList<BatchProperty> Properties;
            public UnsafeList<BatchOverrideComponent> OverrideComponents;
            public UnsafeList<HeapBlock> ChunkMetadataAllocations;

            public bool RequiresMotionVectorUpdates;
            
            public void Dispose()
            {
                if (Properties.IsCreated) Properties.Dispose();
                if (OverrideComponents.IsCreated) OverrideComponents.Dispose();
                if (ChunkMetadataAllocations.IsCreated) ChunkMetadataAllocations.Dispose();
            }
        }
        
        protected override void OnCreate()
        {
            //@TODO: Support SetFilter with EntityQueryDesc syntax
            // This component group must include all types that are being used by the culling job
            m_CullingJobDependencyGroup = GetEntityQuery(
                ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                ComponentType.ReadOnly<RootLodRequirement>(),
                ComponentType.ReadOnly<LodRequirement>(),
                ComponentType.ReadOnly<WorldRenderBounds>(),
                ComponentType.ReadOnly<ChunkHeader>(),
                ComponentType.ChunkComponentReadOnly<HybridChunkInfo>()
            );
            
            m_MissingHybridChunkInfo = GetEntityQuery(new EntityQueryDesc
            {
                All = new []
                {
                    ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                    ComponentType.ReadOnly<WorldRenderBounds>(),
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<RenderMesh>(),
                },
                None = new []
                {
                    ComponentType.ChunkComponentReadOnly<HybridChunkInfo>(),
                    ComponentType.ReadOnly<DisableRendering>(),
                },
                #if USE_FRAGMENTATION_WORKAROUND_WITH_PREFAB_CHECK
                // TODO: Add chunk component to disabled entities and prefab entities to work around
                // the fragmentation issue where entities are not added to existing chunks with chunk
                // components. Remove this once chunk components don't affect archetype matching
                // on entity creation.
                Options = EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludePrefab,
                #endif
            });
            
            m_HybridRenderedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new []
                {
                    ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                    ComponentType.ReadOnly<WorldRenderBounds>(),
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<RenderMesh>(),
                    ComponentType.ChunkComponent<HybridChunkInfo>(),
                },
            });
            
            m_DisabledRenderingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new []
                {
                    ComponentType.ReadOnly<DisableRendering>(),
                },
            });

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
            m_ForceLowLOD = new NativeArray<byte>(kMaxBatchCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_ResetLod = true;
            
            m_GPUPersistentAllocator = new HeapAllocator(kMaxGPUPersistentInstanceDataSize, 16);
            m_ChunkMetadataAllocator = new HeapAllocator(kMaxChunkMetadata);
            
            m_BatchInfos = new NativeArray<BatchInfo>(kMaxBatchCount, Allocator.Persistent);
            m_ChunkProperties = new NativeArray<ChunkProperty>(kMaxChunkMetadata, Allocator.Persistent);
            m_ExistingBatchInternalIndices = new NativeHashMap<int, int>(128, Allocator.Persistent);
            m_ComponentTypeCache = new ComponentTypeCache(128);
            
            m_ChunkComponentCopyDescriptors = new NativeList<ChunkComponentCopyDescriptor>(Allocator.Persistent);
            m_DefaultValueBlits = new NativeList<DefaultValueBlitDescriptor>(Allocator.Persistent);

            // Globally allocate a single zero matrix and reuse that for all default values that are pure zero
            m_SharedZeroAllocation = m_GPUPersistentAllocator.Allocate((ulong)sizeof(float4x4));
            Debug.Assert(!m_SharedZeroAllocation.Empty);
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
                    All = new []
                    {
                        ComponentType.ReadWrite<HybridChunkInfo>(), 
                        ComponentType.ReadOnly<ChunkHeader>(), 
                    },
                });
            
#if UNITY_EDITOR
            m_CullingStats = (CullingStats*)UnsafeUtility.Malloc(JobsUtility.MaxJobThreadCount * sizeof(CullingStats), 64, Allocator.Persistent);
#endif
            //m_MaterialPropertyBlocks = new List<MaterialPropertyBlock>();

            // Collect all components with [MaterialProperty] attribute
            m_MaterialPropertyTypes = new NativeMultiHashMap<int, MaterialPropertyType>(256, Allocator.Persistent);
            m_MaterialPropertyNames = new Dictionary<int, string>();
            m_MaterialPropertyTypeNames = new Dictionary<int, string>();
            
            // Some hardcoded mappings to avoid dependencies to Hybrid from DOTS
            RegisterMaterialPropertyType<LocalToWorld>("unity_ObjectToWorld");
            RegisterMaterialPropertyType<WorldToLocal>("unity_WorldToObject");
            
            foreach (var typeInfo in TypeManager.AllTypes)
            {
                var type = typeInfo.Type;
                if (typeof(IComponentData).IsAssignableFrom(type))
                { 
                    var attributes = type.GetCustomAttributes(typeof(MaterialPropertyAttribute), false);
                    if (attributes.Length > 0)
                    { 
                        var propertyAttr = (MaterialPropertyAttribute)attributes[0];
                        RegisterMaterialPropertyType(type, propertyAttr.Name);
                    }
                }
            }
            
            m_GPUPersistentInstanceData = new ComputeBuffer(
                kMaxGPUPersistentInstanceDataSize / 4,
                4,
                ComputeBufferType.Raw);
            m_GPUUploader = new SparseUploader(m_GPUPersistentInstanceData);
            
            m_FirstFrameAfterInit = true;
        }

        public static void RegisterMaterialPropertyType(Type type, string propertyName)
        {
            Debug.Assert(type != null);
            Debug.Assert(!string.IsNullOrEmpty(propertyName));
            
            // For now, we only support overriding one material property with one type.
            // Several types can override one property, but not the other way around.
            // If necessary, this restriction can be lifted in the future.
            if (s_TypeToPropertyMappings.ContainsKey(type))
                Debug.Assert(s_TypeToPropertyMappings[type].Equals(propertyName));
            else
                s_TypeToPropertyMappings[type] = propertyName;
        }

        public static void RegisterMaterialPropertyType<T>(string propertyName)
            where T : IComponentData
        {
            RegisterMaterialPropertyType(typeof(T), propertyName);
        }

        private void InitializeMaterialProperties()
        {
            foreach (var kv in s_TypeToPropertyMappings)
            {
                Type type = kv.Key;
                string propertyName = kv.Value;
                
                int sizeBytes = UnsafeUtility.SizeOf(type);
                int typeIndex = TypeManager.GetTypeIndex(type);
                int nameID = Shader.PropertyToID(propertyName);

                m_MaterialPropertyTypes.Add(nameID,
                    new MaterialPropertyType
                    {
                        TypeIndex = typeIndex,
                        SizeBytes = sizeBytes,
                    });

                #if USE_PROPERTY_ASSERTS
                m_MaterialPropertyNames[nameID] = propertyName;
                m_MaterialPropertyTypeNames[typeIndex] = type.Name;
                #endif
                
                #if DEBUG_LOG_MATERIAL_PROPERTIES
                Debug.Log($"Type \"{type.Name}\" ({sizeBytes} bytes) overrides material property \"{propertyName}\" (nameID: {nameID}, typeIndex: {typeIndex})");
                #endif

                // We cache all types that we know are capable of overriding properties
                m_ComponentTypeCache.UseType(typeIndex);
            }
        }
        
        protected override void OnDestroy()
        {
            CompleteJobs();
            Dispose();
        }

        JobHandle UpdateHybridV2Batches(JobHandle inputDependencies)
        {
            if (m_FirstFrameAfterInit)
            {
                OnFirstFrame();
                m_FirstFrameAfterInit = false;
            }

#if UNITY_EDITOR
            {
                uint reflectionVersionNumber = HybridV2ShaderReflection.GetDOTSReflectionVersionNumber();
                if (reflectionVersionNumber != m_PreviousDOTSReflectionVersionNumber)
                {
                    EntityManager.RemoveChunkComponentData<HybridChunkInfo>(m_HybridRenderedQuery);
                    
                    Debug.Assert(m_HybridRenderedQuery.CalculateEntityCount() == 0);
                    
#if DEBUG_LOG_FORCED_RECREATE
                    Debug.Log("New shader reflection info detected, recreating hybrid batches");
#endif
                    
                    m_PreviousDOTSReflectionVersionNumber = reflectionVersionNumber;
                }
            }
#endif
            
            Profiler.BeginSample("AddMissingChunkComponents");
            {
                EntityManager.AddComponent(m_MissingHybridChunkInfo, ComponentType.ChunkComponent<HybridChunkInfo>());
                EntityManager.RemoveChunkComponentData<HybridChunkInfo>(m_DisabledRenderingQuery);
            }
            Profiler.EndSample();
            
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
            InitializeMaterialProperties();
            
            #if DEBUG_LOG_HYBRID_V2
            Debug.Log($"Hybrid Renderer V2 active, MaterialProperty component type count {m_ComponentTypeCache.UsedTypeCount} / {ComponentTypeCache.BurstCompatibleTypeArray.kMaxTypes}");
            #endif
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            inputDeps.Complete(); // #todo
            
            CompleteJobs();
            ResetLod();

            StartUpdate();

            Profiler.BeginSample("UpdateHybridV2Batches");
            var done = UpdateHybridV2Batches(inputDeps);
            Profiler.EndSample();

            EndUpdate();

            return done;
        }

        private void ResetIds()
        {
            if (m_InternalToExternalIds.IsCreated) m_InternalToExternalIds.Dispose();
            m_InternalToExternalIds = new NativeArray<int>(kMaxBatchCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            
            if (m_ExternalToInternalIds.IsCreated) m_ExternalToInternalIds.Dispose();
            m_ExternalToInternalIds = new NativeArray<int>(kMaxBatchCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            
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
            Debug.Assert(m_InternalIdFreelist.Length > 0);
            int id = m_InternalIdFreelist[m_InternalIdFreelist.Length - 1];
            m_InternalIdFreelist.Resize(m_InternalIdFreelist.Length - 1, NativeArrayOptions.UninitializedMemory);
            Debug.Assert(!m_SortedInternalIds.Contains(id));
            m_SortedInternalIds.Add(id);
            return id;
        }
        
        internal void ReleaseInternalId(int id)
        {
            Debug.Assert(id >= 0 && id < m_InternalToExternalIds.Length);
            Debug.Assert(m_SortedInternalIds.Contains(id));
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
            Debug.Assert(m_ExternalBatchCount > 0);
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
            UnsafeUtility.Free(m_CullingStats, Allocator.Persistent);

            m_CullingStats = null;
#endif
            m_BatchRendererGroup.Dispose();
            // m_Tags.Dispose();
            m_ForceLowLOD.Dispose();
            m_ResetLod = true;
            m_MaterialPropertyTypes.Dispose();
            m_GPUPersistentAllocator.Dispose();
            m_ChunkMetadataAllocator.Dispose();
            
            m_BatchInfos.Dispose();
            m_ChunkProperties.Dispose();
            m_ExistingBatchInternalIndices.Dispose();
            m_ChunkComponentCopyDescriptors.Dispose();
            m_DefaultValueBlits.Dispose();
            m_ComponentTypeCache.Dispose();
            
            m_MetaEntitiesForHybridRenderableChunks.Dispose();
            
            if (m_InternalToExternalIds.IsCreated) m_InternalToExternalIds.Dispose();
            if (m_ExternalToInternalIds.IsCreated) m_ExternalToInternalIds.Dispose();
            if (m_InternalIdFreelist.IsCreated) m_InternalIdFreelist.Dispose();
            m_ExternalBatchCount = 0;
            m_SortedInternalIds = null;
        }

        public void Clear()
        {
            m_BatchRendererGroup.Dispose();
            m_BatchRendererGroup = new BatchRendererGroup(this.OnPerformCulling);
            m_PrevLODParams = new LODGroupExtensions.LODParams();
            m_PrevCameraPos = default(float3);
            m_PrevLodDistanceScale = 0.0f;
            m_ResetLod = true;
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
                return new JobHandle();;

            var lodParams = LODGroupExtensions.CalculateLODParams(cullingContext.lodParameters);

            Profiler.BeginSample("OnPerformCulling");

            int cullingPlaneCount = cullingContext.cullingPlanes.Length;
            int packetCount = (cullingPlaneCount + 3 )>> 2;
            var planes = FrustumPlanes.BuildSOAPlanePackets(cullingContext.cullingPlanes, Allocator.TempJob);

            JobHandle cullingDependency;
            var resetLod = m_ResetLod || (!lodParams.Equals(m_PrevLODParams));
            if (resetLod)
            {
                // Depend on all component ata we access + previous jobs since we are writing to a single
                // m_ChunkInstanceLodEnableds array.
                var lodJobDependency = JobHandle.CombineDependencies(m_CullingJobDependency, m_CullingJobDependencyGroup.GetDependency());

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
                    RootLodRequirements = GetArchetypeChunkComponentType<RootLodRequirement>(true),
                    InstanceLodRequirements = GetArchetypeChunkComponentType<LodRequirement>(true),
                    HybridChunkInfo = GetArchetypeChunkComponentType<HybridChunkInfo>(),
                    ChunkHeader = GetArchetypeChunkComponentType<ChunkHeader>(),
                    CameraMoveDistanceFixed16 = Fixed16CamDistance.FromFloatCeil(cameraMoveDistance * lodParams.distanceScale),
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

            var batchCullingStates = new NativeArray<BatchCullingState>(InternalIndexRange, Allocator.TempJob, NativeArrayOptions.ClearMemory);
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
                BatchCullingStates = batchCullingStates,
                HybridChunkInfo = GetArchetypeChunkComponentType<HybridChunkInfo>(),
                ChunkHeader = GetArchetypeChunkComponentType<ChunkHeader>(true),
                ChunkWorldRenderBounds = GetArchetypeChunkComponentType<ChunkWorldRenderBounds>(true),
                BoundsComponent = GetArchetypeChunkComponentType<WorldRenderBounds>(true),
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

            Profiler.EndSample();
            return simpleCullingJobHandle;
        }

        public JobHandle UpdateAllBatches(JobHandle inputDependencies)
        {
            int numAtStart = m_ExistingBatchInternalIndices.Count();
            
            m_DefaultValueBlits.Clear();
            m_ChunkComponentCopyDescriptors.Clear();
            
            Profiler.BeginSample("GetComponentTypes");
            var hybridRenderedChunkType = 
                GetArchetypeChunkComponentType<HybridChunkInfo>();
            m_ComponentTypeCache.FetchTypeHandles(this);
            Profiler.EndSample();
            
            int numNewChunks = 0;
            JobHandle hybridCompleted = new JobHandle();
            
            const int kNumBitsPerLong = sizeof(long) * 8;
            var unreferencedInternalIndices = new NativeArray<long>(
                (InternalIndexRange + kNumBitsPerLong) / kNumBitsPerLong,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
            
            var batchFlagUpdates = new NativeArray<long>(
                (InternalIndexRange + kNumBitsPerLong) / kNumBitsPerLong,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
            
            var batchMotionVectorsEnabled = new NativeArray<long>(
                (InternalIndexRange + kNumBitsPerLong) / kNumBitsPerLong,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
            
            JobHandle initializedUnreferenced = default;
#if USE_GATHER_TO_INIT_UNREFERENCED
            initializedUnreferenced = new InitializeUnreferencedIndicesGatherJob
            {
                ExistingInternalIndices = m_ExistingBatchInternalIndices,
                UnreferencedInternalIndices = unreferencedInternalIndices,
            }.Schedule(unreferencedInternalIndices.Length, kNumGatheredIndicesPerThread);
#else
            var existingKeys = m_ExistingBatchInternalIndices.GetKeyArray(Allocator.TempJob);
            initializedUnreferenced = new InitializeUnreferencedIndicesScatterJob
            {
                ExistingInternalIndices = existingKeys,
                UnreferencedInternalIndices = unreferencedInternalIndices,
            }.Schedule(existingKeys.Length, kNumScatteredIndicesPerThread);
            existingKeys.Dispose(initializedUnreferenced);
#endif
            
            inputDependencies = JobHandle.CombineDependencies(inputDependencies, initializedUnreferenced);
            
#if USE_BURST_CHUNK_UPDATES
            
            var newChunks = new NativeArray<ArchetypeChunk>(
                m_HybridRenderedQuery.CalculateChunkCount(),
                Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            var numNewChunksArray = new NativeArray<int>(
                1,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
            
            var hybridChunkUpdater = new HybridChunkUpdater
            {
                ComponentTypes = m_ComponentTypeCache.ToBurstCompatible(Allocator.TempJob),
                ThreadedSparseUploader =  m_ThreadedGPUUploader,
                UnreferencedInternalIndices = unreferencedInternalIndices,
                BatchRequiresFlagUpdate = batchFlagUpdates,
                BatchMotionVectorsEnabled = batchMotionVectorsEnabled,
                NewChunks = newChunks,
                NumNewChunks = numNewChunksArray,
                ChunkProperties = m_ChunkProperties,
                LastSystemVersion = LastSystemVersion,
#if PROFILE_BURST_JOB_INTERNALS
                ProfileAddUpload = new ProfilerMarker("AddUpload"),
#endif
            };
            
            var updateAllJob = new UpdateAllHybridChunksJob
            {
                HybridChunkInfo = GetArchetypeChunkComponentType<HybridChunkInfo>(false),
                ChunkHeader = GetArchetypeChunkComponentType<ChunkHeader>(true),
                LocalToWorld = GetArchetypeChunkComponentType<LocalToWorld>(true),
                HybridChunkUpdater = hybridChunkUpdater,
            };
            
            // We need to wait for the job to complete here so we can process the new chunks
            updateAllJob.Schedule(m_MetaEntitiesForHybridRenderableChunks, inputDependencies).Complete();
            numNewChunks = numNewChunksArray[0];
            
            if (numNewChunks > 0)
            {
                Profiler.BeginSample("AddNewChunks");
                AddNewChunks(newChunks.GetSubArray(0, numNewChunks));
                Profiler.EndSample();
                
                // Must make a new array so the arrays are valid and don't alias.
                hybridChunkUpdater.NewChunks = new NativeArray<ArchetypeChunk>(1, Allocator.TempJob);
                
                var updateNewChunksJob = new UpdateNewHybridChunksJob
                {
                    NewChunks = newChunks,
                    HybridChunkInfo = GetArchetypeChunkComponentType<HybridChunkInfo>(false),
                    HybridChunkUpdater = hybridChunkUpdater,
                };
                
                hybridCompleted = updateNewChunksJob.Schedule(numNewChunks, kNumNewChunksPerThread);
                hybridChunkUpdater.NewChunks.Dispose(hybridCompleted);
            }

            hybridChunkUpdater.ComponentTypes.Dispose(hybridCompleted);
            newChunks.Dispose(hybridCompleted);
            numNewChunksArray.Dispose(hybridCompleted);
            
            hybridCompleted.Complete();
            UpdateBatchFlags(batchFlagUpdates, batchMotionVectorsEnabled);
#else
            
            int numChunks = m_MetaEntitiesForHybridRenderableChunks.CalculateEntityCount();
            
            var allChunksWithHybridChunkInfo =
                m_MetaEntitiesForHybridRenderableChunks.CreateArchetypeChunkArray(Allocator.TempJob);
            
            var newChunks = new NativeArray<ArchetypeChunk>(
                numChunks,
                Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);
            
            initializedUnreferenced.Complete();
            
            // Update chunks with pre-existing batches, append the rest into
            // an array for creation.
            Profiler.BeginSample("UpdateAllChunks");
            
            var hybridChunkInfoType = GetArchetypeChunkComponentType<HybridChunkInfo>(false);
            var chunkHeaderType = GetArchetypeChunkComponentType<ChunkHeader>(true);
            
            for (int i = 0; i < allChunksWithHybridChunkInfo.Length; ++i)
            {
                var metaChunk = allChunksWithHybridChunkInfo[i];
                var hybridChunkInfos = metaChunk.GetNativeArray(hybridChunkInfoType);
                var chunkHeaders = metaChunk.GetNativeArray(chunkHeaderType);
                var pChunkInfos = (HybridChunkInfo*)hybridChunkInfos.GetUnsafePtr();
            
                for (int j = 0; j < metaChunk.Count; ++j)
                {
                    var chunk = chunkHeaders[j].ArchetypeChunk;
                    ref var chunkInfo = ref pChunkInfos[j];
                    
                    if (!chunkInfo.Valid)
                    {
                        newChunks[numNewChunks] = chunk;
                        ++numNewChunks;
                    }
                    else
                    {
                        UpdateChunk(unreferencedInternalIndices, ref chunkInfo, chunk, false);
                    }
                }
            }
            Profiler.EndSample();
            
            if (numNewChunks > 0)
            {
                Profiler.BeginSample("AddNewChunks");
                AddNewChunks(newChunks.GetSubArray(0, numNewChunks));
                
                // Update the chunks we just added
                for (int i = 0; i < numNewChunks; ++i)
                {
                    var chunk = newChunks[i];
                    var chunkInfo = chunk.GetChunkComponentData<HybridChunkInfo>(hybridRenderedChunkType);
                    UpdateChunk(unreferencedInternalIndices, ref chunkInfo, chunk, true);
                }
                Profiler.EndSample();
            }
            
            Profiler.BeginSample("UploadAllDirtyChunks");
            UploadAllDirtyChunks();
            Profiler.EndSample();
            
            allChunksWithHybridChunkInfo.Dispose();
            
#endif
            
            Profiler.BeginSample("UploadAllBlits");
            UploadAllBlits();
            Profiler.EndSample();
            
            Profiler.BeginSample("GarbageCollectUnreferencedBatches");
#if USE_BURST_CHUNK_UPDATES
            hybridCompleted.Complete();
#endif
            int numRemoved = GarbageCollectUnreferencedBatches(unreferencedInternalIndices);
            Profiler.EndSample();
            
#if DEBUG_LOG_CHUNK_CHANGES
            if (numNewChunks > 0 || numRemoved > 0)
                Debug.Log($"Chunks changed, new chunks: {numNewChunks}, removed batches: {numRemoved}, batch count: {m_ExistingBatchInternalIndices.Length}, chunk count: {m_MetaEntitiesForHybridRenderableChunks.CalculateEntityCount()}");
#endif
            
            unreferencedInternalIndices.Dispose();
            batchFlagUpdates.Dispose();
            batchMotionVectorsEnabled.Dispose();
            
            return hybridCompleted;
        }

        private void UploadAllDirtyChunks()
        {
#if DEBUG_LOG_TOP_LEVEL
            if (m_ChunkComponentCopyDescriptors.Length > 0)
                Debug.Log($"UploadAllDirtyChunks(dirtyChunks: {m_ChunkComponentCopyDescriptors.Length})");
#endif
            
            // TODO: Implement this with Burst once possible
            for (int i = 0; i < m_ChunkComponentCopyDescriptors.Length; ++i)
            {
                var copy = m_ChunkComponentCopyDescriptors[i];
                m_ThreadedGPUUploader.AddUpload(
                    copy.SourcePtr,
                    copy.SizeBytes,
                    copy.DestinationOffset
                );
                
#if DEBUG_LOG_UPLOADS
                LogUpload(
                    copy.SourcePtr,
                    copy.SizeBytes,
                    copy.DestinationOffset
                );
                Debug.Log($"Dirty chunk, size: {copy.SizeBytes}, offset: {copy.DestinationOffset}");
#endif
            }
            
            m_ChunkComponentCopyDescriptors.Clear();
        }
        
        internal static void LogUpload(void* src, int size, int destination)
        {
#if DEBUG_LOG_UPLOADS
            byte* p = (byte*)src;
            uint h = XXHash.Hash32(p, size);
            bool allZeroes = true;
            for (int i = 0; i < size; ++i)
            {
                if (p[i] != 0)
                {
                    allZeroes = false;
                    break;
                }
            }
            int end = destination + size;
            
            float* f = (float*)src;
            int numFloats = size / sizeof(float);
            string contents = Enumerable.Range(0, numFloats)
                .Select(i => f[i])
                .Aggregate("", (s, x) => $"{s}{x}, ");
            
            Debug.Log($"UPLOADER Upload {(ulong)src:x16} {size} ({destination} = 0x{destination:x8}, {end} = 0x{end:x8}), h: {h:x8}, zeroes: {allZeroes}, contents: {contents}");
#endif
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
        
        private void UpdateBatchFlags(
            NativeArray<long> batchFlagUpdates,
            NativeArray<long> batchMotionVectorsEnabled)
        {
#if DEBUG_LOG_BATCH_FLAG_UPDATES
            int batchesNeedingMotionVectors = 0;
            int batchesWithoutMotionVectors = 0;
#endif
            
            int firstInQw = 0;
            for (int i = 0; i < batchFlagUpdates.Length; ++i)
            {
                long qw = batchFlagUpdates[i];
                while (qw != 0)
                {
                    int setBit = math.tzcnt(qw);
                    long mask = (1L << setBit);
                    int internalIndex = firstInQw + setBit;

                    if (m_BatchInfos[internalIndex].RequiresMotionVectorUpdates)
                    {
                        bool needMotionVectors = (batchMotionVectorsEnabled[i] & mask) != 0;
                        int externalBatchIndex = m_InternalToExternalIds[internalIndex];
                        if (needMotionVectors)
                        {
#if DEBUG_LOG_BATCH_FLAG_UPDATES
                            ++batchesNeedingMotionVectors;
#endif
                            m_BatchRendererGroup.SetBatchFlags(
                                externalBatchIndex,
                                (int) BatchFlags.NeedMotionVectorPassFlag);
                        }
                        else
                        {
#if DEBUG_LOG_BATCH_FLAG_UPDATES
                            ++batchesWithoutMotionVectors;
#endif
                            m_BatchRendererGroup.SetBatchFlags(
                                externalBatchIndex,
                                0);
                        }
                    }

                    qw &= ~mask;
                }
                firstInQw += (int)AtomicHelpers.kNumBitsInLong;
            }
             
#if DEBUG_LOG_BATCH_FLAG_UPDATES
            if(batchesNeedingMotionVectors != 0 || batchesWithoutMotionVectors != 0)
                Debug.Log($"Settings batch flags. Batches needing motion vectors: {batchesNeedingMotionVectors}, Batches without: {batchesWithoutMotionVectors}");
#endif
        }

        private void RemoveBatch(int internalBatchIndex)
        {
            int externalBatchIndex = m_InternalToExternalIds[internalBatchIndex];
            
            var batchInfo = m_BatchInfos[internalBatchIndex];
            m_BatchInfos[internalBatchIndex] = default;
            
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
                    m_GPUPersistentAllocator.Release(gpuAllocation);
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

        private void UpdateChunk(
            NativeArray<long> unreferencedInternalIndices,
            ref HybridChunkInfo chunkInfo, ArchetypeChunk chunk,
            bool isNewChunk)
        {
            int internalIndex = chunkInfo.InternalIndex;
            
            if (!isNewChunk)
            {
                // Mark chunk as "not unreferenced", so referenced.
                AtomicHelpers.IndexToQwIndexAndMask(internalIndex, out int qw, out long mask);
                unreferencedInternalIndices[qw] &= ~mask;
            }
            
#if DEBUG_LOG_CHUNKS
            Debug.Log($"UpdateChunk(internalBatchIndex: {internalIndex}, externalBatchIndex: {m_InternalToExternalIds[internalIndex]}, chunk: {chunk.Count})");
#endif
            
            bool entityCountChanged = chunkInfo.PrevCount != chunk.Count;
            chunkInfo.PrevCount = chunk.Count;
                    
            for (int i = chunkInfo.ChunkTypesBegin; i < chunkInfo.ChunkTypesEnd; ++i)
            {
                var chunkProperty = m_ChunkProperties[i];
                var type = m_ComponentTypeCache.Type(chunkProperty.ComponentTypeIndex);
                
                bool componentChanged = chunk.DidChange(type, LastSystemVersion);
                bool copyComponentData = isNewChunk || entityCountChanged || componentChanged;
                
                if (copyComponentData)
                {
                    // Stash the raw pointer for the actual copy. This is only legal as long as the pointer
                    // is not used after structural changes can have happened. We are only using it during
                    // update so it should be fine.
                    var src = chunk.GetDynamicComponentDataArrayReinterpret<int>(type, chunkProperty.ValueSizeBytes);
                    m_ChunkComponentCopyDescriptors.Add(new ChunkComponentCopyDescriptor
                    {
                        SourcePtr = src.GetUnsafeReadOnlyPtr(),
                        DestinationOffset = chunkProperty.GPUDataBegin,
                        SizeBytes = (int)((uint)chunk.Count * (uint)chunkProperty.ValueSizeBytes),
                    });

#if DEBUG_LOG_PROPERTIES
                    Debug.Log($"UpdateChunkProperty(internalBatchIndex: {internalIndex}, externalBatchIndex: {m_InternalToExternalIds[internalIndex]} chunk: {chunk.Count}, property: {i}, type: {type}, prevChangeVersion: {chunkProperty->PrevChangeVersion}, componentVersion: {componentVersion})");
#endif
                }
            }
        }
        
        
        private void AddNewChunks(NativeArray<ArchetypeChunk> newChunks)
        {
            Debug.Assert(newChunks.Length > 0);
            
            var hybridChunkInfoType = GetArchetypeChunkComponentType<HybridChunkInfo>();
            // Sort new chunks by RenderMesh so we can put
            // all compatible chunks inside one batch.
            var batchCreateInfoFactory = new BatchCreateInfoFactory
            {
                EntityManager = EntityManager,
                RenderMeshType = GetArchetypeChunkSharedComponentType<RenderMesh>(),
                EditorRenderDataType = GetArchetypeChunkSharedComponentType<EditorRenderData>(),
                RenderMeshFlippedWindingTagType = GetArchetypeChunkComponentType<RenderMeshFlippedWindingTag>(),
#if UNITY_EDITOR
                DefaultEditorRenderData = new EditorRenderData { SceneCullingMask = UnityEditor.SceneManagement.EditorSceneManager.DefaultSceneCullingMask },
#else
                DefaultEditorRenderData = new EditorRenderData { SceneCullingMask = ~0UL },
#endif
            };
            var sortByBatchCompatibility = new SortByBatchCompatibility
            {
                BatchCreateInfoFactory = batchCreateInfoFactory
            };
            newChunks.Sort(sortByBatchCompatibility);
            
            int batchBegin = 0;
            int numInstances = newChunks[0].Capacity;
            var prevCreateInfo = batchCreateInfoFactory.CreateInfoForChunk(newChunks[0]);
            
            for (int i = 1; i <= newChunks.Length; ++i)
            {
                int instancesInChunk = 0;
                bool breakBatch = false;
                BatchCreateInfo createInfo = default;
                
                if (i < newChunks.Length)
                {
                    var chunk = newChunks[i];
                    createInfo = batchCreateInfoFactory.CreateInfoForChunk(chunk);
                    breakBatch = !prevCreateInfo.Equals(createInfo);
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
                    AddNewBatch(ref prevCreateInfo, ref hybridChunkInfoType, newChunks.GetSubArray(batchBegin, numChunks), numInstances);
                    batchBegin = i;
                    numInstances = instancesInChunk;
                }
                else
                {
                    numInstances += instancesInChunk;
                }
                
                prevCreateInfo = createInfo;
            }
        }
        
        private BatchInfo CreateBatchInfo(ref BatchCreateInfo createInfo, NativeArray<ArchetypeChunk> chunks, int numInstances)
        {
            BatchInfo batchInfo = default;
            
            var material = createInfo.RenderMesh.material;
            if (material == null || material.shader == null)
                return batchInfo;
            
            var shaderProperties = HybridV2ShaderReflection.GetDOTSInstancingProperties(material.shader);
            
            ref var properties = ref batchInfo.Properties;
            ref var overrideComponents = ref batchInfo.OverrideComponents;
            properties = new UnsafeList<BatchInfo.BatchProperty>(
                shaderProperties.Length,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            overrideComponents = new UnsafeList<BatchInfo.BatchOverrideComponent>(
                shaderProperties.Length,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
            batchInfo.ChunkMetadataAllocations = new UnsafeList<HeapBlock>(
                shaderProperties.Length,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            
            float4x4 zeroMatrix = float4x4.zero;
            
            for (int i = 0; i < shaderProperties.Length; ++i)
            {
                var shaderProperty = shaderProperties[i];
                int nameID = shaderProperty.ConstantNameID;
                
                bool isBuiltin = shaderProperty.CbufferIndex == kBuiltinCbufferIndex;
                
                bool foundMaterialPropertyType = m_MaterialPropertyTypes.TryGetFirstValue(
                    nameID,
                    out var materialPropertyType,
                    out var it);
                
                int overridesStartIndex = -1;
                
                while (foundMaterialPropertyType)
                {
                    // There can be multiple components that override some particular NameID, so add
                    // entries for all of them.
                    if (materialPropertyType.SizeBytes == shaderProperty.SizeBytes)
                    {
                        if (overridesStartIndex < 0)
                            overridesStartIndex = overrideComponents.Length;
                        
                        overrideComponents.Add(new BatchInfo.BatchOverrideComponent
                        {
                            BatchPropertyIndex = i,
                            TypeIndex = materialPropertyType.TypeIndex,
                        });
                    }
                    else
                    {
                        #if USE_PROPERTY_ASSERTS
                        Debug.Log(
                            $"Shader expects property \"{m_MaterialPropertyNames[nameID]}\" to have size {shaderProperty.SizeBytes}, but overriding component \"{m_MaterialPropertyTypeNames[materialPropertyType.TypeIndex]}\" has size {materialPropertyType.SizeBytes} instead.");
                        #endif
                    }
                    
                    
                    foundMaterialPropertyType = m_MaterialPropertyTypes.TryGetNextValue(out materialPropertyType, ref it);
                }
                
                bool zeroDefault;
                float4x4 defaultValue = default;
                
                // We cannot ask default values for builtins from the material, that causes errors
                if (isBuiltin)
                {
                    zeroDefault = true;
                }
                else
                {
                    defaultValue = MaterialPropertyDefaultValue(material, nameID, shaderProperty.SizeBytes);
                    zeroDefault = UnsafeUtility.MemCmp(
                                      &defaultValue, &zeroMatrix, sizeof(float4x4)) == 0;
                }
                
                properties.Add(new BatchInfo.BatchProperty
                {
                    MetadataOffset = shaderProperty.MetadataOffset,
                    SizeBytes = shaderProperty.SizeBytes,
                    CbufferIndex = shaderProperty.CbufferIndex,
                    OverrideComponentsIndex = overridesStartIndex,
                    OverriddenInBatch = false,
                    ZeroDefaultValue = zeroDefault,
                    DefaultValue = defaultValue,
                    #if USE_PROPERTY_ASSERTS
                    NameID = nameID,
                    #endif
                });
                
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
                        property->OverriddenInBatch = true;
                        break;
                    }
                }
            }
            
            for (int i = 0; i < properties.Length; ++i)
            {
                var property = properties.Ptr + i;
                
                // If the property has a default value of all zeros and isn't overridden,
                // we can use the global offset which contains zero bytes, so we don't need
                // to upload a huge amount of unnecessary zeros.
                bool needsDedicatedAllocation = property->OverriddenInBatch || !property->ZeroDefaultValue;
                if (needsDedicatedAllocation)
                {
                    uint sizeBytes = (uint)numInstances * (uint)property->SizeBytes;
                    property->GPUAllocation = m_GPUPersistentAllocator.Allocate(sizeBytes);
                    Debug.Assert(!property->GPUAllocation.Empty);
                }
            }
            
            return batchInfo;
        }

        private float4x4 MaterialPropertyDefaultValue(
            Material material, int nameID, int sizeBytes)
        {
            float4x4 propertyDefaultValue = default;
            
            switch (sizeBytes)
            {
                case 4:
                    var s = material.GetFloat(nameID);
                    propertyDefaultValue[0] = s;
                    break;
                case 16:
                    var v = material.GetVector(nameID);
                    propertyDefaultValue.c0 = v;
                    break;
                case 64:
                    propertyDefaultValue = (float4x4) material.GetMatrix(nameID);
                    break;
            }
            
            return propertyDefaultValue;
        }

        private NativeList<ChunkProperty> ChunkOverriddenProperties(ref BatchInfo batchInfo, ArchetypeChunk chunk, int chunkStart, Allocator allocator)
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
                
                if (!property->OverriddenInBatch)
                    continue;
                
                if (propertyIndex != prevPropertyIndex)
                    numOverridesForProperty = 0;
                
                prevPropertyIndex = propertyIndex;
                
                Debug.Assert(!property->GPUAllocation.Empty);
                
                int typeIndex = componentType->TypeIndex;
                var type = m_ComponentTypeCache.Type(typeIndex);
                
                if (chunk.Has(type))
                {
                    // If a chunk has multiple separate overrides for a property, it is not
                    // well defined and we ignore all but one of them and possibly issue an error.
                    if (numOverridesForProperty == 0)
                    {
                        uint sizeBytes = (uint)property->SizeBytes;
                        uint batchBeginOffset = (uint)property->GPUAllocation.begin;
                        uint chunkBeginOffset = batchBeginOffset + (uint)chunkStart * sizeBytes;
                        
                        overriddenProperties.Add(new ChunkProperty
                        {
                            ComponentTypeIndex = typeIndex,
                            ValueSizeBytes = property->SizeBytes,
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
                        Debug.Log($"Chunk has multiple overriding components for property \"{m_MaterialPropertyNames[property->NameID]}\". Override from component \"{m_MaterialPropertyTypeNames[overrideComponents.Ptr[overrideIsFromIndex].TypeIndex]}\" used, value from component \"{m_MaterialPropertyTypeNames[componentType->TypeIndex]}\" ignored.");
                        #endif
                    }
                    
                    ++numOverridesForProperty;
                }
            }
            
            return overriddenProperties;
        }
        
        private void AddNewBatch(ref BatchCreateInfo createInfo,
            ref ArchetypeChunkComponentType<HybridChunkInfo> hybridChunkInfoType,
            NativeArray<ArchetypeChunk> batchChunks,
            int numInstances)
        {
            if (!createInfo.Valid)
                return;
            
            ref var renderMesh = ref createInfo.RenderMesh;
            
            int externalBatchIndex = m_BatchRendererGroup.AddBatch(
                renderMesh.mesh,
                renderMesh.subMesh,
                renderMesh.material,
                renderMesh.layer,
                renderMesh.castShadows,
                renderMesh.receiveShadows,
                createInfo.FlippedWinding,
                createInfo.Bounds,
                numInstances,
                null,
                null,
                // TODO: This should probably be implemented at some point?
                // createInfo.EditorRenderData.PickableObject,
                createInfo.EditorRenderData.SceneCullingMask);
            int internalBatchIndex = AddBatchIndex(externalBatchIndex);

            if (renderMesh.needMotionVectorPass)
                m_BatchRendererGroup.SetBatchFlags(externalBatchIndex, 1);

            var batchInfo = CreateBatchInfo(ref createInfo, batchChunks, numInstances);

            batchInfo.RequiresMotionVectorUpdates = renderMesh.needMotionVectorPass;
            
#if DEBUG_LOG_BATCHES
            Debug.Log($"AddBatch(internalBatchIndex: {internalBatchIndex}, externalBatchIndex: {externalBatchIndex}, properties: {batchInfo.Properties.Length}, chunks: {batchChunks.Length}, numInstances: {numInstances}, mesh: {renderMesh.mesh}, material: {renderMesh.material})");
#endif
            
            SetBatchMetadata(externalBatchIndex, ref batchInfo, renderMesh.material);
            AddBlitsForSharedDefaults(ref batchInfo);
            
            CullingComponentTypes batchCullingComponentTypes = new CullingComponentTypes
            {
                RootLodRequirements = GetArchetypeChunkComponentType<RootLodRequirement>(true),
                InstanceLodRequirements = GetArchetypeChunkComponentType<LodRequirement>(true),
                PerInstanceCullingTag = GetArchetypeChunkComponentType<PerInstanceCullingTag>(true)
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
                    Debug.Assert(!metadataAllocation.Empty);
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
                        (ChunkProperty *)m_ChunkProperties.GetUnsafePtr() + chunkInfo.ChunkTypesBegin,
                        overriddenProperties.GetUnsafeReadOnlyPtr(),
                        overriddenProperties.Length * sizeof(ChunkProperty));
                }
                
                chunk.SetChunkComponentData(hybridChunkInfoType, chunkInfo);
                
#if DEBUG_LOG_CHUNKS
                Debug.Log($"AddChunk(chunk: {chunk.Count}, chunkStart: {chunkStart}, overriddenProperties: {overriddenProperties.Length})");
#endif
                
                
                chunkStart += chunk.Capacity;
            }
            
            batchInfo.OverrideComponents.Dispose();
            batchInfo.OverrideComponents = default;
            
            m_BatchInfos[internalBatchIndex] = batchInfo;
        }

        private void SetBatchMetadata(int externalBatchIndex, ref BatchInfo batchInfo, Material material)
        {
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
                if (!property->OverriddenInBatch && property->ZeroDefaultValue)
                    allocation = m_SharedZeroAllocation;
                
                uint metadataForProperty = property->OverriddenInBatch
                    ? 0x80000000
                    : 0;
                metadataForProperty |= (uint)allocation.begin & 0x7fffffff;
                metadataCbufferStorage[metadataIndex] = (int)metadataForProperty;
                
#if DEBUG_LOG_PROPERTIES
                Debug.Log($"Property(internalBatchIndex: {m_ExternalToInternalIds[externalBatchIndex]}, externalBatchIndex: {externalBatchIndex}, property: {i}, elementSize: {property->SizeBytes}, cbuffer: {property->CbufferIndex}, metadataOffset: {property->MetadataOffset}, metadata: {metadataForProperty:x8})");
#endif
            }
            
#if DEBUG_LOG_BATCHES
            Debug.Log($"SetBatchPropertyMetadata(internalBatchIndex: {m_ExternalToInternalIds[externalBatchIndex]}, externalBatchIndex: {externalBatchIndex}, numCbuffers: {metadataCbufferLengths.Length}, numMetadataInts: {metadataCbufferStorage.Length})");
#endif
            
            m_BatchRendererGroup.SetBatchPropertyMetadata(externalBatchIndex, metadataCbufferLengths, metadataCbufferStorage);
        }

        private HybridChunkCullingData ComputeChunkCullingData(
            ref CullingComponentTypes cullingComponentTypes,
            ArchetypeChunk chunk, int chunkStart)
        {
            var hasLodData = chunk.Has(cullingComponentTypes.RootLodRequirements) && chunk.Has(cullingComponentTypes.InstanceLodRequirements);
            var hasPerInstanceCulling = !hasLodData || chunk.Has(cullingComponentTypes.PerInstanceCullingTag);
            
            return new HybridChunkCullingData
            {
                Flags = (byte)
                    ((hasLodData ? HybridChunkCullingData.kFlagHasLodData : 0) |
                    (hasPerInstanceCulling ? HybridChunkCullingData.kFlagInstanceCulling : 0)),
                BatchOffset = (short) chunkStart,
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
                if (property->OverriddenInBatch)
                    continue;
                
                #if DEBUG_LOG_OVERRIDES
                Debug.Log($"Property {m_MaterialPropertyNames[property->NameID]} not overridden in batch, ZeroDefaultValue: {property->ZeroDefaultValue}");
                #endif
                
                // If the default value can be shared, but is known to be zero, we will use the
                // global offset zero, so no need to upload separately for each property.
                if (property->ZeroDefaultValue)
                    continue;
                
                uint sizeBytes = (uint)property->SizeBytes;
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
                if (!property->OverriddenInBatch)
                    continue;
                
                // Loop through all components that could potentially override this property, which
                // are guaranteed to be contiguous in the array.
                int overrideIndex = property->OverrideComponentsIndex;
                bool isOverridden = false;
                
                Debug.Assert(!property->GPUAllocation.Empty);
                Debug.Assert(overrideIndex >= 0);
                
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
                
                    uint sizeBytes = (uint)property->SizeBytes;
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
            public ArchetypeChunkComponentType<RootLodRequirement> RootLodRequirements;
            public ArchetypeChunkComponentType<LodRequirement> InstanceLodRequirements;
            public ArchetypeChunkComponentType<PerInstanceCullingTag> PerInstanceCullingTag;
        }
        
        private struct ChunkComponentCopyDescriptor
        {
            internal void *SourcePtr;
            internal int DestinationOffset;
            internal int SizeBytes;
        }
        
        private struct DefaultValueBlitDescriptor
        {
            internal float4x4 DefaultValue;
            internal uint DestinationOffset;
            internal uint ValueSizeBytes;
            internal uint Count;
        }

        [BurstCompile]
        struct UploadBlitJob : IJobParallelFor
        {
            [ReadOnly] public NativeList<DefaultValueBlitDescriptor> BlitList;
            public ThreadedSparseUploader ThreadedGpuUploader;
           
            public void Execute(int index)
            {
                DefaultValueBlitDescriptor blit = BlitList[index];
                if (blit.Count > 1)
                {
                    var replicatedDefault = new NativeList<byte>(128 * 64, Allocator.Temp);
                    int totalSize = (int)(blit.Count * blit.ValueSizeBytes);
                    replicatedDefault.ResizeUninitialized(totalSize);
                    var scratch = replicatedDefault.GetUnsafePtr();
                    UnsafeUtility.MemCpyReplicate(
                        scratch,
                        &blit.DefaultValue,
                        (int)blit.ValueSizeBytes,
                        (int)blit.Count);
                    ThreadedGpuUploader.AddUpload(
                        scratch,
                        totalSize,
                        (int)blit.DestinationOffset);
                }
                else
                {
                    ThreadedGpuUploader.AddUpload(
                        &blit.DefaultValue,
                        (int)blit.ValueSizeBytes,
                        (int)blit.DestinationOffset);
                }
            }
        }

        private void UploadAllBlits()
        {
#if USE_BURST_BLIT_UPLOAD
            UploadBlitJob uploadJob = new UploadBlitJob()
            {
                BlitList = m_DefaultValueBlits,
                ThreadedGpuUploader = m_ThreadedGPUUploader
            };
            
            JobHandle handle = uploadJob.Schedule(m_DefaultValueBlits.Length, 1);
            handle.Complete();
#else
            var replicatedDefault = new NativeList<byte>(128 * 64, Allocator.Temp);
            
#if DEBUG_LOG_TOP_LEVEL
            if (m_DefaultValueBlits.Length > 0)
                Debug.Log($"UploadAllBlits(blits: {m_DefaultValueBlits.Length})");
#endif
            
            for (int i = 0; i < m_DefaultValueBlits.Length; ++i)
            {
                var blit = m_DefaultValueBlits[i];
                
                // TODO: Implement this on the GPU at some point in the future
                if (blit.Count > 1)
                {
                    int totalSize = (int)(blit.Count * blit.ValueSizeBytes);
                    replicatedDefault.ResizeUninitialized(totalSize);
                    var scratch = replicatedDefault.GetUnsafePtr();
                    UnsafeUtility.MemCpyReplicate(
                        scratch,
                        &blit.DefaultValue,
                        (int)blit.ValueSizeBytes,
                        (int)blit.Count);
                    m_ThreadedGPUUploader.AddUpload(
                        scratch,
                        totalSize,
                        (int)blit.DestinationOffset);
                    LogUpload(
                        scratch,
                        totalSize,
                        (int)blit.DestinationOffset);
                    
#if DEBUG_LOG_UPLOADS
                    Debug.Log($"Default value replicate, size: {totalSize}, offset: {blit.DestinationOffset}, value: {blit.DefaultValue}");
#endif
                }
                else
                {
                    m_ThreadedGPUUploader.AddUpload(
                        &blit.DefaultValue,
                        (int)blit.ValueSizeBytes,
                        (int)blit.DestinationOffset);
                    LogUpload(
                        &blit.DefaultValue,
                        (int)blit.ValueSizeBytes,
                        (int)blit.DestinationOffset);
                    
#if DEBUG_LOG_UPLOADS
                    Debug.Log($"Shared default value, size: {blit.ValueSizeBytes}, offset: {blit.DestinationOffset}, value: {blit.DefaultValue}");
#endif
                }
            }
            
            replicatedDefault.Dispose();
            m_DefaultValueBlits.Clear();
#endif
        }
        
        public void CompleteJobs()
        {
            m_CullingJobDependency.Complete();
            m_CullingJobDependencyGroup.CompleteDependency();
        }


        void DidScheduleCullingJob(JobHandle job)
        {
            m_CullingJobDependency = JobHandle.CombineDependencies(job, m_CullingJobDependency);
            m_CullingJobDependencyGroup.AddDependency(job);
        }

        public void StartUpdate()
        {
            m_ThreadedGPUUploader = m_GPUUploader.Begin(kMaxGPUPersistentInstanceDataSize, kMaxGPUPersistentInstanceDataSize/4);
            // Debug.Log($"GPU allocator: {(double)m_GPUPersistentAllocator.UsedSpace / (double)m_GPUPersistentAllocator.Size * 100.0}%");
        }

        public void EndUpdate()
        {
            m_GPUUploader.EndAndCommit(m_ThreadedGPUUploader);
            // Bind compute buffer here globally
            // TODO: Bind it once to the shader of the batch!
            Shader.SetGlobalBuffer("unity_DOTSInstanceData", m_GPUPersistentInstanceData);
        }
    }
}

#endif // ENABLE_HYBRID_RENDERER_V2
