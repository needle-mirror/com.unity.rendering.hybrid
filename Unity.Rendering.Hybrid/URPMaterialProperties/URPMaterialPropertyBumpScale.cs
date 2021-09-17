using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_BumpScale", MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct URPMaterialPropertyBumpScale : IComponentData
    {
        public float Value;
    }
}
