using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_DetailAlbedoScale"    , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyDetailAlbedoScale : IComponentData { public float  Value; }
}
