using System;
using System.IO;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;

[RequiresEntityConversion]
[CreateAssetMenu(fileName = "MaterialOverrideAsset", menuName = "ScriptableObjects/MaterialOverrideAsset", order = 1)]
public class MaterialOverrideAsset : ScriptableObject
{
    [Serializable]
    public struct OverrideData
    {
        public string name;
        public string materialName;
        public ShaderPropertyType type;
        public Color colorValue;
        public Vector4 vector4Value;
        public float floatValue;
    }

    public List<OverrideData> overrideList = new List<OverrideData>();
    
    public Shader shader;
    
    public void GenerateScriptString()
    {
        string preamble =
@"using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;
";
        for (int i = 0; i < overrideList.Count; i++)
        {
            OverrideData overrideData = overrideList[i];

            bool componentExists = false;
            foreach (var t in TypeManager.GetAllTypes())
            {
                if (t.Type != null)
                {
                    if (t.Type.ToString() == overrideData.name.Replace("_", "") + "Vector4Override")
                    {
                        componentExists = true;
                        break;
                    }
                }
            }

            if (componentExists)
            {
                continue;
            }

            string generatedStruct = "";
                        
            string @fieldName = overrideData.name.Replace("_", "");
            string @typeName = "";
            
            if (overrideData.type == ShaderPropertyType.Color 
                || overrideData.type == ShaderPropertyType.Vector)
            {
                @typeName = "Vector4";
                generatedStruct =
$@"
[Serializable]
[MaterialProperty(""{@overrideData.name}"", MaterialPropertyFormat.Float4)]
struct {@fieldName}{@typeName}Override : IComponentData, IConvertVector
{{
    public float4 Value;

    public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem, Vector4 value)
    {{
        Value = new float4(value.x, value.y, value.z, value.w);
        dstManager.AddComponentData(entity, this);
    }}
}}
";
            } else if (overrideData.type == ShaderPropertyType.Float)
            {
                @typeName = "Float";
                generatedStruct =
$@"
[Serializable]
[MaterialProperty(""{@overrideData.name}"", MaterialPropertyFormat.Float)]
struct {@fieldName}{@typeName}Override : IComponentData, IConvertFloat
{{
    public float Value;

    public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem, float value)
    {{
        Value = value;
        dstManager.AddComponentData(entity, this);
    }}
}}
";
            }

            if (generatedStruct != "")
            {
                //TODO(atheisen): way to guarantee compile happens promptly on file creation to not confuse user?
                //TODO(atheisen): writeall text 260 char limit for filepath
                string scriptPath = Path.Combine(Path.GetDirectoryName(overrideData.materialName),
                    $@"{@fieldName}{@typeName}OverrideGenerated.cs");
                File.WriteAllText(scriptPath, preamble + generatedStruct);
            }
        }

    }

    public int FindOverride(string name)
    {
        for (int i = 0; i < overrideList.Count; i++)
        {
            if (overrideList[i].name == name)
            {
                return i;
            }
        }

        return -1;
    }

    public void OnValidate()
    {
        foreach (var overrideComponent in FindObjectsOfType<MaterialOverride>())
        {
           if (overrideComponent.overrideAsset == this) {
               overrideComponent.ApplyMaterialProperties();
           }
        }
    }
}
