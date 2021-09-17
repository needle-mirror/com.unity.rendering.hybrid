using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_AORemapMin"           , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyAORemapMin : IComponentData { public float  Value; }
}
