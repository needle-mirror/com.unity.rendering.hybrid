using Unity.Entities;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    [UnityEngine.ExecuteAlways]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateBefore(typeof(HybridRendererSystem))]
    public class DeformationsInPresentation : ComponentSystemGroup
    {
        protected override void OnCreate()
        {
            if (UnityEngine.SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                UnityEngine.Debug.LogWarning("Warning: No Graphics Device found. Deformation systems will not run.");
                Enabled = false;
            }

            base.OnCreate();
        }
    }


    [UnityEngine.ExecuteAlways]
    [UpdateInGroup(typeof(DeformationsInPresentation))]
    public partial class PushMeshDataSystem : PushMeshDataSystemBase { }

    [UnityEngine.ExecuteAlways]
    [UpdateInGroup(typeof(DeformationsInPresentation))]
    [UpdateAfter(typeof(PushMeshDataSystem))]
    [UpdateBefore(typeof(FinalizePushSkinMatrixSystem))]
    public partial class PrepareSkinMatrixSystem : PrepareSkinMatrixSystemBase { }

    [UnityEngine.ExecuteAlways]
    [UpdateInGroup(typeof(DeformationsInPresentation))]
#if ENABLE_COMPUTE_DEFORMATIONS
    [UpdateBefore(typeof(SkinningDeformationSystem))]
#endif
    public partial class FinalizePushSkinMatrixSystem : FinalizePushSkinMatrixSystemBase
    {
        protected override PrepareSkinMatrixSystemBase PrepareSkinMatrixSystem =>
            World.GetExistingSystem<PrepareSkinMatrixSystem>();
    }

#if ENABLE_COMPUTE_DEFORMATIONS
    [UnityEngine.ExecuteAlways]
    [UpdateInGroup(typeof(DeformationsInPresentation))]
    [UpdateAfter(typeof(PushMeshDataSystem))]
    [UpdateBefore(typeof(FinalizePushBlendWeightSystem))]
    public partial class PrepareBlendWeightSystem : PrepareBlendWeightSystemBase { }

    [UnityEngine.ExecuteAlways]
    [UpdateInGroup(typeof(DeformationsInPresentation))]
    [UpdateBefore(typeof(BlendShapeDeformationSystem))]
    public partial class FinalizePushBlendWeightSystem : FinalizePushBlendWeightSystemBase
    {
        protected override PrepareBlendWeightSystemBase PrepareBlendShapeSystem =>
            World.GetExistingSystem<PrepareBlendWeightSystem>();
    }

    [UnityEngine.ExecuteAlways]
    [UpdateInGroup(typeof(DeformationsInPresentation))]
    [UpdateAfter(typeof(PushMeshDataSystem))]
    public partial class InstantiateDeformationSystem : InstantiateDeformationSystemBase { }

    [UnityEngine.ExecuteAlways]
    [UpdateInGroup(typeof(DeformationsInPresentation))]
    [UpdateAfter(typeof(InstantiateDeformationSystem))]
    public partial class BlendShapeDeformationSystem : BlendShapeDeformationSystemBase { }

    [UnityEngine.ExecuteAlways]
    [UpdateInGroup(typeof(DeformationsInPresentation))]
    [UpdateAfter(typeof(BlendShapeDeformationSystem))]
    public partial class SkinningDeformationSystem : SkinningDeformationSystemBase { }
#endif
}
