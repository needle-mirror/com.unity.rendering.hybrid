using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_Smoothness", MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct URPMaterialPropertySmoothness : IComponentData
    {
        public float Value;
    }
}
