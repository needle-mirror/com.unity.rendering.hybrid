using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("unity_LightData", MaterialPropertyFormat.Float4)]
    public struct BuiltinMaterialPropertyUnity_LightData : IComponentData
    {
        public float4 Value;
    }
}
