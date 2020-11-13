#if ENABLE_HYBRID_RENDERER_V2
using Unity.Mathematics;
using static Unity.Mathematics.math;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    struct SHProperties
    {
        public float4 SHAr;
        public float4 SHAg;
        public float4 SHAb;
        public float4 SHBr;
        public float4 SHBg;
        public float4 SHBb;
        public float4 SHC;

        public SHProperties(SphericalHarmonicsL2 sh)
        {
            SHAr = GetSHA(sh, 0);
            SHAg = GetSHA(sh, 1);
            SHAb = GetSHA(sh, 2);

            SHBr = GetSHB(sh, 0);
            SHBg = GetSHB(sh, 1);
            SHBb = GetSHB(sh, 2);

            SHC = GetSHC(sh);
        }

        static float4 GetSHA(SphericalHarmonicsL2 sh, int i)
        {
            return float4(sh[i, 3], sh[i, 1], sh[i, 2], sh[i, 0] - sh[i, 6]);
        }

        static float4 GetSHB(SphericalHarmonicsL2 sh, int i)
        {
            return float4(sh[i, 4], sh[i, 5], sh[i, 6] * 3f, sh[i, 7]);
        }

        static float4 GetSHC(SphericalHarmonicsL2 sh)
        {
            return float4(sh[0, 8], sh[1, 8], sh[2, 8], 1);
        }
    }
}
#endif
