using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

#pragma warning disable 414

namespace Unity.Rendering
{
    [UnityEngine.ExecuteAlways]
    [RequireComponent(typeof(LODGroup))]
    public class HLOD : MonoBehaviour
    {
        static HLODManager _Manager;
        static bool        _DidInitialize;
        static bool        _DidSelectionChange;
        static List<HLOD>  _CachedHLODList = new List<HLOD>();

        [Tooltip("Store lowest LOD in scene section 0, and the rest in section 1.")]
        public bool autoLODSections = true;

        [SerializeField] Transform[] m_LODParentTransforms;

        [NonSerialized] internal int _Index = -1;


        public Transform[] LODParentTransforms
        {
            get { return m_LODParentTransforms; }
            set { m_LODParentTransforms = value; }
        }

        public LODGroup[] CalculateLODGroups(int index)
        {
            if (m_LODParentTransforms == null || index >= m_LODParentTransforms.Length || m_LODParentTransforms[index] == null)
                return new LODGroup[0];
            else
                return m_LODParentTransforms[index].GetComponentsInChildren<LODGroup>();
        }

        void OnEnable()
        {
            _Manager.Add(this);

            if (!_DidInitialize)
            {
                _DidInitialize = true;
                RenderPipelineManager.beginCameraRendering += OnBeforeCull;
                Camera.onPreCull += OnBeforeCull;

                #if UNITY_EDITOR
                Selection.selectionChanged += SelectionChange;

                #if UNITY_2020_2_OR_NEWER
                ObjectChangeEvents.changesPublished += ObjectChangeEvent;
                #endif
                #endif
            }
        }

        void OnDisable()
        {
            _Manager.Remove(this);
        }

#if UNITY_EDITOR && UNITY_2020_2_OR_NEWER
        static void DirtyStructure(GameObject go)
        {
            if (go == null)
                return;

            _CachedHLODList.Clear();
            go.GetComponentsInChildren(_CachedHLODList);
            foreach(var hlod in _CachedHLODList)
                _Manager.Update(hlod);

            go.GetComponentsInParent(false, _CachedHLODList);
            foreach(var hlod in _CachedHLODList)
                _Manager.Update(hlod);
        }

        static void ObjectChangeEvent(ref ObjectChangeEventStream stream)
        {
            for (int i = 0; i != stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    {
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var evt);

                        var lodGroup = Resources.InstanceIDToObject(evt.instanceId) as LODGroup;
                        if (lodGroup != null && lodGroup.GetComponent<HLOD>())
                            _Manager.UpdateLODData(lodGroup.GetComponent<HLOD>());

                        var hlod = Resources.InstanceIDToObject(evt.instanceId) as HLOD;
                        if (hlod != null)
                            DirtyStructure(hlod.gameObject);
                        break;
                    }
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                    {
                        stream.GetCreateGameObjectHierarchyEvent(i, out var evt);
                        DirtyStructure(Resources.InstanceIDToObject(evt.instanceId) as GameObject);
                        break;
                    }
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                    {
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var evt);
                        DirtyStructure(Resources.InstanceIDToObject(evt.instanceId) as GameObject);
                        break;
                    }
                    case ObjectChangeKind.ChangeGameObjectStructure:
                    {
                        stream.GetChangeGameObjectStructureEvent(i, out var evt);
                        DirtyStructure(Resources.InstanceIDToObject(evt.instanceId) as GameObject);
                        break;
                    }
                    case ObjectChangeKind.ChangeGameObjectParent:
                    {
                        stream.GetChangeGameObjectParentEvent(i, out var evt);
                        DirtyStructure(Resources.InstanceIDToObject(evt.newParentInstanceId) as GameObject);
                        DirtyStructure(Resources.InstanceIDToObject(evt.previousParentInstanceId) as GameObject);
                        break;
                    }
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                    {
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var evt);
                        DirtyStructure(Resources.InstanceIDToObject(evt.instanceId) as GameObject);
                        break;
                    }
                    case ObjectChangeKind.UpdatePrefabInstances:
                    {
                        stream.GetUpdatePrefabInstancesEvent(i, out var evt);
                        for(int c = 0;c != evt.instanceIds.Length;c++)
                            DirtyStructure(Resources.InstanceIDToObject(evt.instanceIds[c]) as GameObject);
                        break;
                    }
                }
            }
        }
#endif


        // When selection changes editor code might use ForceLOD api as well, resulting in conflicts.
        // Thus just force override in that case.
        static void SelectionChange()
        {
            _DidSelectionChange = true;
        }

        static void OnBeforeCull(ScriptableRenderContext _, Camera camera)
        {
            OnBeforeCull(camera);
        }
        static void OnBeforeCull(Camera camera)
        {
            Transform selection = null;
            #if UNITY_EDITOR
            selection = Selection.activeTransform;
            #endif

            _Manager.Update(LODGroupExtensions.CalculateLODParams(camera), _DidSelectionChange, selection);
            _DidSelectionChange = false;
        }
    }
}
