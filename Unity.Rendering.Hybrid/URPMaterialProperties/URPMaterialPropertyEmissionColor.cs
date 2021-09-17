using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("_EmissionColor", MaterialPropertyFormat.Float4)]
    [GenerateAuthoringComponent]
    public struct URPMaterialPropertyEmissionColor : IComponentData
    {
        public float4 Value;
    }
}
