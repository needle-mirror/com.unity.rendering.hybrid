using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Entities;

internal class MaterialPropPopup : PopupWindowContent
{
    private Vector2 _scrollViewVector;
    private MaterialOverrideAsset _overrideAsset;
    private SerializedObject _serializedObject;
    

    private readonly ShaderPropertyType[] _supportedTypes =
    {
        ShaderPropertyType.Color,
        ShaderPropertyType.Vector,
        ShaderPropertyType.Float,
    };

    public MaterialPropPopup(MaterialOverrideAsset overrideAsset, SerializedObject serializedObject)
    {
        _overrideAsset = overrideAsset;
        _serializedObject = serializedObject;
    }
    
    public override void OnGUI(Rect rect)
    {
        _scrollViewVector = GUILayout.BeginScrollView(_scrollViewVector);
        if (_overrideAsset.shader != null)
        {
            //TODO(atheisen): use TypeManager to find property compenentdatas that already exist for this shader for non-user specified shader properties
            //                (eg. _BaseColor, _Metallic)
            for (int i = 0; i < _overrideAsset.shader.GetPropertyCount(); i++)
            {
                ShaderPropertyType propertyType = _overrideAsset.shader.GetPropertyType(i);
                if (_supportedTypes.Any(item => item == propertyType))
                {
                    string propertyName = _overrideAsset.shader.GetPropertyName(i);

                    //TODO(atheisen): review if this UI code is too coupled with behavior?
                    int index = _overrideAsset.FindOverride(propertyName);
                    bool overriden = index != -1;
                    bool toggle = GUILayout.Toggle(overriden, propertyName);
                    if (overriden != toggle)
                    {
                        _serializedObject.Update();
                        SerializedProperty overrideListProp = _serializedObject.FindProperty("overrideList");
                        int arraySize = overrideListProp.arraySize;
                        
                        string shaderName = AssetDatabase.GetAssetPath(_overrideAsset.shader);
                        if (toggle)
                        {
                            overrideListProp.InsertArrayElementAtIndex(arraySize);
                            SerializedProperty overrideProp = overrideListProp.GetArrayElementAtIndex(arraySize);
                            overrideProp.FindPropertyRelative("name").stringValue = propertyName;
                            
                            overrideProp.FindPropertyRelative("materialName").stringValue = shaderName;
                            overrideProp.FindPropertyRelative("type").intValue = (int) propertyType;
                            //TODO(atheisen): add vector 2,3 support
                            switch (propertyType)
                            {
                                case (ShaderPropertyType.Color):
                                {
                                    overrideProp.FindPropertyRelative("colorValue").colorValue = _overrideAsset.shader.GetPropertyDefaultVectorValue(i);
                                    break;
                                }
                                case (ShaderPropertyType.Vector):
                                {
                                    overrideProp.FindPropertyRelative("vector4Value").vector4Value = _overrideAsset.shader.GetPropertyDefaultVectorValue(i);
                                    break;
                                }
                                case (ShaderPropertyType.Float):
                                {
                                    overrideProp.FindPropertyRelative("floatValue").floatValue = _overrideAsset.shader.GetPropertyDefaultFloatValue(i);
                                    break;
                                }
                            }
                        }
                        else
                        {
                            overrideListProp.DeleteArrayElementAtIndex(index);
                        }
                        _serializedObject.ApplyModifiedProperties();
                        
                        _overrideAsset.GenerateScriptString();
                    }
                }
            }
        }

        GUILayout.EndScrollView();
    }
}

[CustomEditor(typeof(MaterialOverrideAsset))]
public class MaterialOverrideAssetEditor : Editor
{
    private Rect _buttonRect;
    private EditorWindow _popupWindow;

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        MaterialOverrideAsset overrideAsset = (target as MaterialOverrideAsset);
        if (GUILayout.Button("Add Material Override"))
        {

            if (overrideAsset != null)
            {
                PopupWindow.Show(_buttonRect, new MaterialPropPopup(overrideAsset, serializedObject));
                if (Event.current.type == EventType.Repaint)
                {
                    _buttonRect = GUILayoutUtility.GetLastRect();
                }
            }
        }
        
        EditorGUILayout.PropertyField(serializedObject.FindProperty("shader"), new GUIContent("shader"));
        
        SerializedProperty overrideListProp = serializedObject.FindProperty("overrideList");
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
                //TODO(atheisen): add support for vector1,2,3 and find out about Texture and Range overrides
                //case (ShaderPropertyType.Range):
                //{
                //    Debug.Log("Property " + strName + " is of unsupported type " + type + " for material override.");
                //    break;
                //}
                //case (ShaderPropertyType.Texture):
                //{
                //    Debug.Log("Property " + strName + " is of unsupported type " + type + " for material override.");
                //    break;
                //}
                default:
                {
                    Debug.Log("Property " + strName + " is of unsupported type " + type + " for material override.");
                    break;
                }
            }
        }
        
        serializedObject.ApplyModifiedProperties();
    }

}
