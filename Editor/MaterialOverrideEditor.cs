using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[CustomEditor(typeof(MaterialOverride))]
public class MaterialOverrideEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawDefaultInspector();
        
        MaterialOverride overrideComponent = (target as MaterialOverride);
        if (overrideComponent != null)
        {
            MaterialOverrideAsset overrideAsset = overrideComponent.overrideAsset;
            if (overrideAsset != null)
            {
                SerializedObject assetObj = new SerializedObject(overrideAsset);

                assetObj.Update();

                //TODO(atheisen): this just provides a way to edit the asset from the gameobject for convenience. we might actually want assets to be overridable as well
                SerializedProperty overrideListProp = assetObj.FindProperty("overrideList");
                for (int i = 0; i < overrideListProp.arraySize; i++)
                {
                    SerializedProperty overrideProp = overrideListProp.GetArrayElementAtIndex(i);
                    string strName = overrideProp.FindPropertyRelative("name").stringValue;
                    ShaderPropertyType type = (ShaderPropertyType) overrideProp.FindPropertyRelative("type").intValue;

                    switch (type)
                    {
                        case (ShaderPropertyType.Color):
                        {
                            SerializedProperty colorProp = overrideProp.FindPropertyRelative("colorValue");
                            EditorGUILayout.PropertyField(colorProp, new GUIContent(strName));
                            break;
                        }
                        case (ShaderPropertyType.Vector):
                        {
                            SerializedProperty vector4Prop = overrideProp.FindPropertyRelative("vector4Value");
                            EditorGUILayout.PropertyField(vector4Prop, new GUIContent(strName));
                            break;
                        }
                        case (ShaderPropertyType.Float):
                        {
                            SerializedProperty floatProp = overrideProp.FindPropertyRelative("floatValue");
                            EditorGUILayout.PropertyField(floatProp, new GUIContent(strName));
                            break;
                        }
                        default:
                        {
                            Debug.Log("Property " + strName + " is of unsupported type " + type + " for material override.");
                            break;
                        }
                    }
                }

                assetObj.ApplyModifiedProperties();
            }

        }
        
        serializedObject.ApplyModifiedProperties();
    }

}
