using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_OcclusionStrength", MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct URPMaterialPropertyOcclusionStrength : IComponentData
    {
        public float Value;
    }
}
