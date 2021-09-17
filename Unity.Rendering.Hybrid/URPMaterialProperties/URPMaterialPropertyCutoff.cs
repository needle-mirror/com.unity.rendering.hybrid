using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_Cutoff", MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct URPMaterialPropertyCutoff : IComponentData
    {
        public float Value;
    }
}
