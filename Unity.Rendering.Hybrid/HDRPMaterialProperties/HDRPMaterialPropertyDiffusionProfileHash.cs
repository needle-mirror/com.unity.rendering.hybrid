using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_DiffusionProfileHash" , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyDiffusionProfileHash : IComponentData { public float  Value; }
}
