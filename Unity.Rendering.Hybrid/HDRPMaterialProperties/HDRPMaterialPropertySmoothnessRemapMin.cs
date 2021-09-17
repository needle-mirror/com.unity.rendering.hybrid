using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_SmoothnessRemapMin"   , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertySmoothnessRemapMin : IComponentData { public float  Value; }
}
