using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;

namespace Unity.Rendering
{
    struct HLODManager
    {
        TransformAccessArray       _Transforms;
        NativeList<Data>           _Data;
        List<References>           _HLODs;
        NativeList<LODLevelChange> _ChangesCached;

        static ProfilerMarker     _UpdateMarker = new ProfilerMarker("HLOD.Update");
        static ProfilerMarker     _ApplyChangesMarker = new ProfilerMarker("ApplyChanges");


        struct References
        {
            public HLOD          HLOD;
            public LODGroup      LODGroup;
            public LODGroup[][]  ChildLODGroups;
        }

        struct Data
        {
            public float4   LODDistances;
            public int      ActiveLODLevel;
            public float    GlobalScale;
            public float3   GlobalReferencePoint;
            public float3   LocalReferencePoint;

        }
        struct LODLevelChange
        {
            public int LODLevel;
            public int LODIndex;
        }

        unsafe void ApplyChange(in LODLevelChange change)
        {
            var lodgroups = _HLODs[change.LODIndex].ChildLODGroups;
            var lodIndex = change.LODLevel;

            for (int j = 0; j != lodgroups.Length; j++)
            {
                bool visible = j == lodIndex;
                var childGroups = lodgroups[j];

                for (var g = 0; g != childGroups.Length; g++)
                {
                    var subGroup = childGroups[g];
                    if (subGroup != null)
                    {
                        if (visible)
                            subGroup.ForceLOD(-1);
                        else
                            subGroup.ForceLOD(int.MaxValue);
                    }
                }
            }
        }

        unsafe void ApplyChanges(NativeArray<LODLevelChange> changes)
        {
            _ApplyChangesMarker.Begin();

            for (int c = 0; c != changes.Length; c++)
                ApplyChange(changes[c]);

            _ApplyChangesMarker.End();
        }

        public void Update(in LODGroupExtensions.LODParams lodParams, bool selectionChanged, Transform selection)
        {
            if (!_Data.IsCreated)
                return;

            bool isEditMode = !Application.isPlaying;

            using (_UpdateMarker.Auto())
            {
                var jobHandle = UpdateWorldReferencePoints();
                Update(lodParams, selectionChanged && isEditMode, _ChangesCached, jobHandle);
                ApplyChanges(_ChangesCached);
            }

            if (isEditMode && (selectionChanged || _ChangesCached.Length != 0))
                ForceHLODSelection(selection);
        }

        void ForceHLODSelection(Transform selection)
        {
            if (selection == null)
                return;

            // We only do lod forcing if we are inside of a HLOD hierarchy?
            var hlodSelection = selection.GetComponentInParent<HLOD>();
            if (hlodSelection == null || hlodSelection._Index == -1 || selection == hlodSelection.transform)
                return;

            var lodParents = hlodSelection.LODParentTransforms;
            if (lodParents == null)
                return;

            for (int p = 0; p != lodParents.Length; p++)
            {
                // The selection is a child of this lod parent
                if (lodParents[p] != null && selection.IsChildOf(lodParents[p]))
                {
                    // So we force lod for the hlod
                    hlodSelection.GetComponent<LODGroup>().ForceLOD(p);
                    // And all HLOD groups
                    ApplyChange(new LODLevelChange {LODIndex = hlodSelection._Index, LODLevel = p});

                    // But the user might also be selecting a specific lod group renderer.
                    // If he is, also force select that lod level
                    selection.TryGetComponent<Renderer>(out var selectedRenderer);
                    if (selectedRenderer != null)
                    {
                        var parentLodGroup = selection.GetComponentInParent<LODGroup>();
                        if (parentLodGroup != null)
                        {
                            var lods = parentLodGroup.GetLODs();
                            for (int l = 0; l < lods.Length; l++)
                            {
                                if (lods[l].renderers.Contains(selectedRenderer))
                                {
                                    parentLodGroup.ForceLOD(l);
                                    return;
                                }
                            }
                        }
                    }
                    return;
                }
            }
        }

        void Update(in LODGroupExtensions.LODParams lodParams, bool ForceAllChanges, NativeList<LODLevelChange> changes, JobHandle deps)
        {
            var job = new UpdateLODChangesJob
            {
                ForceAllChanges = ForceAllChanges,
                Data = _Data,
                Changes = changes,
                LodParams = lodParams
            };

            job.Schedule(deps).Complete();
        }


        [BurstCompile]
        struct UpdateLODChangesJob : IJob
        {
            public bool                         ForceAllChanges;
            public NativeArray<Data>            Data;
            public NativeList<LODLevelChange>   Changes;
            public LODGroupExtensions.LODParams LodParams;

