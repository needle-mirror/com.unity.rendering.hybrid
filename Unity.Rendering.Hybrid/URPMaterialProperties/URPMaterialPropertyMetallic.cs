using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_Metallic", MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct URPMaterialPropertyMetallic : IComponentData
    {
        public float Value;
    }
}
