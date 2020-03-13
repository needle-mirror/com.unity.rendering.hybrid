using System;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;

public interface IConvertVector
{
    void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem, Vector4 value);
}

public interface IConvertFloat
{
    void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem, float value);
}

[DisallowMultipleComponent]
[RequiresEntityConversion]
[ExecuteInEditMode]
[ConverterVersion("joe", 1)]
public class MaterialOverride : MonoBehaviour, IConvertGameObjectToEntity
{
    public MaterialOverrideAsset overrideAsset;
    public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
    {
        if (overrideAsset != null)
        {
            if (overrideAsset.shader != null)
            {
                foreach (var overrideData in overrideAsset.overrideList)
                {

                    string typeName = overrideData.name.Replace("_", ""); //TODO(atheisen): properly sanitize type names to follow c# class name rules
                    switch (overrideData.type)
                    {
                        case (ShaderPropertyType.Color):
                        {
                            Type overrideType = GetTypeFromString(typeName + "Vector4Override");
                            if (overrideType != null)
                            {
                                var component = (IConvertVector) Activator.CreateInstance(overrideType);
                                component.Convert(entity, dstManager, conversionSystem, overrideData.colorValue);
                            }
                            break;
                        }
                        case (ShaderPropertyType.Vector):
                        {
                            Type overrideType = GetTypeFromString(typeName + "Vector4Override");
                            if (overrideType != null)
                            {
                                var component = (IConvertVector) Activator.CreateInstance(overrideType);
                                component.Convert(entity, dstManager, conversionSystem, overrideData.vector4Value);
                            }
                            break;
                        }
                        case (ShaderPropertyType.Float):
                        {
                            Type overrideType = GetTypeFromString(typeName + "FloatOverride");
                            if (overrideType != null)
                            {
                                var component = (IConvertFloat) Activator.CreateInstance(overrideType);
                                component.Convert(entity, dstManager, conversionSystem, overrideData.floatValue);
                            }
                            break;
                        }
                    }
                }
            }
        }

    }

    private Type GetTypeFromString(string typeName)
    {
        foreach (var t in TypeManager.GetAllTypes())
        {
            if (t.Type != null)
            {
                if (t.Type.ToString() == typeName)
                {
                    return t.Type;
                }
            }
        }
        
        return null;
    }

    public void ApplyMaterialProperties()
    {
        if (overrideAsset != null)
        {
            if (overrideAsset.shader != null)
            {
                //TODO(atheisen): needs support for multiple renderers
                var renderer = GetComponent<Renderer>();
                if (renderer != null)
                {
                    var propertyBlock = new MaterialPropertyBlock();
                    foreach (var overrideData in overrideAsset.overrideList)
                    {
                        switch (overrideData.type)
                        {
                            case (ShaderPropertyType.Color):
                            {
                                propertyBlock.SetColor(overrideData.name, overrideData.colorValue);
                                break;
                            }
                            case (ShaderPropertyType.Vector):
                            {
                                propertyBlock.SetVector(overrideData.name, overrideData.vector4Value);
                                break;
                            }
                            case (ShaderPropertyType.Float):
                            {
                                propertyBlock.SetFloat(overrideData.name, overrideData.floatValue);
                                break;
                            }
                        }
                    }

                    renderer.SetPropertyBlock(propertyBlock);
                }
            }
        }
    }

    public void OnValidate()
    {
        ApplyMaterialProperties();
    }
}
