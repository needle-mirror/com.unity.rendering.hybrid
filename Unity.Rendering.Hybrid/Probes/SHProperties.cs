using System;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    struct SHProperties : IEquatable<SHProperties>
    {
        public float4 SHAr;
        public float4 SHAg;
        public float4 SHAb;
        public float4 SHBr;
        public float4 SHBg;
        public float4 SHBb;
        public float4 SHC;

        // Keep these in sync with the above layout
        public const int kOffsetOfSHAr = 0 * 4 * sizeof(float);
        public const int kOffsetOfSHAg = 1 * 4 * sizeof(float);
        public const int kOffsetOfSHAb = 2 * 4 * sizeof(float);
        public const int kOffsetOfSHBr = 3 * 4 * sizeof(float);
        public const int kOffsetOfSHBg = 4 * 4 * sizeof(float);
        public const int kOffsetOfSHBb = 5 * 4 * sizeof(float);
        public const int kOffsetOfSHC  = 6 * 4 * sizeof(float);

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

        public bool Equals(SHProperties other)
        {
            return SHAr.Equals(other.SHAr) && SHAg.Equals(other.SHAg) && SHAb.Equals(other.SHAb) && SHBr.Equals(other.SHBr) && SHBg.Equals(other.SHBg) && SHBb.Equals(other.SHBb) && SHC.Equals(other.SHC);
        }

        public override bool Equals(object obj)
        {
            return obj is SHProperties other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = SHAr.GetHashCode();
                hashCode = (hashCode * 397) ^ SHAg.GetHashCode();
                hashCode = (hashCode * 397) ^ SHAb.GetHashCode();
                hashCode = (hashCode * 397) ^ SHBr.GetHashCode();
                hashCode = (hashCode * 397) ^ SHBg.GetHashCode();
                hashCode = (hashCode * 397) ^ SHBb.GetHashCode();
                hashCode = (hashCode * 397) ^ SHC.GetHashCode();
                return hashCode;
            }
        }

        public static bool operator ==(SHProperties left, SHProperties right)
        {
            return left.SHAr.Equals(right.SHAr) &&
                   left.SHAg.Equals(right.SHAg) &&
                   left.SHAb.Equals(right.SHAb) &&
                   left.SHBr.Equals(right.SHBr) &&
                   left.SHBg.Equals(right.SHBg) &&
                   left.SHBb.Equals(right.SHBb) &&
                   left.SHC. Equals(right.SHC);
        }

        public static bool operator !=(SHProperties left, SHProperties right)
        {
            return !left.Equals(right);
        }
    }
}
