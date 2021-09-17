using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("_EmissiveColor"        , MaterialPropertyFormat.Float3)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyEmissiveColor : IComponentData { public float3 Value; }
}
