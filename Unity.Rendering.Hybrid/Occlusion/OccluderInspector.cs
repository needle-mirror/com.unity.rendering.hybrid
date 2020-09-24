#if UNITY_EDITOR && ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Unity.Rendering.Occlusion
{
    [CustomEditor(typeof(Occluder))]
    [CanEditMultipleObjects]
    internal class OccluderInspector : Editor
    {

        class Contents
        {
            public GUIContent occluderType = EditorGUIUtility.TrTextContent("Occluder type", "The source of geometry for this occluder.");
            public GUIContent[] occluderTypes = new GUIContent[]
            {
            EditorGUIUtility.TrTextContent("Mesh"),
            EditorGUIUtility.TrTextContent("Volume")
            };

            public GUIContent editOccluderMesh = EditorGUIUtility.TrTextContent("Edit Occluder Mesh");
            public GUIContent editOccluderVolume = EditorGUIUtility.TrTextContent("Edit Occluder Volume");
            public GUIContent meshContent = EditorGUIUtility.TrTextContent("Mesh", "The occluder mesh");
            public GUIContent positionContent = EditorGUIUtility.TrTextContent("Position", "The position of this occluder relative to transform.");
            public GUIContent rotationContent = EditorGUIUtility.TrTextContent("Rotation", "The rotation of this occluder relative to transform.");
            public GUIContent scaleContent = EditorGUIUtility.TrTextContent("Scale", "The scale of this occluder relative to transform.");
        }
        static Contents s_Contents;

        SerializedProperty m_Mesh;
        SerializedProperty m_Position;
        SerializedProperty m_Rotation;
        SerializedProperty m_Scale;

        public void OnEnable()
        {
            m_Mesh = serializedObject.FindProperty("Mesh");
            m_Position = serializedObject.FindProperty("relativePosition");
            m_Rotation = serializedObject.FindProperty("relativeRotation");
            m_Scale = serializedObject.FindProperty("relativeScale");
        }

        public override void OnInspectorGUI()
        {
            if (s_Contents == null)
                s_Contents = new Contents();

            if (!EditorGUIUtility.wideMode)
            {
                EditorGUIUtility.wideMode = true;
                EditorGUIUtility.labelWidth = EditorGUIUtility.currentViewWidth - 212;
            }

            serializedObject.Update();

            var occluder = target as Occluder;

            var oldType = occluder.Type;
            occluder.Type = (Occluder.OccluderType)EditorGUILayout.Popup(s_Contents.occluderType, (int)occluder.Type, s_Contents.occluderTypes);

            if (oldType != occluder.Type)
            {
                occluder.Mesh = null;
            }

            EditorGUI.indentLevel++;
            if (occluder.Type == Occluder.OccluderType.Mesh)
            {
                EditorGUILayout.EditorToolbarForTarget(s_Contents.editOccluderMesh, occluder);
                EditorGUILayout.PropertyField(m_Mesh, s_Contents.meshContent);
            }
            else if (occluder.Type == Occluder.OccluderType.Volume)
            {
                EditorGUILayout.BeginHorizontal();
                occluder.m_PrismSides = EditorGUILayout.IntField(EditorGUIUtility.TrTempContent("Prism sides"), occluder.m_PrismSides);
                if (occluder.m_PrismSides < 3)
                    occluder.m_PrismSides = 3;

                if (GUILayout.Button("Create"))
                {
                    var volume = new OccluderVolume();
                    volume.CreatePrism(occluder.m_PrismSides);
                    occluder.Mesh = volume.CalculateMesh();
                }
                EditorGUILayout.EndHorizontal();

                if (occluder.Mesh == null)
                {
                    EditorGUILayout.LabelField("Volume not yet created.");
                }
                else
                {
                    EditorGUILayout.EditorToolbarForTarget(s_Contents.editOccluderVolume, target);
                }
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.PropertyField(m_Position, s_Contents.positionContent);
            EditorGUILayout.PropertyField(m_Rotation, s_Contents.rotationContent);
            EditorGUILayout.PropertyField(m_Scale, s_Contents.scaleContent);

            serializedObject.ApplyModifiedProperties();
        }
    }
}

#endif