            public void Execute()
            {
                Changes.Clear();

                for (int i = 0; i != Data.Length; i++)
                {
                    var data = Data[i];
                    int lodIndex = LODGroupExtensions.CalculateCurrentLODIndex(data.LODDistances, data.GlobalScale, data.GlobalReferencePoint, ref LodParams);

                    if (lodIndex != data.ActiveLODLevel || ForceAllChanges)
                    {
                        Changes.Add(new LODLevelChange
                        {
                            LODLevel = lodIndex,
                            LODIndex = i
                        });

                        data.ActiveLODLevel = lodIndex;
                        Data[i] = data;
                    }
                }
            }
        }

        [BurstCompile]
        struct UpdateWorldReferencePointsJob : IJobParallelForTransform
        {
            public NativeArray<Data> Data;
            public void Execute(int index, TransformAccess transform)
            {
                var matrix = transform.localToWorldMatrix;
                var data = Data[index];
                data.GlobalReferencePoint = matrix.MultiplyPoint(Data[index].LocalReferencePoint);
                data.GlobalScale = math.cmax(math.abs(CalculateLossyScale(matrix, transform.rotation)));

                Data[index] = data;
            }

            static float3 CalculateLossyScale(float4x4 matrix, quaternion rotation)
            {
                float4x4 m4x4 = matrix;
                float3x3 invR = new float3x3(math.conjugate(rotation));
                float3x3 gsm = new float3x3 { c0 = m4x4.c0.xyz, c1 = m4x4.c1.xyz, c2 = m4x4.c2.xyz };
                float3x3 scale = math.mul(invR, gsm);
                float3 globalScale = new float3(scale.c0.x, scale.c1.y, scale.c2.z);
                return globalScale;
            }
        }

        JobHandle UpdateWorldReferencePoints()
        {
            var jobData = new UpdateWorldReferencePointsJob {Data = _Data};
#if UNITY_2020_2_OR_NEWER
            return jobData.ScheduleReadOnly(_Transforms, 32);
#else
            return jobData.Schedule(_Transforms);

#endif
        }

        static void UpdateLODData(LODGroup lodGroup, LOD[] lods, out Data data)
        {
            var worldSpaceSize = lodGroup.size;

            data.LODDistances = new float4(float.PositiveInfinity);
            for (int i = 0; i != lods.Length; i++)
                data.LODDistances[i] = worldSpaceSize / lods[i].screenRelativeTransitionHeight;
            data.ActiveLODLevel = -1;
            data.LocalReferencePoint = lodGroup.localReferencePoint;
            // Updated every frame...
            data.GlobalReferencePoint = float3.zero;
            data.GlobalScale = 1.0F;
        }

        public bool Add(HLOD hlod)
        {
            if (hlod._Index != -1)
                throw new ArgumentException("hlod._Index != -1");

            var lodGroup = hlod.GetComponent<LODGroup>();

            var lods = lodGroup.GetLODs();

            if (lods.Length > 4)
            {
                Debug.LogWarning("Only 4 LOD levels are supported");
                return false;
            }

            References refs;
            refs.HLOD = hlod;
            refs.LODGroup = hlod.GetComponent<LODGroup>();
            refs.ChildLODGroups = new LODGroup[lods.Length][];
            for (int i = 0; i != refs.ChildLODGroups.Length; i++)
                refs.ChildLODGroups[i] = hlod.CalculateLODGroups(i);

            Data data;
            UpdateLODData(lodGroup, lods, out data);

            InitializeIfEmpty();

            hlod._Index = _Data.Length;
            _Data.Add(data);
            _Transforms.Add(hlod.transform);
            _HLODs.Add(refs);

            return true;
        }

        public void Remove(HLOD hlod)
        {
            var oldIndex = hlod._Index;
            if (oldIndex == -1)
                return;

            _HLODs[_HLODs.Count - 1].HLOD._Index = oldIndex;
            hlod._Index = -1;

            _Data.RemoveAtSwapBack(oldIndex);
            _Transforms.RemoveAtSwapBack(oldIndex);
            _HLODs.RemoveAtSwapBack(oldIndex);

            // Cleanup is driven by there being no hlods left as opposed to shutdown callback on domain reload
            CleanupIfEmpty();
        }

        public void UpdateLODData(HLOD hlod)
        {
            if (hlod._Index == -1)
                return;

            var group = _HLODs[hlod._Index].LODGroup;
            UpdateLODData(group, group.GetLODs(), out var data);
            _Data[hlod._Index] = data;
        }
        public void Update(HLOD hlod)
        {
            Remove(hlod);
            if (hlod.isActiveAndEnabled)
                Add(hlod);
        }

        void InitializeIfEmpty()
        {
            if (!_Data.IsCreated)
            {
                _Data = new NativeList<Data>(1024, Allocator.Persistent);
                _Transforms = new TransformAccessArray(1024);
                _ChangesCached = new NativeList<LODLevelChange>(128, Allocator.Persistent);
            }

            if (_HLODs == null)
                _HLODs = new List<References>();
        }

        void CleanupIfEmpty()
        {
            if (_Data.Length == 0)
            {
                _Data.Dispose();
                _Transforms.Dispose();
                _ChangesCached.Dispose();
            }
        }
    }
}
