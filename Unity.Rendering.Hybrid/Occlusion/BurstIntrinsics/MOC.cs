#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

// DS: temp dev config
#define MOC_SUPPORT_BURST_INTRINSICS

// if not defined, fall back to alternative naive implementation
//#define MOC_USE_LANE_ACCESS_UNSAFE

// if not defined, fall back to SSE2 instructions
#define MOC_USE_SSE41


// original MOC compile time config:

/*!
 * Configure the algorithm used for updating and merging hierarchical z buffer entries. If QUICK_MASK
 * is defined to 1, use the algorithm from the paper "Masked Software Occlusion Culling", which has good
 * balance between performance and low leakage. If QUICK_MASK is defined to 0, use the algorithm from
 * "Masked Depth Culling for Graphics Hardware" which has less leakage, but also lower performance.
 */
//#define MOC_QUICK_MASK

/*!
* Configures the library for use with Direct3D (default) or OpenGL rendering. This changes whether the
* screen space Y axis points downwards (D3D) or upwards (OGL), and is primarily important in combination
* with the PRECISE_COVERAGE define, where this is important to ensure correct rounding and tie-breaker
* behaviour. It also affects the ScissorRect screen space coordinates.
*/
#define MOC_USE_D3D

/*!
 * Define PRECISE_COVERAGE to 1 to more closely match GPU rasterization rules. The increased precision comes
 * at a cost of slightly lower performance.
 */
#define MOC_PRECISE_COVERAGE

/*!
 * Define CLIPPING_PRESERVES_ORDER to 1 to prevent clipping from reordering triangle rasterization
 * order; This comes at a cost (approx 3-4%) but removes one source of temporal frame-to-frame instability.
 */
#define MOC_CLIPPING_PRESERVES_ORDER

/*!
* Define ENABLE_STATS to 1 to gather various statistics during occlusion culling. Can be used for profiling
* and debugging. Note that enabling this function will reduce performance significantly.
*/
//#define MOC_ENABLE_STATS


using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
#if MOC_ENABLE_STATS
using System.Threading;
#endif
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;


//using __mw = Unity.Burst.Intrinsics.v128;
//using __mw = Unity.Burst.Intrinsics.m256;
//using __mw = Unity.Burst.Intrinsics.m512;

namespace Unity.Rendering.Occlusion.MOC
{
    public enum WantedImplementation
    {
        AUTO = 0,
        SSE2 = 1,
        SSE41 = 2,
        AVX2 = 3,
        AVX512 = 4
    };

    public enum CullingResult
    {
        VISIBLE = 0x0,
        OCCLUDED = 0x1,
        VIEW_CULLED = 0x3
    };

    public enum BackfaceWinding
    {
        BACKFACE_NONE = 0,
        BACKFACE_CW = 1,
        BACKFACE_CCW = 2,
    };

    public enum ClipPlanes
    {
        CLIP_PLANE_NONE = 0x00,
        CLIP_PLANE_NEAR = 0x01,
        CLIP_PLANE_LEFT = 0x02,
        CLIP_PLANE_RIGHT = 0x04,
        CLIP_PLANE_BOTTOM = 0x08,
        CLIP_PLANE_TOP = 0x10,
        CLIP_PLANE_SIDES = (CLIP_PLANE_LEFT | CLIP_PLANE_RIGHT | CLIP_PLANE_BOTTOM | CLIP_PLANE_TOP),
        CLIP_PLANE_ALL = (CLIP_PLANE_LEFT | CLIP_PLANE_RIGHT | CLIP_PLANE_BOTTOM | CLIP_PLANE_TOP | CLIP_PLANE_NEAR)
    };

    /*!
     * Used to specify custom vertex layout. Memory offsets to y and z coordinates are set through
     * mOffsetY and mOffsetW, and vertex stride is given by mStride. It's possible to configure both
     * AoS and SoA layouts. Note that large strides may cause more cache misses and decrease
     * performance. It is advisable to store position data as compactly in memory as possible.
     */
    public struct VertexLayout
    {
        public int mStride;      //!< byte stride between vertices
        public int mOffsetY;     //!< byte offset from X to Y coordinate

        // DS: was union, doesn't seem necessary
        public int mOffsetZ; //!< byte offset from X to Z coordinate
        //public int mOffsetW; //!< byte offset from X to W coordinate
    };

    public unsafe struct ZTile
    {
        // DS: was v128 mZMin[2]
        public v128 mZMin0;
        public v128 mZMin1;

        public v128 mMask;
    };

    /*!
     * Used to control scissoring during rasterization. Note that we only provide coarse scissor support.
     * The scissor box x coordinates must be a multiple of 32, and the y coordinates a multiple of 8.
     * Scissoring is mainly meant as a means of enabling binning (sort middle) rasterizers in case
     * application developers want to use that approach for multithreading.
     */
    public struct ScissorRect
    {
        public int mMinX; //!< Screen space X coordinate for left side of scissor rect, inclusive and must be a multiple of 32
        public int mMinY; //!< Screen space Y coordinate for bottom side of scissor rect, inclusive and must be a multiple of 8
        public int mMaxX; //!< Screen space X coordinate for right side of scissor rect, <B>non</B> inclusive and must be a multiple of 32
        public int mMaxY; //!< Screen space Y coordinate for top side of scissor rect, <B>non</B> inclusive and must be a multiple of 8
    };

    /*!
     * Used to specify storage area for a binlist, containing triangles. This struct is used for binning
     * and multithreading. The host application is responsible for allocating memory for the binlists.
     */
    public unsafe struct TriList
    {
        public uint mMaxNumTriangles; //!< Maximum number of triangles that may be stored in mPtr
        public uint mNextTriIdx;       //!< Index of next triangle to be written, clear before calling BinTriangles to start from the beginning of the list
        public float* mDataBufferPtr;        //!< Scratchpad buffer allocated by the host application
    };

    /*!
    * Statistics that can be gathered during occluder rendering and visibility to aid debugging
    * and profiling. Must be enabled by changing the ENABLE_STATS define.
    */
    public struct OcclusionCullingStatistics
    {
        public struct Occluders
        {
            public System.Int64 mNumProcessedTriangles;  //!< Number of occluder triangles processed in total
            public System.Int64 mNumRasterizedTriangles; //!< Number of occluder triangles passing view frustum and backface culling
            public System.Int64 mNumTilesTraversed;      //!< Number of tiles traversed by the rasterizer
            public System.Int64 mNumTilesUpdated;        //!< Number of tiles where the hierarchical z buffer was updated
            public System.Int64 mNumTilesMerged;         //!< Number of tiles where the hierarchical z buffer was updated
        };

        public struct Occludees
        {

            public System.Int64 mNumProcessedRectangles; //!< Number of rects processed (TestRect())
            public System.Int64 mNumProcessedTriangles;  //!< Number of ocludee triangles processed (TestTriangles())
            public System.Int64 mNumRasterizedTriangles; //!< Number of ocludee triangle passing view frustum and backface culling
            public System.Int64 mNumTilesTraversed;      //!< Number of tiles traversed by triangle & rect rasterizers
        }

        public Occluders mOccluders;
        public Occludees mOccludees;
    };


#if MOC_SUPPORT_BURST_INTRINSICS

    public static unsafe class IntrinsicUtils
    {

#if MOC_USE_LANE_ACCESS_UNSAFE

        // read access
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int getIntLane(v128 v, uint laneIdx)
        {
            //Debug.Assert(laneIdx >= 0 && laneIdx < 4);
            int* ptr = &v.SInt0;
            return ptr[laneIdx];
        }

        // read access
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe float getFloatLane(v128 vector, uint laneIdx)
        {
            //Debug.Assert(laneIdx >= 0 && laneIdx < 4);
            float* ptr = &vector.Float0;
            return ptr[laneIdx];
        }

        // used for "write" access (returns copy, requires assignment afterwards)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if true
        // DS: TODO: not great, check code gen
        public static v128 getCopyWithFloatLane(v128 vector, uint laneIdx, float laneVal)
        {
            //Debug.Assert(laneIdx >= 0 && laneIdx < 4);

            // eat the modulo cost to not let it overflow
            switch (laneIdx % 4)
            {
                default:    // DS: incorrect fallthrough, but works with modulo and silences compiler (CS0161)
                case 0: return X86.Sse4_1.insert_ps(vector, X86.Sse.set_ps(laneVal, laneVal, laneVal, laneVal), X86.Sse.SHUFFLE(0, 0, 0, 0));
                case 1: return X86.Sse4_1.insert_ps(vector, X86.Sse.set_ps(laneVal, laneVal, laneVal, laneVal), X86.Sse.SHUFFLE(1, 1, 0, 0));
                case 2: return X86.Sse4_1.insert_ps(vector, X86.Sse.set_ps(laneVal, laneVal, laneVal, laneVal), X86.Sse.SHUFFLE(2, 2, 0, 0));
                case 3: return X86.Sse4_1.insert_ps(vector, X86.Sse.set_ps(laneVal, laneVal, laneVal, laneVal), X86.Sse.SHUFFLE(3, 3, 0, 0));
            }
        }
#endif

#if false
        // DS: TODO: check if works (first version should work with custom unity version)
        public static unsafe v128 getCopyWithFloatLane(v128 vector, uint laneIdx, float laneVal)
        {
            //Debug.Assert(laneIdx >= 0 && laneIdx < 4);
            float* ptr = &vector.Float0;    // Burst error BC1303: Cannot take a reference or pointer to intrinsic vector field `Float0`
            ptr[laneIdx] = laneVal;
            return vector;
        }
#endif

#if false
        // DS: TODO: Issam's unsafe version, test
        public static unsafe v128 getCopyWithFloatLane(v128 vector, uint laneIdx, float laneVal)
        {
            ((float*)&vector)[laneIdx] = laneVal;
            return vector;
        }
#endif


#else


        // naive approach, works with C# reference implementation

        // read access
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int getIntLane(v128 vector, uint laneIdx)
        {
            //Debug.Assert(laneIdx >= 0 && laneIdx < 4);

            // eat the modulo cost to not let it overflow
            switch (laneIdx % 4)
            {
                default:    // DS: incorrect, but works with modulo and silences compiler (CS0161)
                case 0: { return vector.SInt0; }
                case 1: { return vector.SInt1; }
                case 2: { return vector.SInt2; }
                case 3: { return vector.SInt3; }
            }
        }

        // used for "write" access (returns copy, requires assignment afterwards)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 getCopyWithIntLane(v128 vector, uint laneIdx, int laneVal)
        {
            //Debug.Assert(laneIdx >= 0 && laneIdx < 4);

            // eat the modulo cost to not let it overflow
            switch (laneIdx % 4)
            {
                default:    // DS: incorrect fallthrough, but works with modulo and silences compiler (CS0161)
                case 0: { vector.SInt0 = laneVal; break; }
                case 1: { vector.SInt1 = laneVal; break; }
                case 2: { vector.SInt2 = laneVal; break; }
                case 3: { vector.SInt3 = laneVal; break; }
            }

            return vector;
        }

        // read access
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float getFloatLane(v128 vector, uint laneIdx)
        {
            //Debug.Assert(laneIdx >= 0 && laneIdx < 4);

            // eat the modulo cost to not let it overflow
            switch (laneIdx % 4)
            {
                default:    // DS: incorrect fallthrough, but works with modulo and silences compiler (CS0161)
                case 0: { return vector.Float0; }
                case 1: { return vector.Float1; }
                case 2: { return vector.Float2; }
                case 3: { return vector.Float3; }
            }
        }

        // used for "write" access (returns copy, requires assignment afterwards)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 getCopyWithFloatLane(v128 vector, uint laneIdx, float laneVal)
        {
            //Debug.Assert(laneIdx >= 0 && laneIdx < 4);

            // eat the modulo cost to not let it overflow
            switch (laneIdx % 4)
            {
                default:    // DS: incorrect fallthrough, but works with modulo and silences compiler (CS0161)
                case 0: { vector.Float0 = laneVal; break; }
                case 1: { vector.Float1 = laneVal; break; }
                case 2: { vector.Float2 = laneVal; break; }
                case 3: { vector.Float3 = laneVal; break; }
            }

            return vector;
        }

#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_fmadd_ps(v128 a, v128 b, v128 c) { return X86.Sse.add_ps(X86.Sse.mul_ps(a, b), c); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_fmsub_ps(v128 a, v128 b, v128 c) { return X86.Sse.sub_ps(X86.Sse.mul_ps(a, b), c); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_neg_ps(v128 a) { return X86.Sse.xor_ps((a), X86.Sse.set1_ps(-0f)); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_neg_epi32(v128 a) { return X86.Sse2.sub_epi32(X86.Sse2.set1_epi32(0), (a)); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_not_epi32(v128 a) { return X86.Sse2.xor_si128((a), X86.Sse2.set1_epi32(~0)); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_abs_ps(v128 a) { return X86.Sse.and_ps((a), X86.Sse2.set1_epi32(0x7FFFFFFF)); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_or_epi32(v128 a, v128 b) { return X86.Sse2.or_si128(a, b); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_andnot_epi32(v128 a, v128 b) { return X86.Sse2.andnot_si128(a, b); }

#if MOC_USE_SSE41

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_mullo_epi32(v128 a, v128 b) { return X86.Sse4_1.mullo_epi32(a, b); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_min_epi32(v128 a, v128 b) { return X86.Sse4_1.min_epi32(a, b); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_max_epi32(v128 a, v128 b) { return X86.Sse4_1.max_epi32(a, b); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_abs_epi32(v128 a) { return X86.Ssse3.abs_epi32(a); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_blendv_ps(v128 a, v128 b, v128 c) { return X86.Sse4_1.blendv_ps(a, b, c); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int _mmw_testz_epi32(v128 a, v128 b) { return X86.Sse4_1.testz_si128(a, b); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmx_dp4_ps(v128 a, v128 b) { return X86.Sse4_1.dp_ps(a, b, 0xFF); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_floor_ps(v128 a) { return X86.Sse4_1.round_ps(a, (int)X86.RoundingMode.FROUND_FLOOR_NOEXC); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_ceil_ps(v128 a) { return X86.Sse4_1.round_ps(a, (int)X86.RoundingMode.FROUND_CEIL_NOEXC); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_transpose_epi8(v128 a)
        {
            v128 shuff = X86.Sse2.setr_epi8(0x0, 0x4, 0x8, 0xC, 0x1, 0x5, 0x9, 0xD, 0x2, 0x6, 0xA, 0xE, 0x3, 0x7, 0xB, 0xF);
            return X86.Ssse3.shuffle_epi8(a, shuff);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_sllv_ones(v128 ishift)
        {
            v128 shift = X86.Sse4_1.min_epi32(ishift, X86.Sse2.set1_epi32(32));

            // Uses lookup tables and _mm_shuffle_epi8 to perform _mm_sllv_epi32(~0, shift)
            v128 byteShiftLUT;
            unchecked
            {
                byteShiftLUT = X86.Sse2.setr_epi8((sbyte)0xFF, (sbyte)0xFE, (sbyte)0xFC, (sbyte)0xF8, (sbyte)0xF0, (sbyte)0xE0, (sbyte)0xC0, (sbyte)0x80, 0, 0, 0, 0, 0, 0, 0, 0);
            }
            v128 byteShiftOffset = X86.Sse2.setr_epi8(0, 8, 16, 24, 0, 8, 16, 24, 0, 8, 16, 24, 0, 8, 16, 24);
            v128 byteShiftShuffle = X86.Sse2.setr_epi8(0x0, 0x0, 0x0, 0x0, 0x4, 0x4, 0x4, 0x4, 0x8, 0x8, 0x8, 0x8, 0xC, 0xC, 0xC, 0xC);

            v128 byteShift = X86.Ssse3.shuffle_epi8(shift, byteShiftShuffle);

            // DS: TODO: change once we get Burst fix for X86.Sse2.set1_epi8()
            const sbyte val = 8;
            byteShift = X86.Sse4_1.min_epi8(X86.Sse2.subs_epu8(byteShift, byteShiftOffset), new v128(val) /*X86.Sse2.set1_epi8(8)*/);

            v128 retMask = X86.Ssse3.shuffle_epi8(byteShiftLUT, byteShift);

            return retMask;
        }

#else

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_mullo_epi32(v128 a, v128 b)
        {
            // Do products for even / odd lanes & merge the result
            v128 even = X86.Sse2.and_si128(X86.Sse2.mul_epu32(a, b), X86.Sse2.setr_epi32(~0, 0, ~0, 0));
            v128 odd = X86.Sse2.slli_epi64(X86.Sse2.mul_epu32(X86.Sse2.srli_epi64(a, 32), X86.Sse2.srli_epi64(b, 32)), 32);
            return X86.Sse2.or_si128(even, odd);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_min_epi32(v128 a, v128 b)
        {
            v128 cond = X86.Sse2.cmpgt_epi32(a, b);
            return X86.Sse2.or_si128(X86.Sse2.andnot_si128(cond, a), X86.Sse2.and_si128(cond, b));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_max_epi32(v128 a, v128 b)
        {
            v128 cond = X86.Sse2.cmpgt_epi32(b, a);
            return X86.Sse2.or_si128(X86.Sse2.andnot_si128(cond, a), X86.Sse2.and_si128(cond, b));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_abs_epi32(v128 a)
        {
            v128 mask = X86.Sse2.cmplt_epi32(a, X86.Sse2.setzero_si128());
            return X86.Sse2.add_epi32(X86.Sse2.xor_si128(a, mask), X86.Sse2.srli_epi32(mask, 31));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_blendv_ps(v128 a, v128 b, v128 c)
        {
            v128 cond = X86.Sse2.srai_epi32(c, 31);
            return X86.Sse.or_ps(X86.Sse.andnot_ps(cond, a), X86.Sse.and_ps(cond, b));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int _mmw_testz_epi32(v128 a, v128 b)
        {
            return X86.Sse2.movemask_epi8(X86.Sse2.cmpeq_epi8(X86.Sse2.and_si128(a, b), X86.Sse2.setzero_si128())) == 0xFFFF ? 1 : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmx_dp4_ps(v128 a, v128 b)
        {
            // Product and two shuffle/adds pairs (similar to hadd_ps)
            v128 prod = X86.Sse.mul_ps(a, b);
            v128 dp = X86.Sse.add_ps(prod, X86.Sse.shuffle_ps(prod, prod, X86.Sse.SHUFFLE(2, 3, 0, 1)));
            dp = X86.Sse.add_ps(dp, X86.Sse.shuffle_ps(dp, dp, X86.Sse.SHUFFLE(0, 1, 2, 3)));
            return dp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_floor_ps(v128 a)
        {
            // DS: TODO: UNITY BURST FIX
            //using (var roundingMode = new X86.RoundingScope(X86.MXCSRBits.RoundDown))

            const X86.MXCSRBits roundingMode = X86.MXCSRBits.RoundDown;
            X86.MXCSRBits OldBits = X86.MXCSR;
            X86.MXCSR = (OldBits & ~X86.MXCSRBits.RoundingControlMask) | roundingMode;

            v128 rounded = X86.Sse2.cvtepi32_ps(X86.Sse2.cvtps_epi32(a));

            // DS: TODO: UNITY BURST FIX
            X86.MXCSR = OldBits;

            return rounded;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_ceil_ps(v128 a)
        {
            // DS: TODO: UNITY BURST FIX
            //using (var roundingMode = new X86.RoundingScope(X86.MXCSRBits.RoundUp))

            const X86.MXCSRBits roundingMode = X86.MXCSRBits.RoundUp;
            X86.MXCSRBits OldBits = X86.MXCSR;
            X86.MXCSR = (OldBits & ~X86.MXCSRBits.RoundingControlMask) | roundingMode;

            v128 rounded = X86.Sse2.cvtepi32_ps(X86.Sse2.cvtps_epi32(a));

            // DS: TODO: UNITY BURST FIX
            X86.MXCSR = OldBits;

            return rounded;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_transpose_epi8(v128 a)
        {
            // Perform transpose through two 16->8 bit pack and byte shifts
            v128 res = a;
            v128 mask = X86.Sse2.setr_epi8(~0, 0, ~0, 0, ~0, 0, ~0, 0, ~0, 0, ~0, 0, ~0, 0, ~0, 0);
            res = X86.Sse2.packus_epi16(X86.Sse2.and_si128(res, mask), X86.Sse2.srli_epi16(res, 8));
            res = X86.Sse2.packus_epi16(X86.Sse2.and_si128(res, mask), X86.Sse2.srli_epi16(res, 8));
            return res;
        }

        static readonly uint[] maskLUT = new uint[33] {
            ~0U << 0, ~0U << 1, ~0U << 2 ,  ~0U << 3, ~0U << 4, ~0U << 5, ~0U << 6 , ~0U << 7, ~0U << 8, ~0U << 9, ~0U << 10 , ~0U << 11, ~0U << 12, ~0U << 13, ~0U << 14 , ~0U << 15,
            ~0U << 16, ~0U << 17, ~0U << 18 , ~0U << 19, ~0U << 20, ~0U << 21, ~0U << 22 , ~0U << 23, ~0U << 24, ~0U << 25, ~0U << 26 , ~0U << 27, ~0U << 28, ~0U << 29, ~0U << 30 , ~0U << 31,
            0U
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static v128 _mmw_sllv_ones(v128 ishift)
        {
            v128 shift = _mmw_min_epi32(ishift, X86.Sse2.set1_epi32(32));

            // Uses scalar approach to perform _mm_sllv_epi32(~0, shift)

            v128 retMask = X86.Sse2.setzero_si128();    // DS: TODO: should not need to initialize?
            retMask.SInt0 = (int)maskLUT[shift.SInt0];
            retMask.SInt1 = (int)maskLUT[shift.SInt1];
            retMask.SInt2 = (int)maskLUT[shift.SInt2];
            retMask.SInt3 = (int)maskLUT[shift.SInt3];
            return retMask;
        }

#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong find_clear_lsb(ref uint mask)
        {
            ulong idx = (ulong)math.tzcnt(mask);
            mask &= mask - 1;
            return idx;
        }
    }

    public unsafe struct BurstIntrinsics
    {
        ////////////////////////

        // Common SSE2/SSE4.1 constants
        const int SIMD_LANES = 4;
        const int TILE_HEIGHT_SHIFT = 2;

        const int SIMD_ALL_LANES_MASK = (1 << SIMD_LANES) - 1;

        // Tile dimensions are 32xN pixels. These values are not tweakable and the code must also be modified
        // to support different tile sizes as it is tightly coupled with the SSE/AVX register size
        const int TILE_WIDTH_SHIFT = 5;

        const int TILE_WIDTH = 1 << TILE_WIDTH_SHIFT;
        const int TILE_HEIGHT = 1 << TILE_HEIGHT_SHIFT;

        // Sub-tiles (used for updating the masked HiZ buffer) are 8x4 tiles, so there are 4x2 sub-tiles in a tile
        const int SUB_TILE_WIDTH = 8;
        const int SUB_TILE_HEIGHT = 4;

        // The number of fixed point bits used to represent vertex coordinates / edge slopes.
#if MOC_PRECISE_COVERAGE
        const int FP_BITS = 8;
        const int FP_HALF_PIXEL = 1 << (FP_BITS - 1);
        const float FP_INV = 1f / (float)(1 << FP_BITS);
#else
        // Note that too low precision, without precise coverage, may cause overshoots / false coverage during rasterization.
        // This is configured for 14 bits for AVX512 and 16 bits for SSE. Max tile slope delta is roughly
        // (screenWidth + 2*(GUARD_BAND_PIXEL_SIZE + 1)) * (2^FP_BITS * (TILE_HEIGHT + GUARD_BAND_PIXEL_SIZE + 1))
        // and must fit in 31 bits. With this config, max image resolution (width) is ~3272, so stay well clear of this limit.
        const int FP_BITS = 19 - TILE_HEIGHT_SHIFT;
#endif

        // Tile dimensions in fixed point coordinates
        const int FP_TILE_HEIGHT_SHIFT = (FP_BITS + TILE_HEIGHT_SHIFT);
        const int FP_TILE_HEIGHT = (1 << FP_TILE_HEIGHT_SHIFT);

        // Maximum number of triangles that may be generated during clipping. We process SIMD_LANES triangles at a time and
        // clip against 5 planes, so the max should be 5*8 = 40 (we immediately draw the first clipped triangle).
        // This number must be a power of two.
        const int MAX_CLIPPED = (8 * SIMD_LANES);
        const int MAX_CLIPPED_WRAP = (MAX_CLIPPED - 1);

        // Size of guard band in pixels. Clipping doesn't seem to be very expensive so we use a small guard band
        // to improve rasterization performance. It's not recommended to set the guard band to zero, as this may
        // cause leakage along the screen border due to precision/rounding.
        const float GUARD_BAND_PIXEL_SIZE = 1f;

        // We classify triangles as big if the bounding box is wider than this given threshold and use a tighter
        // but slightly more expensive traversal algorithm. This improves performance greatly for sliver triangles
        const int BIG_TRIANGLE = 3;

        ////////////////////////

        static v128 SIMD_SUB_TILE_COL_OFFSET { get { return X86.Sse2.setr_epi32(0, SUB_TILE_WIDTH, SUB_TILE_WIDTH * 2, SUB_TILE_WIDTH * 3); } }

        static v128 SIMD_SUB_TILE_ROW_OFFSET { get { return X86.Sse2.setzero_si128(); } }

        static v128 SIMD_SUB_TILE_COL_OFFSET_F { get { return X86.Sse.setr_ps(0, SUB_TILE_WIDTH, SUB_TILE_WIDTH * 2, SUB_TILE_WIDTH * 3); } }

        static v128 SIMD_SUB_TILE_ROW_OFFSET_F { get { return X86.Sse2.setzero_si128(); } }

        static v128 SIMD_LANE_YCOORD_I { get { return X86.Sse2.setr_epi32(128, 384, 640, 896); } }

        static v128 SIMD_LANE_YCOORD_F { get { return X86.Sse.setr_ps(128f, 384f, 640f, 896f); } }

        static v128 SIMD_BITS_ONE { get { return X86.Sse2.set1_epi32(~0); } }

        static v128 SIMD_BITS_ZERO { get { return X86.Sse2.setzero_si128(); } }

        static v128 SIMD_TILE_WIDTH { get { return X86.Sse2.set1_epi32(TILE_WIDTH); } }

        static v128 SIMD_TILE_PAD { get { return X86.Sse2.setr_epi32(0, TILE_WIDTH, 0, TILE_HEIGHT); } }

        static v128 SIMD_TILE_PAD_MASK { get { return X86.Sse2.setr_epi32(~(TILE_WIDTH - 1), ~(TILE_WIDTH - 1), ~(TILE_HEIGHT - 1), ~(TILE_HEIGHT - 1)); } }

        static v128 SIMD_SUB_TILE_PAD { get { return X86.Sse2.setr_epi32(0, SUB_TILE_WIDTH, 0, SUB_TILE_HEIGHT); } }

        static v128 SIMD_SUB_TILE_PAD_MASK { get { return X86.Sse2.setr_epi32(~(SUB_TILE_WIDTH - 1), ~(SUB_TILE_WIDTH - 1), ~(SUB_TILE_HEIGHT - 1), ~(SUB_TILE_HEIGHT - 1)); } }

        static v128 SIMD_PAD_W_MASK { get { return X86.Sse2.set1_epi32(~(TILE_WIDTH - 1)); } }

        static v128 SIMD_PAD_H_MASK { get { return X86.Sse2.set1_epi32(~(TILE_HEIGHT - 1)); } }

        static v128 SIMD_LANE_IDX { get { return X86.Sse2.setr_epi32(0, 1, 2, 3); } }

        ////////////////////////
        public ZTile* mMaskedHiZBuffer { get;  private set; }

        //Need to contain 3 int
        [NativeDisableUnsafePtrRestriction]
        int* vertexOrder;
        const int vertexOrderCount = 3;

        //Need to contain 5 * Burst.Intrinsics.v128
        [NativeDisableUnsafePtrRestriction]
        v128* CSFrustumPlanes;
        const int CSFrustumPlanesCount = 5;

        v128 mHalfWidth;
        v128 mHalfHeight;
        v128 mCenterX;
        v128 mCenterY;

        v128 mIHalfSize;
        v128 mICenter;
        v128 mIScreenSize;

        float mNearDist;
        int mWidth;
        int mHeight;
        int mTilesWidth;
        int mTilesHeight;

        ScissorRect mFullscreenScissor;

        OcclusionCullingStatistics mStats;

        ////////////////////////

#if MOC_ENABLE_STATS
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void STATS_ADD(ref System.Int64 statMember, int val)
        {
            Interlocked.Add(ref statMember, val);
        }
#endif

        public void Create(uint width, uint height)
        {
            mMaskedHiZBuffer = null; //this is allocated in the SetResolution call below
            CSFrustumPlanes = (v128*)Memory.Unmanaged.Allocate(CSFrustumPlanesCount * sizeof(v128), 64, Allocator.Persistent);
            vertexOrder = (int*)Memory.Unmanaged.Allocate(vertexOrderCount * sizeof(int), 64, Allocator.Persistent);


#if MOC_USE_D3D
            vertexOrder[0] = 2;
            vertexOrder[1] = 1;
            vertexOrder[2] = 0;
#else
            vertexOrder[0] = 0;
            vertexOrder[1] = 1;
            vertexOrder[2] = 2;
#endif

            SetResolution(width, height);

        }

        public void Destroy()
        {
            if (mMaskedHiZBuffer != null)
            {
                Memory.Unmanaged.Free(mMaskedHiZBuffer, Allocator.Persistent);
                mMaskedHiZBuffer = null;
            }

            Memory.Unmanaged.Free(CSFrustumPlanes, Allocator.Persistent);
            Memory.Unmanaged.Free(vertexOrder, Allocator.Persistent);
        }

        void AllocateMaskedHiZBuffer(uint width, uint height)
        {
            uint tilesWidth = ((uint)(width + TILE_WIDTH - 1)) >> TILE_WIDTH_SHIFT;
            uint tilesHeight = ((uint)(height + TILE_HEIGHT - 1)) >> TILE_HEIGHT_SHIFT;

            const uint kBufferAlignment = 64u;

            int bufferLength = (int)(tilesWidth * tilesHeight);
            long bufferSize = bufferLength * (uint)UnsafeUtility.SizeOf<ZTile>();

            // TODO: Convert to NativeArray once it supports specfic alignment
            if (mMaskedHiZBuffer != null)
            {
                Memory.Unmanaged.Free(mMaskedHiZBuffer, Allocator.Persistent);
            }
            mMaskedHiZBuffer = (ZTile*)Memory.Unmanaged.Allocate(bufferSize, (int)kBufferAlignment, Allocator.Persistent);

            // Test alignment
            Debug.Assert((long)mMaskedHiZBuffer % kBufferAlignment == 0);
            Debug.Assert(((uint)mMaskedHiZBuffer & (kBufferAlignment - 1u)) == 0u);
        }

        public void SetResolution(uint width, uint height)
        {
            // Resolution must be a multiple of the subtile size
            Debug.Assert(width % SUB_TILE_WIDTH == 0 && height % SUB_TILE_HEIGHT == 0);

#if !MOC_PRECISE_COVERAGE
            // Test if combination of resolution & SLOPE_FP_BITS bits may cause 32-bit overflow. Note that the maximum resolution estimate
            // is only an estimate (not conservative). It's advicable to stay well below the limit.
            Debug.Assert(width<((1U << 31) - 1U) / ((1U << FP_BITS) * (TILE_HEIGHT + (uint)(GUARD_BAND_PIXEL_SIZE + 1.0f))) - (2U * (uint)(GUARD_BAND_PIXEL_SIZE + 1.0f)));
#endif

            // Setup various resolution dependent constant values
            mWidth = (int)width;
            mHeight = (int)height;
            mTilesWidth = (int)(width + TILE_WIDTH - 1) >> TILE_WIDTH_SHIFT;
            mTilesHeight = (int)(height + TILE_HEIGHT - 1) >> TILE_HEIGHT_SHIFT;

            mCenterX = X86.Sse.set1_ps((float)mWidth * 0.5f);
            mCenterY = X86.Sse.set1_ps((float)mHeight * 0.5f);
            mICenter = X86.Sse.setr_ps((float)mWidth * 0.5f, (float)mWidth * 0.5f, (float)mHeight * 0.5f, (float)mHeight * 0.5f);
            mHalfWidth = X86.Sse.set1_ps((float)mWidth * 0.5f);
#if MOC_USE_D3D
            mHalfHeight = X86.Sse.set1_ps((float)-mHeight * 0.5f);
            mIHalfSize = X86.Sse.setr_ps((float)mWidth * 0.5f, (float)mWidth * 0.5f, (float)-mHeight * 0.5f, (float)-mHeight * 0.5f);
#else
            mHalfHeight = X86.Sse.set1_ps((float)mHeight * 0.5f);
            mIHalfSize = X86.Sse.setr_ps((float)mWidth * 0.5f, (float)mWidth * 0.5f, (float)mHeight * 0.5f, (float)mHeight * 0.5f);
#endif
            mIScreenSize = X86.Sse2.setr_epi32(mWidth - 1, mWidth - 1, mHeight - 1, mHeight - 1);

            // Setup a full screen scissor rectangle
            mFullscreenScissor.mMinX = 0;
            mFullscreenScissor.mMinY = 0;
            mFullscreenScissor.mMaxX = mTilesWidth << TILE_WIDTH_SHIFT;
            mFullscreenScissor.mMaxY = mTilesHeight << TILE_HEIGHT_SHIFT;

            // Adjust clip planes to include a small guard band to avoid clipping leaks
            float guardBandWidth = (2f / (float)mWidth) * GUARD_BAND_PIXEL_SIZE;
            float guardBandHeight = (2f / (float)mHeight) * GUARD_BAND_PIXEL_SIZE;

            CSFrustumPlanes[1] = X86.Sse.setr_ps(1f - guardBandWidth, 0f, 1f, 0f);
            CSFrustumPlanes[2] = X86.Sse.setr_ps(-1f + guardBandWidth, 0f, 1f, 0f);
            CSFrustumPlanes[3] = X86.Sse.setr_ps(0f, 1f - guardBandHeight, 1f, 0f);
            CSFrustumPlanes[4] = X86.Sse.setr_ps(0f, -1f + guardBandHeight, 1f, 0f);

            AllocateMaskedHiZBuffer(width, height);
        }

        public void GetResolution(out uint width, out uint height)
        {
            width = (uint)mWidth;
            height = (uint)mHeight;
        }

        public void GetResolutionTiles(out uint tilesWidth, out uint tilesHeight)
        {
            tilesWidth = (uint)mTilesWidth;
            tilesHeight = (uint)mTilesHeight;
        }


        public void SetNearClipPlane(float nearDist)
        {
            mNearDist = nearDist;
            CSFrustumPlanes[0] = X86.Sse.setr_ps(0f, 0f, 1f, -nearDist);
        }


        public float GetNearClipPlane()
        {
            return mNearDist;
        }

        public unsafe void ClearBuffer()
        {
            int numTiles = mTilesWidth * mTilesHeight;

            // Iterate through all depth tiles and clear to default values
            for (int i = 0; i < numTiles; i++)
            {
                mMaskedHiZBuffer[i].mMask = X86.Sse2.setzero_si128();

                // Clear z0 to beyond infinity to ensure we never merge with clear data
                mMaskedHiZBuffer[i].mZMin0 = X86.Sse.set1_ps(-1f);
#if MOC_QUICK_MASK
                // Clear z1 to nearest depth value as it is pushed back on each update
                mMaskedHiZBuffer[i].mZMin1 = X86.Sse.set1_ps(float.MaxValue);
#else
                mMaskedHiZBuffer[i].mZMin1 = X86.Sse2.setzero_si128();
#endif
            }

#if MOC_ENABLE_STATS
            fixed (void* dst = &mStats)
            {
                UnsafeUtility.MemClear(dst, UnsafeUtility.SizeOf<OcclusionCullingStatistics>());
            }
#endif
        }

        public unsafe void MergeBufferTile(BurstIntrinsics* mocBurstInstrinsicsToMerge, int startTileID, int tileCount)
        {
            ZTile* MaskedHiZBufferB = mocBurstInstrinsicsToMerge->mMaskedHiZBuffer;

            for (int i = startTileID; i < startTileID + tileCount; i++)
            {
                v128* zMinB_0 = &(MaskedHiZBufferB[i].mZMin0);
                v128* zMinB_1 = &(MaskedHiZBufferB[i].mZMin1);

                v128* zMinA_0 = &(mMaskedHiZBuffer[i].mZMin0);
                v128* zMinA_1 = &(mMaskedHiZBuffer[i].mZMin1);

                v128 RastMaskB = MaskedHiZBufferB[i].mMask;
#if MOC_QUICK_MASK
                // Clear z0 to beyond infinity to ensure we never merge with clear data
                v128 sign0 = X86.Sse2.srai_epi32(*zMinB_0, 31);
                // Only merge tiles that have data in zMinB[0], use the sign bit to determine if they are still in a clear state
                sign0 = X86.Sse2.cmpeq_epi32(sign0, SIMD_BITS_ZERO);
                if (IntrinsicUtils._mmw_testz_epi32(sign0, sign0) == 0)
                {
#if MOC_ENABLE_STATS
                    STATS_ADD(ref mStats.mOccluders.mNumTilesMerged, 1);
#endif
                    *zMinA_0 = X86.Sse.max_ps(*zMinA_0, *zMinB_0);

                    v128 rastMask = mMaskedHiZBuffer[i].mMask;
                    v128 deadLane = X86.Sse2.cmpeq_epi32(rastMask, SIMD_BITS_ZERO);
                    // Mask out all subtiles failing the depth test (don't update these subtiles)
                    //X86.Sse2.or
                    deadLane = IntrinsicUtils._mmw_or_epi32(deadLane, X86.Sse2.srai_epi32(X86.Sse.sub_ps(*zMinA_1, *zMinA_0), 31));
                    mMaskedHiZBuffer[i].mMask = IntrinsicUtils._mmw_andnot_epi32(deadLane, rastMask);
                }

                // Set 32bit value to -1 if any pixels are set incide the coverage mask for a subtile
                v128 LiveTile = X86.Sse2.cmpeq_epi32(RastMaskB, SIMD_BITS_ZERO);
                // invert to have bits set for clear subtiles
                v128 t0inv = IntrinsicUtils._mmw_not_epi32(LiveTile);
                // VPTEST sets the ZF flag if all the resulting bits are 0 (ie if all tiles are clear)
                if (IntrinsicUtils._mmw_testz_epi32(t0inv, t0inv) == 0)
                {
#if MOC_ENABLE_STATS
                    STATS_ADD(ref mStats.mOccluders.mNumTilesMerged, 1);
#endif
                    UpdateTileQuick(i, RastMaskB, *zMinB_1);
                }
#else

                // Clear z0 to beyond infinity to ensure we never merge with clear data
                v128 sign1 = X86.Sse2.srai_epi32(mMaskedHiZBuffer[i].mZMin0, 31);
                // Only merge tiles that have data in zMinB[0], use the sign bit to determine if they are still in a clear state
                sign1 = X86.Sse2.cmpeq_epi32(sign1, SIMD_BITS_ZERO);

                // Set 32bit value to -1 if any pixels are set incide the coverage mask for a subtile
                v128 LiveTile1 = X86.Sse2.cmpeq_epi32(mMaskedHiZBuffer[i].mMask, SIMD_BITS_ZERO);
                // invert to have bits set for clear subtiles
                v128 t1inv = IntrinsicUtils._mmw_not_epi32(LiveTile1);
                // VPTEST sets the ZF flag if all the resulting bits are 0 (ie if all tiles are clear)
                if (IntrinsicUtils._mmw_testz_epi32(sign1, sign1) != 0 && IntrinsicUtils._mmw_testz_epi32(t1inv, t1inv) != 0)
                {
                    mMaskedHiZBuffer[i].mMask = MaskedHiZBufferB[i].mMask;
                    mMaskedHiZBuffer[i].mZMin0 = *zMinB_0;
                    mMaskedHiZBuffer[i].mZMin1 = *zMinB_1;
                }
                else
                {
                    // Clear z0 to beyond infinity to ensure we never merge with clear data
                    v128 sign0 = X86.Sse2.srai_epi32(*zMinB_0, 31);
                    sign0 = X86.Sse2.cmpeq_epi32(sign0, SIMD_BITS_ZERO);
                    // Only merge tiles that have data in zMinB[0], use the sign bit to determine if they are still in a clear state
                    if (IntrinsicUtils._mmw_testz_epi32(sign0, sign0) == 0)
                    {
                        // build a mask for Zmin[0], full if the layer has been completed, or partial if tile is still partly filled.
                        // cant just use the completement of the mask, as tiles might not get updated by merge
                        v128 sign_1 = X86.Sse2.srai_epi32(*zMinB_1, 31);
                        v128 LayerMask0 = IntrinsicUtils._mmw_not_epi32(sign_1);
                        v128 LayerMask1 = IntrinsicUtils._mmw_not_epi32(MaskedHiZBufferB[i].mMask);
                        v128 rastMask = IntrinsicUtils._mmw_or_epi32(LayerMask0, LayerMask1);

                        UpdateTileAccurate(i, rastMask, *zMinB_0);
                    }

                    // Set 32bit value to -1 if any pixels are set incide the coverage mask for a subtile
                    v128 LiveTile = X86.Sse2.cmpeq_epi32(MaskedHiZBufferB[i].mMask, SIMD_BITS_ZERO);
                    // invert to have bits set for clear subtiles
                    v128 t0inv = IntrinsicUtils._mmw_not_epi32(LiveTile);
                    // VPTEST sets the ZF flag if all the resulting bits are 0 (ie if all tiles are clear)
                    if (IntrinsicUtils._mmw_testz_epi32(t0inv, t0inv) == 0)
                    {
                        UpdateTileAccurate(i, MaskedHiZBufferB[i].mMask, *zMinB_1);
                    }

#if MOC_ENABLE_STATS
				    if (IntrinsicUtils._mmw_testz_epi32(sign0, sign0) != 0 && IntrinsicUtils._mmw_testz_epi32(t0inv, t0inv) != 0)
					    STATS_ADD(mStats.mOccluders.mNumTilesMerged, 1);
#endif

                }


#endif
            }

        }

        public unsafe void MergeBuffer(BurstIntrinsics* mocBurstInstrinsicsToMerge)
        {
            //// Iterate through all depth tiles and merge the 2 tiles
            MergeBufferTile(mocBurstInstrinsicsToMerge, 0, mTilesWidth * mTilesHeight);
        }


        public void ComputePixelDepthBuffer(float* depthData, bool flipY)
        {
            for (int y = 0; y < mHeight; y++)
            {
                for (int x = 0; x < mWidth; x++)
                {
                    // Compute 32xN tile index (SIMD value offset)
                    int tx = x / TILE_WIDTH;
                    int ty = y / TILE_HEIGHT;
                    int tileIdx = ty * mTilesWidth + tx;

                    // Compute 8x4 subtile index (SIMD lane offset)
                    int stx = (x % TILE_WIDTH) / SUB_TILE_WIDTH;
                    int sty = (y % TILE_HEIGHT) / SUB_TILE_HEIGHT;
                    int subTileIdx = sty * 4 + stx;

                    // Compute pixel index in subtile (bit index in 32-bit word)
                    int px = (x % SUB_TILE_WIDTH);
                    int py = (y % SUB_TILE_HEIGHT);
                    int bitIdx = py * 8 + px;

                    int pixelLayer = (IntrinsicUtils.getIntLane(mMaskedHiZBuffer[tileIdx].mMask, (uint)subTileIdx) >> bitIdx) & 1;
                    float pixelDepth = 0f;

                    if (pixelLayer == 0)
                    {
                        pixelDepth = IntrinsicUtils.getFloatLane(mMaskedHiZBuffer[tileIdx].mZMin0, (uint)subTileIdx);
                    }
                    else if (pixelLayer == 1)
                    {
                        pixelDepth = IntrinsicUtils.getFloatLane(mMaskedHiZBuffer[tileIdx].mZMin1, (uint)subTileIdx);
                    }
                    else
                    {
                        Debug.Assert(false);
                    }

                    if (flipY)
                    {
                        depthData[(mHeight - y - 1) * mWidth + x] = pixelDepth;
                    }
                    else
                    {
                        depthData[y * mWidth + x] = pixelDepth;
                    }
                }
            }
        }



        public unsafe CullingResult TestRect(float xmin, float ymin, float xmax, float ymax, float wmin)
        {
#if MOC_ENABLE_STATS
            STATS_ADD(ref mStats.mOccludees.mNumProcessedRectangles, 1);
#endif

            Debug.Assert(mMaskedHiZBuffer != null);

            //////////////////////////////////////////////////////////////////////////////
            // Compute screen space bounding box and guard for out of bounds
            //////////////////////////////////////////////////////////////////////////////
#if MOC_USE_D3D
            v128 pixelBBox = IntrinsicUtils._mmw_fmadd_ps(X86.Sse.setr_ps(xmin, xmax, ymax, ymin), mIHalfSize, mICenter);
#else
            v128 pixelBBox = IntrinsicUtils._mmw_fmadd_ps(X86.Sse.setr_ps(xmin, xmax, ymin, ymax), mIHalfSize, mICenter);
#endif

            v128 pixelBBoxi = X86.Sse2.cvttps_epi32(pixelBBox);
            pixelBBoxi = IntrinsicUtils._mmw_max_epi32(X86.Sse2.setzero_si128(), IntrinsicUtils._mmw_min_epi32(mIScreenSize, pixelBBoxi));

            //////////////////////////////////////////////////////////////////////////////
            // Pad bounding box to (32xN) tiles. Tile BB is used for looping / traversal
            //////////////////////////////////////////////////////////////////////////////
            v128 tileBBoxi = X86.Sse2.and_si128(X86.Sse2.add_epi32(pixelBBoxi, SIMD_TILE_PAD), SIMD_TILE_PAD_MASK);

            int txMin = tileBBoxi.SInt0 >> TILE_WIDTH_SHIFT;
            int txMax = tileBBoxi.SInt1 >> TILE_WIDTH_SHIFT;
            int tileRowIdx = (tileBBoxi.SInt2 >> TILE_HEIGHT_SHIFT) * mTilesWidth;
            int tileRowIdxEnd = (tileBBoxi.SInt3 >> TILE_HEIGHT_SHIFT) * mTilesWidth;

            if (tileBBoxi.SInt0 == tileBBoxi.SInt1 || tileBBoxi.SInt2 == tileBBoxi.SInt3)
            {
                return CullingResult.VIEW_CULLED;
            }

            ///////////////////////////////////////////////////////////////////////////////
            // Pad bounding box to (8x4) subtiles. Skip SIMD lanes outside the subtile BB
            ///////////////////////////////////////////////////////////////////////////////
            v128 subTileBBoxi = X86.Sse2.and_si128(X86.Sse2.add_epi32(pixelBBoxi, SIMD_SUB_TILE_PAD), SIMD_SUB_TILE_PAD_MASK);

            v128 stxmin = X86.Sse2.set1_epi32(subTileBBoxi.SInt0 - 1); // - 1 to be able to use GT test
            v128 stymin = X86.Sse2.set1_epi32(subTileBBoxi.SInt2 - 1); // - 1 to be able to use GT test
            v128 stxmax = X86.Sse2.set1_epi32(subTileBBoxi.SInt1);
            v128 stymax = X86.Sse2.set1_epi32(subTileBBoxi.SInt3);

            // Setup pixel coordinates used to discard lanes outside subtile BB
            v128 startPixelX = X86.Sse2.add_epi32(SIMD_SUB_TILE_COL_OFFSET, X86.Sse2.set1_epi32(tileBBoxi.SInt0));
            v128 pixelY = X86.Sse2.add_epi32(SIMD_SUB_TILE_ROW_OFFSET, X86.Sse2.set1_epi32(tileBBoxi.SInt2));

            //////////////////////////////////////////////////////////////////////////////
            // Compute z from w. Note that z is reversed order, 0 = far, 1 = near, which
            // means we use a greater than test, so zMax is used to test for visibility.
            //////////////////////////////////////////////////////////////////////////////
            v128 zMax = X86.Sse.div_ps(X86.Sse.set1_ps(1f), X86.Sse.set1_ps(wmin));

            for (; ; )
            {
                v128 pixelX = startPixelX;

                for (int tx = txMin; ;)
                {
#if MOC_ENABLE_STATS
                    STATS_ADD(ref mStats.mOccludees.mNumTilesTraversed, 1);
#endif

                    int tileIdx = tileRowIdx + tx;
                    Debug.Assert(tileIdx >= 0 && tileIdx < mTilesWidth * mTilesHeight);

                    // Fetch zMin from masked hierarchical Z buffer
#if MOC_QUICK_MASK
                    v128 zBuf = mMaskedHiZBuffer[tileIdx].mZMin0;
#else
                    v128 mask = mMaskedHiZBuffer[tileIdx].mMask;
                    v128 zMin0 = IntrinsicUtils._mmw_blendv_ps(mMaskedHiZBuffer[tileIdx].mZMin0, mMaskedHiZBuffer[tileIdx].mZMin1, X86.Sse2.cmpeq_epi32(mask, X86.Sse2.set1_epi32(~0)));
                    v128 zMin1 = IntrinsicUtils._mmw_blendv_ps(mMaskedHiZBuffer[tileIdx].mZMin1, mMaskedHiZBuffer[tileIdx].mZMin0, X86.Sse2.cmpeq_epi32(mask, X86.Sse2.setzero_si128()));
                    v128 zBuf = X86.Sse.min_ps(zMin0, zMin1);
#endif

                    // Perform conservative greater than test against hierarchical Z buffer (zMax >= zBuf means the subtile is visible)
                    v128 zPass = X86.Sse.cmpge_ps(zMax, zBuf);  //zPass = zMax >= zBuf ? ~0 : 0

                    // Mask out lanes corresponding to subtiles outside the bounding box
                    v128 bboxTestMin = X86.Sse2.and_si128(X86.Sse2.cmpgt_epi32(pixelX, stxmin), X86.Sse2.cmpgt_epi32(pixelY, stymin));
                    v128 bboxTestMax = X86.Sse2.and_si128(X86.Sse2.cmpgt_epi32(stxmax, pixelX), X86.Sse2.cmpgt_epi32(stymax, pixelY));
                    v128 boxMask = X86.Sse2.and_si128(bboxTestMin, bboxTestMax);
                    zPass = X86.Sse2.and_si128(zPass, boxMask);

                    // If not all tiles failed the conservative z test we can immediately terminate the test
                    if (IntrinsicUtils._mmw_testz_epi32(zPass, zPass) == 0)
                    {
                        return CullingResult.VISIBLE;
                    }

                    if (++tx >= txMax)
                    {
                        break;
                    }

                    pixelX = X86.Sse2.add_epi32(pixelX, X86.Sse2.set1_epi32(TILE_WIDTH));
                }

                tileRowIdx += mTilesWidth;

                if (tileRowIdx >= tileRowIdxEnd)
                {
                    break;
                }

                pixelY = X86.Sse2.add_epi32(pixelY, X86.Sse2.set1_epi32(TILE_HEIGHT));
            }

            return CullingResult.OCCLUDED;
        }

        ////// RasterizeTriangleBatch //////

        void UpdateTileQuick(int tileIdx, v128 coverage, v128 zTriv)
        {
#if MOC_ENABLE_STATS
            // Update heuristic used in the paper "Masked Software Occlusion Culling",
            // good balance between performance and accuracy
            STATS_ADD(ref mStats.mOccluders.mNumTilesUpdated, 1);
#endif

            Debug.Assert(tileIdx >= 0 && tileIdx < mTilesWidth * mTilesHeight);

            v128 mask = mMaskedHiZBuffer[tileIdx].mMask;
            v128 zMin0 = mMaskedHiZBuffer[tileIdx].mZMin0;
            v128 zMin1 = mMaskedHiZBuffer[tileIdx].mZMin1;

            // Swizzle coverage mask to 8x4 subtiles and test if any subtiles are not covered at all
            v128 rastMask = coverage;
            v128 deadLane = X86.Sse2.cmpeq_epi32(rastMask, SIMD_BITS_ZERO);

            // Mask out all subtiles failing the depth test (don't update these subtiles)
            deadLane = X86.Sse2.or_si128(deadLane, X86.Sse2.srai_epi32(X86.Sse.sub_ps(zTriv, zMin0), 31));
            rastMask = X86.Sse2.andnot_si128(deadLane, rastMask);

            // Use distance heuristic to discard layer 1 if incoming triangle is significantly nearer to observer
            // than the buffer contents. See Section 3.2 in "Masked Software Occlusion Culling"
            v128 coveredLane = X86.Sse2.cmpeq_epi32(rastMask, SIMD_BITS_ONE);
            v128 diff = IntrinsicUtils._mmw_fmsub_ps(zMin1, X86.Sse.set1_ps(2f), X86.Sse.add_ps(zTriv, zMin0));
            v128 discardLayerMask = X86.Sse2.andnot_si128(deadLane, X86.Sse2.or_si128(X86.Sse2.srai_epi32(diff, 31), coveredLane));

            // Update the mask with incoming triangle coverage
            mask = X86.Sse2.or_si128(X86.Sse2.andnot_si128(discardLayerMask, mask), rastMask);

            v128 maskFull = X86.Sse2.cmpeq_epi32(mask, SIMD_BITS_ONE);

            // Compute new value for zMin[1]. This has one of four outcomes: zMin[1] = min(zMin[1], zTriv),  zMin[1] = zTriv,
            // zMin[1] = FLT_MAX or unchanged, depending on if the layer is updated, discarded, fully covered, or not updated
            v128 opA = IntrinsicUtils._mmw_blendv_ps(zTriv, zMin1, deadLane);
            v128 opB = IntrinsicUtils._mmw_blendv_ps(zMin1, zTriv, discardLayerMask);
            v128 z1min = X86.Sse.min_ps(opA, opB);

            mMaskedHiZBuffer[tileIdx].mZMin1 = IntrinsicUtils._mmw_blendv_ps(z1min, X86.Sse.set1_ps(float.MaxValue), maskFull);
            // Propagate zMin[1] back to zMin[0] if tile was fully covered, and update the mask
            mMaskedHiZBuffer[tileIdx].mZMin0 = IntrinsicUtils._mmw_blendv_ps(zMin0, z1min, maskFull);
            mMaskedHiZBuffer[tileIdx].mMask = X86.Sse2.andnot_si128(maskFull, mask);
        }

        void UpdateTileAccurate(int tileIdx, v128 coverage, v128 zTriv)
        {
            Debug.Assert(tileIdx >= 0 && tileIdx < mTilesWidth * mTilesHeight);

            v128 zMin0 = mMaskedHiZBuffer[tileIdx].mZMin0;
            v128 zMin1 = mMaskedHiZBuffer[tileIdx].mZMin1;
            v128 mask = mMaskedHiZBuffer[tileIdx].mMask;

            // Swizzle coverage mask to 8x4 subtiles
            v128 rastMask = coverage;

            // Perform individual depth tests with layer 0 & 1 and mask out all failing pixels
            v128 sdist0 = X86.Sse.sub_ps(zMin0, zTriv);
            v128 sdist1 = X86.Sse.sub_ps(zMin1, zTriv);
            v128 sign0 = X86.Sse2.srai_epi32(sdist0, 31);
            v128 sign1 = X86.Sse2.srai_epi32(sdist1, 31);
            v128 triMask = X86.Sse2.and_si128(rastMask, X86.Sse2.or_si128(X86.Sse2.andnot_si128(mask, sign0), X86.Sse2.and_si128(mask, sign1)));

            // Early out if no pixels survived the depth test (this test is more accurate than
            // the early culling test in TraverseScanline())
            v128 t0 = X86.Sse2.cmpeq_epi32(triMask, SIMD_BITS_ZERO);
            v128 t0inv = IntrinsicUtils._mmw_not_epi32(t0);

            if (IntrinsicUtils._mmw_testz_epi32(t0inv, t0inv) != 0)
            {
                return;
            }

#if MOC_ENABLE_STATS
            STATS_ADD(ref mStats.mOccluders.mNumTilesUpdated, 1);
#endif

            v128 zTri = IntrinsicUtils._mmw_blendv_ps(zTriv, zMin0, t0);

            // Test if incoming triangle completely overwrites layer 0 or 1
            v128 layerMask0 = X86.Sse2.andnot_si128(triMask, IntrinsicUtils._mmw_not_epi32(mask));
            v128 layerMask1 = X86.Sse2.andnot_si128(triMask, mask);
            v128 lm0 = X86.Sse2.cmpeq_epi32(layerMask0, SIMD_BITS_ZERO);
            v128 lm1 = X86.Sse2.cmpeq_epi32(layerMask1, SIMD_BITS_ZERO);
            v128 z0 = IntrinsicUtils._mmw_blendv_ps(zMin0, zTri, lm0);
            v128 z1 = IntrinsicUtils._mmw_blendv_ps(zMin1, zTri, lm1);

            // Compute distances used for merging heuristic
            v128 d0 = IntrinsicUtils._mmw_abs_ps(sdist0);
            v128 d1 = IntrinsicUtils._mmw_abs_ps(sdist1);
            v128 d2 = IntrinsicUtils._mmw_abs_ps(X86.Sse.sub_ps(z0, z1));

            // Find minimum distance
            v128 c01 = X86.Sse.sub_ps(d0, d1);
            v128 c02 = X86.Sse.sub_ps(d0, d2);
            v128 c12 = X86.Sse.sub_ps(d1, d2);
            // Two tests indicating which layer the incoming triangle will merge with or
            // overwrite. d0min indicates that the triangle will overwrite layer 0, and
            // d1min flags that the triangle will overwrite layer 1.
            v128 d0min = X86.Sse2.or_si128(X86.Sse2.and_si128(c01, c02), X86.Sse2.or_si128(lm0, t0));
            v128 d1min = X86.Sse2.andnot_si128(d0min, X86.Sse2.or_si128(c12, lm1));

            ///////////////////////////////////////////////////////////////////////////////
            // Update depth buffer entry. NOTE: we always merge into layer 0, so if the
            // triangle should be merged with layer 1, we first swap layer 0 & 1 and then
            // merge into layer 0.
            ///////////////////////////////////////////////////////////////////////////////

            // Update mask based on which layer the triangle overwrites or was merged into
            v128 inner = IntrinsicUtils._mmw_blendv_ps(triMask, layerMask1, d0min);

            // Update the zMin[0] value. There are four outcomes: overwrite with layer 1,
            // merge with layer 1, merge with zTri or overwrite with layer 1 and then merge
            // with zTri.
            v128 e0 = IntrinsicUtils._mmw_blendv_ps(z0, z1, d1min);
            v128 e1 = IntrinsicUtils._mmw_blendv_ps(z1, zTri, X86.Sse2.or_si128(d1min, d0min));

            // Update the zMin[1] value. There are three outcomes: keep current value,
            // overwrite with zTri, or overwrite with z1
            v128 z1t = IntrinsicUtils._mmw_blendv_ps(zTri, z1, d0min);

            mMaskedHiZBuffer[tileIdx].mZMin0 = X86.Sse.min_ps(e0, e1);
            mMaskedHiZBuffer[tileIdx].mZMin1 = IntrinsicUtils._mmw_blendv_ps(z1t, z0, d1min);
            mMaskedHiZBuffer[tileIdx].mMask = IntrinsicUtils._mmw_blendv_ps(inner, layerMask0, d1min);
        }

        int TraverseScanline(int TEST_Z, int NRIGHT, int NLEFT, int leftOffset, int rightOffset, int tileIdx, int rightEvent, int leftEvent, v128* events, v128 zTriMin, v128 zTriMax, v128 iz0, float zx)
        {
            // Floor edge events to integer pixel coordinates (shift out fixed point bits)
            int eventOffset = leftOffset << TILE_WIDTH_SHIFT;

            v128* right = stackalloc v128[NRIGHT];
            v128* left = stackalloc v128[NLEFT];

            for (int i = 0; i < NRIGHT; ++i)
            {
                right[i] = IntrinsicUtils._mmw_max_epi32(X86.Sse2.sub_epi32(X86.Sse2.srai_epi32(events[rightEvent + i], FP_BITS), X86.Sse2.set1_epi32(eventOffset)), SIMD_BITS_ZERO);
            }

            for (int i = 0; i < NLEFT; ++i)
            {
                left[i] = IntrinsicUtils._mmw_max_epi32(X86.Sse2.sub_epi32(X86.Sse2.srai_epi32(events[leftEvent - i], FP_BITS), X86.Sse2.set1_epi32(eventOffset)), SIMD_BITS_ZERO);
            }

            v128 z0 = X86.Sse.add_ps(iz0, X86.Sse.set1_ps(zx * leftOffset));
            int tileIdxEnd = tileIdx + rightOffset;
            tileIdx += leftOffset;

            for (; ; )
            {
#if MOC_ENABLE_STATS
                if (TEST_Z != 0)
                {
                    STATS_ADD(ref mStats.mOccludees.mNumTilesTraversed, 1);
                }
                else
                {
                    STATS_ADD(ref mStats.mOccluders.mNumTilesTraversed, 1);
                }
#endif

                // Perform a coarse test to quickly discard occluded tiles
#if MOC_QUICK_MASK
                // Only use the reference layer (layer 0) to cull as it is always conservative
                v128 zMinBuf = mMaskedHiZBuffer[tileIdx].mZMin0;
#else
                // Compute zMin for the overlapped layers
                v128 mask = mMaskedHiZBuffer[tileIdx].mMask;
                v128 zMin0 = IntrinsicUtils._mmw_blendv_ps(mMaskedHiZBuffer[tileIdx].mZMin0, mMaskedHiZBuffer[tileIdx].mZMin1, X86.Sse2.cmpeq_epi32(mask, X86.Sse2.set1_epi32(~0)));
                v128 zMin1 = IntrinsicUtils._mmw_blendv_ps(mMaskedHiZBuffer[tileIdx].mZMin1, mMaskedHiZBuffer[tileIdx].mZMin0, X86.Sse2.cmpeq_epi32(mask, X86.Sse2.setzero_si128()));
                v128 zMinBuf = X86.Sse.min_ps(zMin0, zMin1);
#endif
                v128 dist0 = X86.Sse.sub_ps(zTriMax, zMinBuf);

                if (X86.Sse.movemask_ps(dist0) != SIMD_ALL_LANES_MASK)
                {
                    // Compute coverage mask for entire 32xN using shift operations
                    v128 accumulatedMask = IntrinsicUtils._mmw_sllv_ones(left[0]);

                    for (int i = 1; i < NLEFT; ++i)
                    {
                        accumulatedMask = X86.Sse2.and_si128(accumulatedMask, IntrinsicUtils._mmw_sllv_ones(left[i]));
                    }

                    for (int i = 0; i < NRIGHT; ++i)
                    {
                        accumulatedMask = X86.Sse2.andnot_si128(IntrinsicUtils._mmw_sllv_ones(right[i]), accumulatedMask);
                    }

                    if (TEST_Z != 0)
                    {
                        // Perform a conservative visibility test (test zMax against buffer for each covered 8x4 subtile)
                        v128 zSubTileMax = X86.Sse.min_ps(z0, zTriMax);
                        v128 zPass = X86.Sse.cmpge_ps(zSubTileMax, zMinBuf);

                        v128 rastMask = IntrinsicUtils._mmw_transpose_epi8(accumulatedMask);
                        v128 deadLane = X86.Sse2.cmpeq_epi32(rastMask, SIMD_BITS_ZERO);
                        zPass = X86.Sse2.andnot_si128(deadLane, zPass);

                        if (IntrinsicUtils._mmw_testz_epi32(zPass, zPass) == 0)
                        {
                            return (int)CullingResult.VISIBLE;
                        }
                    }
                    else
                    {
                        // Compute interpolated min for each 8x4 subtile and update the masked hierarchical z buffer entry
                        v128 zSubTileMin = X86.Sse.max_ps(z0, zTriMin);
#if MOC_QUICK_MASK
                        UpdateTileQuick(tileIdx, IntrinsicUtils._mmw_transpose_epi8(accumulatedMask), zSubTileMin);
#else
                        UpdateTileAccurate(tileIdx, IntrinsicUtils._mmw_transpose_epi8(accumulatedMask), zSubTileMin);
#endif
                    }
                }

                // Update buffer address, interpolate z and edge events
                tileIdx++;

                if (tileIdx >= tileIdxEnd)
                {
                    break;
                }

                z0 = X86.Sse.add_ps(z0, X86.Sse.set1_ps(zx));

                for (int i = 0; i < NRIGHT; ++i)
                {
                    right[i] = X86.Sse2.subs_epu16(right[i], SIMD_TILE_WIDTH);  // Trick, use sub saturated to avoid checking against < 0 for shift (values should fit in 16 bits)
                }

                for (int i = 0; i < NLEFT; ++i)
                {
                    left[i] = X86.Sse2.subs_epu16(left[i], SIMD_TILE_WIDTH);
                }
            }

            return (int)(TEST_Z != 0 ? CullingResult.OCCLUDED : CullingResult.VISIBLE);
        }

#if MOC_PRECISE_COVERAGE
        static void UPDATE_TILE_EVENTS_Y(v128* triEventRemainder, v128* triSlopeTileRemainder, v128* triEdgeY, v128* triEvent, v128* triSlopeTileDelta, v128* triSlopeSign, int i)
        {
            triEventRemainder[i] = X86.Sse2.sub_epi32(triEventRemainder[i], triSlopeTileRemainder[i]);
            v128 overflow = X86.Sse2.srai_epi32(triEventRemainder[i], 31);
            triEventRemainder[i] = X86.Sse2.add_epi32(triEventRemainder[i], X86.Sse2.and_si128(overflow, triEdgeY[i]));
            triEvent[i] = X86.Sse2.add_epi32(triEvent[i], X86.Sse2.add_epi32(triSlopeTileDelta[i], X86.Sse2.and_si128(overflow, triSlopeSign[i])));
        }
#else
        static void UPDATE_TILE_EVENTS_Y(v128* triEvent, v128* triSlopeTileDelta, int i)
        {
            triEvent[i] = X86.Sse2.add_epi32(triEvent[i], triSlopeTileDelta[i]);
        }
#endif

#if MOC_PRECISE_COVERAGE
        int RasterizeTriangle(int TEST_Z, int TIGHT_TRAVERSAL, int MID_VTX_RIGHT, uint triIdx, int bbWidth, int tileRowIdx, int tileMidRowIdx, int tileEndRowIdx, v128* eventStart, v128* slope, v128* slopeTileDelta, v128 zTriMin, v128 zTriMax, ref v128 z0, float zx, float zy, v128* edgeY, v128* absEdgeX, v128* slopeSign, v128* eventStartRemainder, v128* slopeTileRemainder)
#else
        int RasterizeTriangle(int TEST_Z, int TIGHT_TRAVERSAL, int MID_VTX_RIGHT, uint triIdx, int bbWidth, int tileRowIdx, int tileMidRowIdx, int tileEndRowIdx, v128* eventStart, v128* slope, v128* slopeTileDelta, v128 zTriMin, v128 zTriMax, ref v128 z0, float zx, float zy)
#endif
        {
#if MOC_ENABLE_STATS
            if (TEST_Z != 0)
            {
                STATS_ADD(ref mStats.mOccludees.mNumRasterizedTriangles, 1);
            }
            else
            {
                STATS_ADD(ref mStats.mOccluders.mNumRasterizedTriangles, 1);
            }
#endif

            int cullResult;

#if MOC_PRECISE_COVERAGE

            const int LEFT_EDGE_BIAS = -1;
            const int RIGHT_EDGE_BIAS = 1;

            v128* triSlopeSign = stackalloc v128[3];
            v128* triSlopeTileDelta = stackalloc v128[3];
            v128* triEdgeY = stackalloc v128[3];
            v128* triSlopeTileRemainder = stackalloc v128[3];
            v128* triEventRemainder = stackalloc v128[3];
            v128* triEvent = stackalloc v128[3];

            for (int i = 0; i < 3; ++i)
            {
                triSlopeSign[i] = X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(slopeSign[i], triIdx));
                triSlopeTileDelta[i] = X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(slopeTileDelta[i], triIdx));
                triEdgeY[i] = X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(edgeY[i], triIdx));
                triSlopeTileRemainder[i] = X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(slopeTileRemainder[i], triIdx));

                v128 triSlope = X86.Sse.set1_ps(IntrinsicUtils.getFloatLane(slope[i], triIdx));
                v128 triAbsEdgeX = X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(absEdgeX[i], triIdx));
                v128 triStartRemainder = X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(eventStartRemainder[i], triIdx));
                v128 triEventStart = X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(eventStart[i], triIdx));

                v128 scanlineDelta = X86.Sse2.cvttps_epi32(X86.Sse.mul_ps(triSlope, SIMD_LANE_YCOORD_F));
                v128 scanlineSlopeRemainder = X86.Sse2.sub_epi32(IntrinsicUtils._mmw_mullo_epi32(triAbsEdgeX, SIMD_LANE_YCOORD_I),
                                                                 IntrinsicUtils._mmw_mullo_epi32(IntrinsicUtils._mmw_abs_epi32(scanlineDelta), triEdgeY[i]));

                triEventRemainder[i] = X86.Sse2.sub_epi32(triStartRemainder, scanlineSlopeRemainder);
                v128 overflow = X86.Sse2.srai_epi32(triEventRemainder[i], 31);
                triEventRemainder[i] = X86.Sse2.add_epi32(triEventRemainder[i], X86.Sse2.and_si128(overflow, triEdgeY[i]));
                triEvent[i] = X86.Sse2.add_epi32(X86.Sse2.add_epi32(triEventStart, scanlineDelta), X86.Sse2.and_si128(overflow, triSlopeSign[i]));
            }

#else

            const int LEFT_EDGE_BIAS = 0;
            const int RIGHT_EDGE_BIAS = 0;

            // Get deltas used to increment edge events each time we traverse one scanline of tiles
            v128* triSlopeTileDelta = stackalloc v128[3];
            triSlopeTileDelta[0] = X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(slopeTileDelta[0], triIdx));
            triSlopeTileDelta[1] = X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(slopeTileDelta[1], triIdx));
            triSlopeTileDelta[2] = X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(slopeTileDelta[2], triIdx));

            // Setup edge events for first batch of SIMD_LANES scanlines
            v128* triEvent = stackalloc v128[3];
            triEvent[0] = X86.Sse2.add_epi32(X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(eventStart[0], triIdx)),
                                             IntrinsicUtils._mmw_mullo_epi32(SIMD_LANE_IDX, X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(slope[0], triIdx))));
            triEvent[1] = X86.Sse2.add_epi32(X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(eventStart[1], triIdx)),
                                             IntrinsicUtils._mmw_mullo_epi32(SIMD_LANE_IDX, X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(slope[1], triIdx))));
            triEvent[2] = X86.Sse2.add_epi32(X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(eventStart[2], triIdx)),
                                             IntrinsicUtils._mmw_mullo_epi32(SIMD_LANE_IDX, X86.Sse2.set1_epi32(IntrinsicUtils.getIntLane(slope[2], triIdx))));

#endif

            // For big triangles track start & end tile for each scanline and only traverse the valid region
            int startDelta = 0;
            int endDelta = 0;
            int topDelta = 0;
            int startEvent = 0;
            int endEvent = 0;
            int topEvent = 0;

            if (TIGHT_TRAVERSAL != 0)
            {
                startDelta = IntrinsicUtils.getIntLane(slopeTileDelta[2], triIdx) + LEFT_EDGE_BIAS;
                endDelta = IntrinsicUtils.getIntLane(slopeTileDelta[0], triIdx) + RIGHT_EDGE_BIAS;
                topDelta = IntrinsicUtils.getIntLane(slopeTileDelta[1], triIdx) + (MID_VTX_RIGHT != 0 ? RIGHT_EDGE_BIAS : LEFT_EDGE_BIAS);

                // Compute conservative bounds for the edge events over a 32xN tile
                startEvent = IntrinsicUtils.getIntLane(eventStart[2], triIdx) + Mathf.Min(0, startDelta);
                endEvent = IntrinsicUtils.getIntLane(eventStart[0], triIdx) + Mathf.Max(0, endDelta) + (TILE_WIDTH << FP_BITS);

                if (MID_VTX_RIGHT != 0)
                {
                    topEvent = IntrinsicUtils.getIntLane(eventStart[1], triIdx) + Mathf.Max(0, topDelta) + (TILE_WIDTH << FP_BITS);
                }
                else
                {
                    topEvent = IntrinsicUtils.getIntLane(eventStart[1], triIdx) + Mathf.Min(0, topDelta);
                }
            }

            if (tileRowIdx <= tileMidRowIdx)
            {
                int tileStopIdx = Mathf.Min(tileEndRowIdx, tileMidRowIdx);

                // Traverse the bottom half of the triangle
                while (tileRowIdx < tileStopIdx)
                {
                    int start = 0;
                    int end = bbWidth;

                    if (TIGHT_TRAVERSAL != 0)
                    {
                        // Compute tighter start and endpoints to avoid traversing empty space
                        start = Mathf.Max(0, Mathf.Min(bbWidth - 1, startEvent >> (TILE_WIDTH_SHIFT + FP_BITS)));
                        end = Mathf.Min(bbWidth, ((int)endEvent >> (TILE_WIDTH_SHIFT + FP_BITS)));

                        startEvent += startDelta;
                        endEvent += endDelta;
                    }

                    // Traverse the scanline and update the masked hierarchical z buffer
                    cullResult = TraverseScanline(TEST_Z, 1, 1, start, end, tileRowIdx, 0, 2, triEvent, zTriMin, zTriMax, z0, zx);

                    if (TEST_Z != 0 && cullResult == (int)CullingResult.VISIBLE)
                    {
                        // Early out if performing occlusion query
                        return (int)CullingResult.VISIBLE;
                    }

                    // move to the next scanline of tiles, update edge events and interpolate z
                    tileRowIdx += mTilesWidth;
                    z0 = X86.Sse.add_ps(z0, X86.Sse.set1_ps(zy));

#if MOC_PRECISE_COVERAGE
                    UPDATE_TILE_EVENTS_Y(triEventRemainder, triSlopeTileRemainder, triEdgeY, triEvent, triSlopeTileDelta, triSlopeSign, 0);
                    UPDATE_TILE_EVENTS_Y(triEventRemainder, triSlopeTileRemainder, triEdgeY, triEvent, triSlopeTileDelta, triSlopeSign, 2);
#else
                    UPDATE_TILE_EVENTS_Y(triEvent, triSlopeTileDelta, 0);
                    UPDATE_TILE_EVENTS_Y(triEvent, triSlopeTileDelta, 2);
#endif
                }

                // Traverse the middle scanline of tiles. We must consider all three edges only in this region
                if (tileRowIdx < tileEndRowIdx)
                {
                    int start = 0;
                    int end = bbWidth;

                    if (TIGHT_TRAVERSAL > 0)
                    {
                        // Compute tighter start and endpoints to avoid traversing lots of empty space
                        start = Mathf.Max(0, Mathf.Min(bbWidth - 1, startEvent >> (TILE_WIDTH_SHIFT + FP_BITS)));
                        end = Mathf.Min(bbWidth, ((int)endEvent >> (TILE_WIDTH_SHIFT + FP_BITS)));

                        // Switch the traversal start / end to account for the upper side edge
                        endEvent = MID_VTX_RIGHT != 0 ? topEvent : endEvent;
                        endDelta = MID_VTX_RIGHT != 0 ? topDelta : endDelta;
                        startEvent = MID_VTX_RIGHT != 0 ? startEvent : topEvent;
                        startDelta = MID_VTX_RIGHT != 0 ? startDelta : topDelta;

                        startEvent += startDelta;
                        endEvent += endDelta;
                    }

                    // Traverse the scanline and update the masked hierarchical z buffer.
                    if (MID_VTX_RIGHT != 0)
                    {
                        cullResult = TraverseScanline(TEST_Z, 2, 1, start, end, tileRowIdx, 0, 2, triEvent, zTriMin, zTriMax, z0, zx);
                    }
                    else
                    {
                        cullResult = TraverseScanline(TEST_Z, 1, 2, start, end, tileRowIdx, 0, 2, triEvent, zTriMin, zTriMax, z0, zx);
                    }

                    if (TEST_Z != 0 && cullResult == (int)CullingResult.VISIBLE)
                    {
                        // Early out if performing occlusion query
                        return (int)CullingResult.VISIBLE;
                    }

                    tileRowIdx += mTilesWidth;
                }

                // Traverse the top half of the triangle
                if (tileRowIdx < tileEndRowIdx)
                {
                    // move to the next scanline of tiles, update edge events and interpolate z
                    z0 = X86.Sse.add_ps(z0, X86.Sse.set1_ps(zy));
                    int i0 = MID_VTX_RIGHT + 0;
                    int i1 = MID_VTX_RIGHT + 1;

#if MOC_PRECISE_COVERAGE
                    UPDATE_TILE_EVENTS_Y(triEventRemainder, triSlopeTileRemainder, triEdgeY, triEvent, triSlopeTileDelta, triSlopeSign, i0);
                    UPDATE_TILE_EVENTS_Y(triEventRemainder, triSlopeTileRemainder, triEdgeY, triEvent, triSlopeTileDelta, triSlopeSign, i1);
#else
                    UPDATE_TILE_EVENTS_Y(triEvent, triSlopeTileDelta, i0);
                    UPDATE_TILE_EVENTS_Y(triEvent, triSlopeTileDelta, i1);
#endif

                    for (; ; )
                    {
                        int start = 0;
                        int end = bbWidth;

                        if (TIGHT_TRAVERSAL != 0)
                        {
                            // Compute tighter start and endpoints to avoid traversing lots of empty space
                            start = Mathf.Max(0, Mathf.Min(bbWidth - 1, startEvent >> (TILE_WIDTH_SHIFT + FP_BITS)));
                            end = Mathf.Min(bbWidth, ((int)endEvent >> (TILE_WIDTH_SHIFT + FP_BITS)));

                            startEvent += startDelta;
                            endEvent += endDelta;
                        }

                        // Traverse the scanline and update the masked hierarchical z buffer
                        cullResult = TraverseScanline(TEST_Z, 1, 1, start, end, tileRowIdx, MID_VTX_RIGHT + 0, MID_VTX_RIGHT + 1, triEvent, zTriMin, zTriMax, z0, zx);

                        if (TEST_Z != 0 && cullResult == (int)CullingResult.VISIBLE)
                        {
                            // Early out if performing occlusion query
                            return (int)CullingResult.VISIBLE;
                        }

                        // move to the next scanline of tiles, update edge events and interpolate z
                        tileRowIdx += mTilesWidth;
                        if (tileRowIdx >= tileEndRowIdx)
                        {
                            break;
                        }

                        z0 = X86.Sse.add_ps(z0, X86.Sse.set1_ps(zy));

#if MOC_PRECISE_COVERAGE
                        UPDATE_TILE_EVENTS_Y(triEventRemainder, triSlopeTileRemainder, triEdgeY, triEvent, triSlopeTileDelta, triSlopeSign, i0);
                        UPDATE_TILE_EVENTS_Y(triEventRemainder, triSlopeTileRemainder, triEdgeY, triEvent, triSlopeTileDelta, triSlopeSign, i1);
#else
                        UPDATE_TILE_EVENTS_Y(triEvent, triSlopeTileDelta, i0);
                        UPDATE_TILE_EVENTS_Y(triEvent, triSlopeTileDelta, i1);
#endif
                    }
                }
            }
            else
            {
                if (TIGHT_TRAVERSAL != 0)
                {
                    // For large triangles, switch the traversal start / end to account for the upper side edge
                    endEvent = MID_VTX_RIGHT != 0 ? topEvent : endEvent;
                    endDelta = MID_VTX_RIGHT != 0 ? topDelta : endDelta;
                    startEvent = MID_VTX_RIGHT != 0 ? startEvent : topEvent;
                    startDelta = MID_VTX_RIGHT != 0 ? startDelta : topDelta;
                }

                // Traverse the top half of the triangle
                if (tileRowIdx < tileEndRowIdx)
                {
                    int i0 = MID_VTX_RIGHT + 0;
                    int i1 = MID_VTX_RIGHT + 1;

                    for (; ; )
                    {
                        int start = 0;
                        int end = bbWidth;

                        if (TIGHT_TRAVERSAL != 0)
                        {
                            // Compute tighter start and endpoints to avoid traversing lots of empty space
                            start = Mathf.Max(0, Mathf.Min(bbWidth - 1, startEvent >> (TILE_WIDTH_SHIFT + FP_BITS)));
                            end = Mathf.Min(bbWidth, ((int)endEvent >> (TILE_WIDTH_SHIFT + FP_BITS)));

                            startEvent += startDelta;
                            endEvent += endDelta;
                        }

                        // Traverse the scanline and update the masked hierarchical z buffer
                        cullResult = TraverseScanline(TEST_Z, 1, 1, start, end, tileRowIdx, MID_VTX_RIGHT + 0, MID_VTX_RIGHT + 1, triEvent, zTriMin, zTriMax, z0, zx);

                        if (TEST_Z != 0 && cullResult == (int)CullingResult.VISIBLE)
                        {
                            // Early out if performing occlusion query
                            return (int)CullingResult.VISIBLE;
                        }

                        // move to the next scanline of tiles, update edge events and interpolate z
                        tileRowIdx += mTilesWidth;
                        if (tileRowIdx >= tileEndRowIdx)
                        {
                            break;
                        }

                        z0 = X86.Sse.add_ps(z0, X86.Sse.set1_ps(zy));

#if MOC_PRECISE_COVERAGE
                        UPDATE_TILE_EVENTS_Y(triEventRemainder, triSlopeTileRemainder, triEdgeY, triEvent, triSlopeTileDelta, triSlopeSign, i0);
                        UPDATE_TILE_EVENTS_Y(triEventRemainder, triSlopeTileRemainder, triEdgeY, triEvent, triSlopeTileDelta, triSlopeSign, i1);
#else
                        UPDATE_TILE_EVENTS_Y(triEvent, triSlopeTileDelta, i0);
                        UPDATE_TILE_EVENTS_Y(triEvent, triSlopeTileDelta, i1);
#endif
                    }
                }
            }

            return (int)(TEST_Z != 0 ? CullingResult.OCCLUDED : CullingResult.VISIBLE);
        }

#if MOC_PRECISE_COVERAGE

        static void SortVertices(v128* vX, v128* vY)
        {
            // Rotate the triangle in the winding order until v0 is the vertex with lowest Y value
            for (int i = 0; i < 2; i++)
            {
                v128 ey1 = X86.Sse2.sub_epi32(vY[1], vY[0]);
                v128 ey2 = X86.Sse2.sub_epi32(vY[2], vY[0]);
                v128 swapMask = X86.Sse2.or_si128(X86.Sse2.or_si128(ey1, ey2), X86.Sse2.cmpeq_epi32(ey2, X86.Sse2.setzero_si128()));

                v128 sX = IntrinsicUtils._mmw_blendv_ps(vX[2], vX[0], swapMask);
                vX[0] = IntrinsicUtils._mmw_blendv_ps(vX[0], vX[1], swapMask);
                vX[1] = IntrinsicUtils._mmw_blendv_ps(vX[1], vX[2], swapMask);
                vX[2] = sX;

                v128 sY = IntrinsicUtils._mmw_blendv_ps(vY[2], vY[0], swapMask);
                vY[0] = IntrinsicUtils._mmw_blendv_ps(vY[0], vY[1], swapMask);
                vY[1] = IntrinsicUtils._mmw_blendv_ps(vY[1], vY[2], swapMask);
                vY[2] = sY;
            }
        }

#else

        static void SortVertices(v128* vX, v128* vY)
        {
            // Rotate the triangle in the winding order until v0 is the vertex with lowest Y value
            for (int i = 0; i < 2; i++)
            {
                v128 ey1 = X86.Sse.sub_ps(vY[1], vY[0]);
                v128 ey2 = X86.Sse.sub_ps(vY[2], vY[0]);
                v128 swapMask = X86.Sse.or_ps(X86.Sse.or_ps(ey1, ey2), X86.Sse2.cmpeq_epi32(ey2, X86.Sse2.setzero_si128()));

                v128 sX = IntrinsicUtils._mmw_blendv_ps(vX[2], vX[0], swapMask);
                vX[0] = IntrinsicUtils._mmw_blendv_ps(vX[0], vX[1], swapMask);
                vX[1] = IntrinsicUtils._mmw_blendv_ps(vX[1], vX[2], swapMask);
                vX[2] = sX;

                v128 sY = IntrinsicUtils._mmw_blendv_ps(vY[2], vY[0], swapMask);
                vY[0] = IntrinsicUtils._mmw_blendv_ps(vY[0], vY[1], swapMask);
                vY[1] = IntrinsicUtils._mmw_blendv_ps(vY[1], vY[2], swapMask);
                vY[2] = sY;
            }
        }

#endif

        static void ComputeDepthPlane(v128* pVtxX, v128* pVtxY, v128* pVtxZ, out v128 zPixelDx, out v128 zPixelDy)
        {
            // Setup z(x,y) = z0 + dx*x + dy*y screen space depth plane equation
            v128 x2 = X86.Sse.sub_ps(pVtxX[2], pVtxX[0]);
            v128 x1 = X86.Sse.sub_ps(pVtxX[1], pVtxX[0]);
            v128 y1 = X86.Sse.sub_ps(pVtxY[1], pVtxY[0]);
            v128 y2 = X86.Sse.sub_ps(pVtxY[2], pVtxY[0]);
            v128 z1 = X86.Sse.sub_ps(pVtxZ[1], pVtxZ[0]);
            v128 z2 = X86.Sse.sub_ps(pVtxZ[2], pVtxZ[0]);
            v128 d = X86.Sse.div_ps(X86.Sse.set1_ps(1.0f), IntrinsicUtils._mmw_fmsub_ps(x1, y2, X86.Sse.mul_ps(y1, x2)));
            zPixelDx = X86.Sse.mul_ps(IntrinsicUtils._mmw_fmsub_ps(z1, y2, X86.Sse.mul_ps(y1, z2)), d);
            zPixelDy = X86.Sse.mul_ps(IntrinsicUtils._mmw_fmsub_ps(x1, z2, X86.Sse.mul_ps(z1, x2)), d);
        }

        static void ComputeBoundingBox(v128* vX, v128* vY, ref ScissorRect scissor, out v128 bbminX, out v128 bbminY, out v128 bbmaxX, out v128 bbmaxY)
        {
            // Find Min/Max vertices
            bbminX = X86.Sse2.cvttps_epi32(X86.Sse.min_ps(vX[0], X86.Sse.min_ps(vX[1], vX[2])));
            bbminY = X86.Sse2.cvttps_epi32(X86.Sse.min_ps(vY[0], X86.Sse.min_ps(vY[1], vY[2])));
            bbmaxX = X86.Sse2.cvttps_epi32(X86.Sse.max_ps(vX[0], X86.Sse.max_ps(vX[1], vX[2])));
            bbmaxY = X86.Sse2.cvttps_epi32(X86.Sse.max_ps(vY[0], X86.Sse.max_ps(vY[1], vY[2])));

            // Clamp to tile boundaries
            bbminX = X86.Sse2.and_si128(bbminX, SIMD_PAD_W_MASK);
            bbmaxX = X86.Sse2.and_si128(X86.Sse2.add_epi32(bbmaxX, X86.Sse2.set1_epi32(TILE_WIDTH)), SIMD_PAD_W_MASK);
            bbminY = X86.Sse2.and_si128(bbminY, SIMD_PAD_H_MASK);
            bbmaxY = X86.Sse2.and_si128(X86.Sse2.add_epi32(bbmaxY, X86.Sse2.set1_epi32(TILE_HEIGHT)), SIMD_PAD_H_MASK);

            // Clip to scissor
            bbminX = IntrinsicUtils._mmw_max_epi32(bbminX, X86.Sse2.set1_epi32(scissor.mMinX));
            bbmaxX = IntrinsicUtils._mmw_min_epi32(bbmaxX, X86.Sse2.set1_epi32(scissor.mMaxX));
            bbminY = IntrinsicUtils._mmw_max_epi32(bbminY, X86.Sse2.set1_epi32(scissor.mMinY));
            bbmaxY = IntrinsicUtils._mmw_min_epi32(bbmaxY, X86.Sse2.set1_epi32(scissor.mMaxY));
        }

#if MOC_PRECISE_COVERAGE
        unsafe int RasterizeTriangleBatch(int TEST_Z, v128* ipVtxX, v128* ipVtxY, v128* pVtxX, v128* pVtxY, v128* pVtxZ, uint triMask, ScissorRect scissor)
#else
        unsafe int RasterizeTriangleBatch(int TEST_Z, v128* pVtxX, v128* pVtxY, v128* pVtxZ, uint triMask, ScissorRect scissor)
#endif
        {
            int cullResult = (int)CullingResult.VIEW_CULLED;

            //////////////////////////////////////////////////////////////////////////////
            // Compute bounding box and clamp to tile coordinates
            //////////////////////////////////////////////////////////////////////////////

            v128 bbPixelMinX;
            v128 bbPixelMinY;
            v128 bbPixelMaxX;
            v128 bbPixelMaxY;
            ComputeBoundingBox(pVtxX, pVtxY, ref scissor, out bbPixelMinX, out bbPixelMinY, out bbPixelMaxX, out bbPixelMaxY);

            // Clamp bounding box to tiles (it's already padded in computeBoundingBox)
            v128 bbTileMinX = X86.Sse2.srai_epi32(bbPixelMinX, TILE_WIDTH_SHIFT);
            v128 bbTileMinY = X86.Sse2.srai_epi32(bbPixelMinY, TILE_HEIGHT_SHIFT);
            v128 bbTileMaxX = X86.Sse2.srai_epi32(bbPixelMaxX, TILE_WIDTH_SHIFT);
            v128 bbTileMaxY = X86.Sse2.srai_epi32(bbPixelMaxY, TILE_HEIGHT_SHIFT);
            v128 bbTileSizeX = X86.Sse2.sub_epi32(bbTileMaxX, bbTileMinX);
            v128 bbTileSizeY = X86.Sse2.sub_epi32(bbTileMaxY, bbTileMinY);

            // Cull triangles with zero bounding box
            v128 bboxSign = X86.Sse2.or_si128(X86.Sse2.sub_epi32(bbTileSizeX, X86.Sse2.set1_epi32(1)), X86.Sse2.sub_epi32(bbTileSizeY, X86.Sse2.set1_epi32(1)));
            triMask &= (uint)((~X86.Sse.movemask_ps(bboxSign)) & SIMD_ALL_LANES_MASK);

            if (triMask == 0x0)
            {
                return cullResult;
            }

            if (TEST_Z == 0)
            {
                cullResult = (int)CullingResult.VISIBLE;
            }

            //////////////////////////////////////////////////////////////////////////////
            // Set up screen space depth plane
            //////////////////////////////////////////////////////////////////////////////

            v128 zPixelDx;
            v128 zPixelDy;
            ComputeDepthPlane(pVtxX, pVtxY, pVtxZ, out zPixelDx, out zPixelDy);

            // Compute z value at min corner of bounding box. Offset to make sure z is conservative for all 8x4 subtiles
            v128 bbMinXV0 = X86.Sse.sub_ps(X86.Sse2.cvtepi32_ps(bbPixelMinX), pVtxX[0]);
            v128 bbMinYV0 = X86.Sse.sub_ps(X86.Sse2.cvtepi32_ps(bbPixelMinY), pVtxY[0]);
            v128 zPlaneOffset = IntrinsicUtils._mmw_fmadd_ps(zPixelDx, bbMinXV0, IntrinsicUtils._mmw_fmadd_ps(zPixelDy, bbMinYV0, pVtxZ[0]));
            v128 zTileDx = X86.Sse.mul_ps(zPixelDx, X86.Sse.set1_ps((float)TILE_WIDTH));
            v128 zTileDy = X86.Sse.mul_ps(zPixelDy, X86.Sse.set1_ps((float)TILE_HEIGHT));

            if (TEST_Z != 0)
            {
                zPlaneOffset = X86.Sse.add_ps(zPlaneOffset, X86.Sse.max_ps(X86.Sse2.setzero_si128(), X86.Sse.mul_ps(zPixelDx, X86.Sse.set1_ps(SUB_TILE_WIDTH))));
                zPlaneOffset = X86.Sse.add_ps(zPlaneOffset, X86.Sse.max_ps(X86.Sse2.setzero_si128(), X86.Sse.mul_ps(zPixelDy, X86.Sse.set1_ps(SUB_TILE_HEIGHT))));
            }
            else
            {
                zPlaneOffset = X86.Sse.add_ps(zPlaneOffset, X86.Sse.min_ps(X86.Sse2.setzero_si128(), X86.Sse.mul_ps(zPixelDx, X86.Sse.set1_ps(SUB_TILE_WIDTH))));
                zPlaneOffset = X86.Sse.add_ps(zPlaneOffset, X86.Sse.min_ps(X86.Sse2.setzero_si128(), X86.Sse.mul_ps(zPixelDy, X86.Sse.set1_ps(SUB_TILE_HEIGHT))));
            }

            // Compute Zmin and Zmax for the triangle (used to narrow the range for difficult tiles)
            v128 zMin = X86.Sse.min_ps(pVtxZ[0], X86.Sse.min_ps(pVtxZ[1], pVtxZ[2]));
            v128 zMax = X86.Sse.max_ps(pVtxZ[0], X86.Sse.max_ps(pVtxZ[1], pVtxZ[2]));

            //////////////////////////////////////////////////////////////////////////////
            // Sort vertices (v0 has lowest Y, and the rest is in winding order) and
            // compute edges. Also find the middle vertex and compute tile
            //////////////////////////////////////////////////////////////////////////////

#if MOC_PRECISE_COVERAGE

            // Rotate the triangle in the winding order until v0 is the vertex with lowest Y value
            SortVertices(ipVtxX, ipVtxY);

            // Compute edges
            v128* edgeX = stackalloc v128[3];
            edgeX[0] = X86.Sse2.sub_epi32(ipVtxX[1], ipVtxX[0]);
            edgeX[1] = X86.Sse2.sub_epi32(ipVtxX[2], ipVtxX[1]);
            edgeX[2] = X86.Sse2.sub_epi32(ipVtxX[2], ipVtxX[0]);

            v128* edgeY = stackalloc v128[3];
            edgeY[0] = X86.Sse2.sub_epi32(ipVtxY[1], ipVtxY[0]);
            edgeY[1] = X86.Sse2.sub_epi32(ipVtxY[2], ipVtxY[1]);
            edgeY[2] = X86.Sse2.sub_epi32(ipVtxY[2], ipVtxY[0]);

            // Classify if the middle vertex is on the left or right and compute its position
            int midVtxRight = ~X86.Sse.movemask_ps(edgeY[1]);
            v128 midPixelX = IntrinsicUtils._mmw_blendv_ps(ipVtxX[1], ipVtxX[2], edgeY[1]);
            v128 midPixelY = IntrinsicUtils._mmw_blendv_ps(ipVtxY[1], ipVtxY[2], edgeY[1]);
            v128 midTileY = X86.Sse2.srai_epi32(IntrinsicUtils._mmw_max_epi32(midPixelY, X86.Sse2.setzero_si128()), TILE_HEIGHT_SHIFT + FP_BITS);
            v128 bbMidTileY = IntrinsicUtils._mmw_max_epi32(bbTileMinY, IntrinsicUtils._mmw_min_epi32(bbTileMaxY, midTileY));

            // Compute edge events for the bottom of the bounding box, or for the middle tile in case of
            // the edge originating from the middle vertex.
            v128* xDiffi = stackalloc v128[2];
            xDiffi[0] = X86.Sse2.sub_epi32(ipVtxX[0], X86.Sse2.slli_epi32(bbPixelMinX, FP_BITS));
            xDiffi[1] = X86.Sse2.sub_epi32(midPixelX, X86.Sse2.slli_epi32(bbPixelMinX, FP_BITS));

            v128* yDiffi = stackalloc v128[2];
            yDiffi[0] = X86.Sse2.sub_epi32(ipVtxY[0], X86.Sse2.slli_epi32(bbPixelMinY, FP_BITS));
            yDiffi[1] = X86.Sse2.sub_epi32(midPixelY, X86.Sse2.slli_epi32(bbMidTileY, FP_BITS + TILE_HEIGHT_SHIFT));

            //////////////////////////////////////////////////////////////////////////////
            // Edge slope setup - Note we do not conform to DX/GL rasterization rules
            //////////////////////////////////////////////////////////////////////////////

            // Potentially flip edge to ensure that all edges have positive Y slope.
            edgeX[1] = IntrinsicUtils._mmw_blendv_ps(edgeX[1], IntrinsicUtils._mmw_neg_epi32(edgeX[1]), edgeY[1]);
            edgeY[1] = IntrinsicUtils._mmw_abs_epi32(edgeY[1]);

            // Compute floating point slopes
            v128* slope = stackalloc v128[3];
            slope[0] = X86.Sse.div_ps(X86.Sse2.cvtepi32_ps(edgeX[0]), X86.Sse2.cvtepi32_ps(edgeY[0]));
            slope[1] = X86.Sse.div_ps(X86.Sse2.cvtepi32_ps(edgeX[1]), X86.Sse2.cvtepi32_ps(edgeY[1]));
            slope[2] = X86.Sse.div_ps(X86.Sse2.cvtepi32_ps(edgeX[2]), X86.Sse2.cvtepi32_ps(edgeY[2]));

            // Modify slope of horizontal edges to make sure they mask out pixels above/below the edge. The slope is set to screen
            // width to mask out all pixels above or below the horizontal edge. We must also add a small bias to acount for that
            // vertices may end up off screen due to clipping. We're assuming that the round off error is no bigger than 1.0
            v128 horizontalSlopeDelta = X86.Sse.set1_ps(2f * ((float)mWidth + 2f * (GUARD_BAND_PIXEL_SIZE + 1.0f)));
            v128 horizontalSlope0 = X86.Sse2.cmpeq_epi32(edgeY[0], X86.Sse2.setzero_si128());
            v128 horizontalSlope1 = X86.Sse2.cmpeq_epi32(edgeY[1], X86.Sse2.setzero_si128());
            slope[0] = IntrinsicUtils._mmw_blendv_ps(slope[0], horizontalSlopeDelta, horizontalSlope0);
            slope[1] = IntrinsicUtils._mmw_blendv_ps(slope[1], IntrinsicUtils._mmw_neg_ps(horizontalSlopeDelta), horizontalSlope1);

            v128* vy = stackalloc v128[3];
            vy[0] = yDiffi[0];
            vy[1] = yDiffi[1];
            vy[2] = yDiffi[0];

            v128 offset0 = X86.Sse2.and_si128(X86.Sse2.add_epi32(yDiffi[0], X86.Sse2.set1_epi32(FP_HALF_PIXEL - 1)), X86.Sse2.set1_epi32((-1 << FP_BITS)));
            v128 offset1 = X86.Sse2.and_si128(X86.Sse2.add_epi32(yDiffi[1], X86.Sse2.set1_epi32(FP_HALF_PIXEL - 1)), X86.Sse2.set1_epi32((-1 << FP_BITS)));
            vy[0] = IntrinsicUtils._mmw_blendv_ps(yDiffi[0], offset0, horizontalSlope0);
            vy[1] = IntrinsicUtils._mmw_blendv_ps(yDiffi[1], offset1, horizontalSlope1);

            // Compute edge events for the bottom of the bounding box, or for the middle tile in case of
            // the edge originating from the middle vertex.
            v128* slopeSign = stackalloc v128[3];
            v128* absEdgeX = stackalloc v128[3];
            v128* slopeTileDelta = stackalloc v128[3];
            v128* eventStartRemainder = stackalloc v128[3];
            v128* slopeTileRemainder = stackalloc v128[3];
            v128* eventStart = stackalloc v128[3];

            for (int i = 0; i < 3; i++)
            {
                // Common, compute slope sign (used to propagate the remainder term when overflowing) is postive or negative x-direction
                slopeSign[i] = IntrinsicUtils._mmw_blendv_ps(X86.Sse2.set1_epi32(1), X86.Sse2.set1_epi32(-1), edgeX[i]);
                absEdgeX[i] = IntrinsicUtils._mmw_abs_epi32(edgeX[i]);

                // Delta and error term for one vertical tile step. The exact delta is exactDelta = edgeX / edgeY, due to limited precision we
                // repersent the delta as delta = qoutient + remainder / edgeY, where quotient = int(edgeX / edgeY). In this case, since we step
                // one tile of scanlines at a time, the slope is computed for a tile-sized step.
                slopeTileDelta[i] = X86.Sse2.cvttps_epi32(X86.Sse.mul_ps(slope[i], X86.Sse.set1_ps(FP_TILE_HEIGHT)));
                slopeTileRemainder[i] = X86.Sse2.sub_epi32(X86.Sse2.slli_epi32(absEdgeX[i], FP_TILE_HEIGHT_SHIFT), IntrinsicUtils._mmw_mullo_epi32(IntrinsicUtils._mmw_abs_epi32(slopeTileDelta[i]), edgeY[i]));

                // Jump to bottom scanline of tile row, this is the bottom of the bounding box, or the middle vertex of the triangle.
                // The jump can be in both positive and negative y-direction due to clipping / offscreen vertices.
                v128 tileStartDir = IntrinsicUtils._mmw_blendv_ps(slopeSign[i], IntrinsicUtils._mmw_neg_epi32(slopeSign[i]), vy[i]);
                v128 tieBreaker = IntrinsicUtils._mmw_blendv_ps(X86.Sse2.set1_epi32(0), X86.Sse2.set1_epi32(1), tileStartDir);
                v128 tileStartSlope = X86.Sse2.cvttps_epi32(X86.Sse.mul_ps(slope[i], X86.Sse2.cvtepi32_ps(IntrinsicUtils._mmw_neg_epi32(vy[i]))));
                v128 tileStartRemainder = X86.Sse2.sub_epi32(IntrinsicUtils._mmw_mullo_epi32(absEdgeX[i], IntrinsicUtils._mmw_abs_epi32(vy[i])), IntrinsicUtils._mmw_mullo_epi32(IntrinsicUtils._mmw_abs_epi32(tileStartSlope), edgeY[i]));

                eventStartRemainder[i] = X86.Sse2.sub_epi32(tileStartRemainder, tieBreaker);
                v128 overflow = X86.Sse2.srai_epi32(eventStartRemainder[i], 31);
                eventStartRemainder[i] = X86.Sse2.add_epi32(eventStartRemainder[i], X86.Sse2.and_si128(overflow, edgeY[i]));
                eventStartRemainder[i] = IntrinsicUtils._mmw_blendv_ps(eventStartRemainder[i], X86.Sse2.sub_epi32(X86.Sse2.sub_epi32(edgeY[i], eventStartRemainder[i]), X86.Sse2.set1_epi32(1)), vy[i]);

                //eventStart[i] = xDiffi[i & 1] + tileStartSlope + (overflow & tileStartDir) + X86.Sse2.set1_epi32(FP_HALF_PIXEL - 1) + tieBreaker;
                eventStart[i] = X86.Sse2.add_epi32(X86.Sse2.add_epi32(xDiffi[i & 1], tileStartSlope), X86.Sse2.and_si128(overflow, tileStartDir));
                eventStart[i] = X86.Sse2.add_epi32(X86.Sse2.add_epi32(eventStart[i], X86.Sse2.set1_epi32(FP_HALF_PIXEL - 1)), tieBreaker);
            }

#else // PRECISE_COVERAGE

            SortVertices(pVtxX, pVtxY);

            // Compute edges
            v128* edgeX = stackalloc v128[3];
            edgeX[0] = X86.Sse.sub_ps(pVtxX[1], pVtxX[0]);
            edgeX[1] = X86.Sse.sub_ps(pVtxX[2], pVtxX[1]);
            edgeX[2] = X86.Sse.sub_ps(pVtxX[2], pVtxX[0]);

            v128* edgeY = stackalloc v128[3];
            edgeY[0] = X86.Sse.sub_ps(pVtxY[1], pVtxY[0]);
            edgeY[1] = X86.Sse.sub_ps(pVtxY[2], pVtxY[1]);
            edgeY[2] = X86.Sse.sub_ps(pVtxY[2], pVtxY[0]);

            // Classify if the middle vertex is on the left or right and compute its position
            int midVtxRight = ~X86.Sse.movemask_ps(edgeY[1]);
            v128 midPixelX = IntrinsicUtils._mmw_blendv_ps(pVtxX[1], pVtxX[2], edgeY[1]);
            v128 midPixelY = IntrinsicUtils._mmw_blendv_ps(pVtxY[1], pVtxY[2], edgeY[1]);
            v128 midTileY = X86.Sse2.srai_epi32(IntrinsicUtils._mmw_max_epi32(X86.Sse2.cvttps_epi32(midPixelY), SIMD_BITS_ZERO), TILE_HEIGHT_SHIFT);
            v128 bbMidTileY = IntrinsicUtils._mmw_max_epi32(bbTileMinY, IntrinsicUtils._mmw_min_epi32(bbTileMaxY, midTileY));

            //////////////////////////////////////////////////////////////////////////////
            // Edge slope setup - Note we do not conform to DX/GL rasterization rules
            //////////////////////////////////////////////////////////////////////////////

            // Compute floating point slopes
            v128* slope = stackalloc v128[3];
            slope[0] = X86.Sse.div_ps(edgeX[0], edgeY[0]);
            slope[1] = X86.Sse.div_ps(edgeX[1], edgeY[1]);
            slope[2] = X86.Sse.div_ps(edgeX[2], edgeY[2]);

            // Modify slope of horizontal edges to make sure they mask out pixels above/below the edge. The slope is set to screen
            // width to mask out all pixels above or below the horizontal edge. We must also add a small bias to acount for that
            // vertices may end up off screen due to clipping. We're assuming that the round off error is no bigger than 1.0
            v128 horizontalSlopeDelta = X86.Sse.set1_ps((float)mWidth + 2.0f*(GUARD_BAND_PIXEL_SIZE + 1.0f));
            slope[0] = IntrinsicUtils._mmw_blendv_ps(slope[0], horizontalSlopeDelta, X86.Sse.cmpeq_ps(edgeY[0], X86.Sse2.setzero_si128()));
            slope[1] = IntrinsicUtils._mmw_blendv_ps(slope[1], IntrinsicUtils._mmw_neg_ps(horizontalSlopeDelta), X86.Sse.cmpeq_ps(edgeY[1], X86.Sse2.setzero_si128()));

            // Convert floaing point slopes to fixed point
            v128* slopeFP = stackalloc v128[3];
            slopeFP[0] = X86.Sse2.cvttps_epi32(X86.Sse.mul_ps(slope[0], X86.Sse.set1_ps(1 << FP_BITS)));
            slopeFP[1] = X86.Sse2.cvttps_epi32(X86.Sse.mul_ps(slope[1], X86.Sse.set1_ps(1 << FP_BITS)));
            slopeFP[2] = X86.Sse2.cvttps_epi32(X86.Sse.mul_ps(slope[2], X86.Sse.set1_ps(1 << FP_BITS)));

            // Fan out edge slopes to avoid (rare) cracks at vertices. We increase right facing slopes
            // by 1 LSB, which results in overshooting vertices slightly, increasing triangle coverage.
            // e0 is always right facing, e1 depends on if the middle vertex is on the left or right
            slopeFP[0] = X86.Sse2.add_epi32(slopeFP[0], X86.Sse2.set1_epi32(1));
            slopeFP[1] = X86.Sse2.add_epi32(slopeFP[1], X86.Sse2.srli_epi32(IntrinsicUtils._mmw_not_epi32(edgeY[1]), 31));

            // Compute slope deltas for an SIMD_LANES scanline step (tile height)
            v128* slopeTileDelta = stackalloc v128[3];
            slopeTileDelta[0] = X86.Sse2.slli_epi32(slopeFP[0], TILE_HEIGHT_SHIFT);
            slopeTileDelta[1] = X86.Sse2.slli_epi32(slopeFP[1], TILE_HEIGHT_SHIFT);
            slopeTileDelta[2] = X86.Sse2.slli_epi32(slopeFP[2], TILE_HEIGHT_SHIFT);

            // Compute edge events for the bottom of the bounding box, or for the middle tile in case of
            // the edge originating from the middle vertex.
            v128* xDiffi = stackalloc v128[2];
            xDiffi[0] = X86.Sse2.slli_epi32(X86.Sse2.sub_epi32(X86.Sse2.cvttps_epi32(pVtxX[0]), bbPixelMinX), FP_BITS);
            xDiffi[1] = X86.Sse2.slli_epi32(X86.Sse2.sub_epi32(X86.Sse2.cvttps_epi32(midPixelX), bbPixelMinX), FP_BITS);

            v128* yDiffi = stackalloc v128[2];
            yDiffi[0] = X86.Sse2.sub_epi32(X86.Sse2.cvttps_epi32(pVtxY[0]), bbPixelMinY);
            yDiffi[1] = X86.Sse2.sub_epi32(X86.Sse2.cvttps_epi32(midPixelY), X86.Sse2.slli_epi32(bbMidTileY, TILE_HEIGHT_SHIFT));

            v128* eventStart = stackalloc v128[3];
            eventStart[0] = X86.Sse2.sub_epi32(xDiffi[0], IntrinsicUtils._mmw_mullo_epi32(slopeFP[0], yDiffi[0]));
            eventStart[1] = X86.Sse2.sub_epi32(xDiffi[1], IntrinsicUtils._mmw_mullo_epi32(slopeFP[1], yDiffi[1]));
            eventStart[2] = X86.Sse2.sub_epi32(xDiffi[0], IntrinsicUtils._mmw_mullo_epi32(slopeFP[2], yDiffi[0]));

#endif

            //////////////////////////////////////////////////////////////////////////////
            // Split bounding box into bottom - middle - top region.
            //////////////////////////////////////////////////////////////////////////////

            v128 bbBottomIdx = X86.Sse2.add_epi32(bbTileMinX, IntrinsicUtils._mmw_mullo_epi32(bbTileMinY, X86.Sse2.set1_epi32(mTilesWidth)));
            v128 bbTopIdx = X86.Sse2.add_epi32(bbTileMinX, IntrinsicUtils._mmw_mullo_epi32(X86.Sse2.add_epi32(bbTileMinY, bbTileSizeY), X86.Sse2.set1_epi32(mTilesWidth)));
            v128 bbMidIdx = X86.Sse2.add_epi32(bbTileMinX, IntrinsicUtils._mmw_mullo_epi32(midTileY, X86.Sse2.set1_epi32(mTilesWidth)));

            //////////////////////////////////////////////////////////////////////////////
            // Loop over non-culled triangle and change SIMD axis to per-pixel
            //////////////////////////////////////////////////////////////////////////////
            while (triMask != 0)
            {
                uint triIdx = (uint)IntrinsicUtils.find_clear_lsb(ref triMask);
                int triMidVtxRight = (midVtxRight >> (int)triIdx) & 1;

                // Get Triangle Zmin zMax
                v128 zTriMax = X86.Sse.set1_ps(IntrinsicUtils.getFloatLane(zMax, triIdx));
                v128 zTriMin = X86.Sse.set1_ps(IntrinsicUtils.getFloatLane(zMin, triIdx));

                // Setup Zmin value for first set of 8x4 subtiles
                v128 z0 = IntrinsicUtils._mmw_fmadd_ps(X86.Sse.set1_ps(IntrinsicUtils.getFloatLane(zPixelDx, triIdx)),
                                                       SIMD_SUB_TILE_COL_OFFSET_F,
                                                       IntrinsicUtils._mmw_fmadd_ps(X86.Sse.set1_ps(IntrinsicUtils.getFloatLane(zPixelDy, triIdx)),
                                                                                    SIMD_SUB_TILE_ROW_OFFSET_F,
                                                                                    X86.Sse.set1_ps(IntrinsicUtils.getFloatLane(zPlaneOffset, triIdx))));

                float zx = IntrinsicUtils.getFloatLane(zTileDx, triIdx);
                float zy = IntrinsicUtils.getFloatLane(zTileDy, triIdx);

                // Get dimension of bounding box bottom, mid & top segments
                int bbWidth = IntrinsicUtils.getIntLane(bbTileSizeX, triIdx);
                int bbHeight = IntrinsicUtils.getIntLane(bbTileSizeY, triIdx);
                int tileRowIdx = IntrinsicUtils.getIntLane(bbBottomIdx, triIdx);
                int tileMidRowIdx = IntrinsicUtils.getIntLane(bbMidIdx, triIdx);
                int tileEndRowIdx = IntrinsicUtils.getIntLane(bbTopIdx, triIdx);

                if (bbWidth > BIG_TRIANGLE && bbHeight > BIG_TRIANGLE) // For big triangles we use a more expensive but tighter traversal algorithm
                {
#if MOC_PRECISE_COVERAGE
                    if (triMidVtxRight != 0)
                    {
                        cullResult &= RasterizeTriangle(TEST_Z, 1, 1, triIdx, bbWidth, tileRowIdx, tileMidRowIdx, tileEndRowIdx, eventStart, slope, slopeTileDelta, zTriMin, zTriMax, ref z0, zx, zy, edgeY, absEdgeX, slopeSign, eventStartRemainder, slopeTileRemainder);
                    }
                    else
                    {
                        cullResult &= RasterizeTriangle(TEST_Z, 1, 0, triIdx, bbWidth, tileRowIdx, tileMidRowIdx, tileEndRowIdx, eventStart, slope, slopeTileDelta, zTriMin, zTriMax, ref z0, zx, zy, edgeY, absEdgeX, slopeSign, eventStartRemainder, slopeTileRemainder);
                    }
#else
                    if (triMidVtxRight != 0)
                    {
                        cullResult &= RasterizeTriangle(TEST_Z, 1, 1, triIdx, bbWidth, tileRowIdx, tileMidRowIdx, tileEndRowIdx, eventStart, slopeFP, slopeTileDelta, zTriMin, zTriMax, ref z0, zx, zy);
                    }
                    else
                    {
                        cullResult &= RasterizeTriangle(TEST_Z, 1, 0, triIdx, bbWidth, tileRowIdx, tileMidRowIdx, tileEndRowIdx, eventStart, slopeFP, slopeTileDelta, zTriMin, zTriMax, ref z0, zx, zy);
                    }
#endif
                }
                else
                {
#if MOC_PRECISE_COVERAGE
                    if (triMidVtxRight != 0)
                    {
                        cullResult &= RasterizeTriangle(TEST_Z, 0, 1, triIdx, bbWidth, tileRowIdx, tileMidRowIdx, tileEndRowIdx, eventStart, slope, slopeTileDelta, zTriMin, zTriMax, ref z0, zx, zy, edgeY, absEdgeX, slopeSign, eventStartRemainder, slopeTileRemainder);
                    }
                    else
                    {
                        cullResult &= RasterizeTriangle(TEST_Z, 0, 0, triIdx, bbWidth, tileRowIdx, tileMidRowIdx, tileEndRowIdx, eventStart, slope, slopeTileDelta, zTriMin, zTriMax, ref z0, zx, zy, edgeY, absEdgeX, slopeSign, eventStartRemainder, slopeTileRemainder);
                    }
#else
                    if (triMidVtxRight != 0)
                    {
                        cullResult &= RasterizeTriangle(TEST_Z, 0, 1, triIdx, bbWidth, tileRowIdx, tileMidRowIdx, tileEndRowIdx, eventStart, slopeFP, slopeTileDelta, zTriMin, zTriMax, ref z0, zx, zy);
                    }
                    else
                    {
                        cullResult &= RasterizeTriangle(TEST_Z, 0, 0, triIdx, bbWidth, tileRowIdx, tileMidRowIdx, tileEndRowIdx, eventStart, slopeFP, slopeTileDelta, zTriMin, zTriMax, ref z0, zx, zy);
                    }
#endif
                }

                if (TEST_Z != 0 && cullResult == (int)CullingResult.VISIBLE)
                {
                    cullResult = (int)CullingResult.VISIBLE;
                    break;
                }
            }

            return cullResult;
        }

        ////// CullBackfaces //////

#if MOC_PRECISE_COVERAGE

        static int CullBackfaces(v128* ipVtxX, v128* ipVtxY, v128* pVtxX, v128* pVtxY, v128* pVtxZ, v128 ccwMask, BackfaceWinding bfWinding)
        {
            // Reverse vertex order if non cw faces are considered front facing (rasterizer code requires CCW order)
            if (((uint)(bfWinding & BackfaceWinding.BACKFACE_CW)) == 0)
            {
                v128 itmpX = IntrinsicUtils._mmw_blendv_ps(ipVtxX[2], ipVtxX[0], ccwMask);
                v128 itmpY = IntrinsicUtils._mmw_blendv_ps(ipVtxY[2], ipVtxY[0], ccwMask);

                v128 tmpX = IntrinsicUtils._mmw_blendv_ps(pVtxX[2], pVtxX[0], ccwMask);
                v128 tmpY = IntrinsicUtils._mmw_blendv_ps(pVtxY[2], pVtxY[0], ccwMask);
                v128 tmpZ = IntrinsicUtils._mmw_blendv_ps(pVtxZ[2], pVtxZ[0], ccwMask);

                ipVtxX[2] = IntrinsicUtils._mmw_blendv_ps(ipVtxX[0], ipVtxX[2], ccwMask);
                ipVtxY[2] = IntrinsicUtils._mmw_blendv_ps(ipVtxY[0], ipVtxY[2], ccwMask);

                pVtxX[2] = IntrinsicUtils._mmw_blendv_ps(pVtxX[0], pVtxX[2], ccwMask);
                pVtxY[2] = IntrinsicUtils._mmw_blendv_ps(pVtxY[0], pVtxY[2], ccwMask);
                pVtxZ[2] = IntrinsicUtils._mmw_blendv_ps(pVtxZ[0], pVtxZ[2], ccwMask);

                ipVtxX[0] = itmpX;
                ipVtxY[0] = itmpY;

                pVtxX[0] = tmpX;
                pVtxY[0] = tmpY;
                pVtxZ[0] = tmpZ;
            }

            // Return a lane mask with all front faces set
            return (((uint)bfWinding & (uint)BackfaceWinding.BACKFACE_CCW) != 0 ? 0 : X86.Sse.movemask_ps(ccwMask))
                 | (((uint)bfWinding & (uint)BackfaceWinding.BACKFACE_CW) != 0 ? 0 : ~X86.Sse.movemask_ps(ccwMask));
        }

#else

        static int CullBackfaces(v128* pVtxX, v128* pVtxY, v128* pVtxZ, v128 ccwMask, BackfaceWinding bfWinding)
        {
            // Reverse vertex order if non cw faces are considered front facing (rasterizer code requires CCW order)
            if (((uint)(bfWinding & BackfaceWinding.BACKFACE_CW)) == 0)
            {
                v128 tmpX = IntrinsicUtils._mmw_blendv_ps(pVtxX[2], pVtxX[0], ccwMask);
                v128 tmpY = IntrinsicUtils._mmw_blendv_ps(pVtxY[2], pVtxY[0], ccwMask);
                v128 tmpZ = IntrinsicUtils._mmw_blendv_ps(pVtxZ[2], pVtxZ[0], ccwMask);

                pVtxX[2] = IntrinsicUtils._mmw_blendv_ps(pVtxX[0], pVtxX[2], ccwMask);
                pVtxY[2] = IntrinsicUtils._mmw_blendv_ps(pVtxY[0], pVtxY[2], ccwMask);
                pVtxZ[2] = IntrinsicUtils._mmw_blendv_ps(pVtxZ[0], pVtxZ[2], ccwMask);

                pVtxX[0] = tmpX;
                pVtxY[0] = tmpY;
                pVtxZ[0] = tmpZ;
            }

            // Return a lane mask with all front faces set
            return (((uint)bfWinding & (uint)BackfaceWinding.BACKFACE_CCW) != 0 ? 0 :  X86.Sse.movemask_ps(ccwMask))
                 | (((uint)bfWinding & (uint)BackfaceWinding.BACKFACE_CW)  != 0 ? 0 : ~X86.Sse.movemask_ps(ccwMask));
        }

#endif

        ////// ProjectVertices //////

#if MOC_PRECISE_COVERAGE

        unsafe void ProjectVertices(v128* ipVtxX, v128* ipVtxY, v128* pVtxX, v128* pVtxY, v128* pVtxZ, v128* vtxX, v128* vtxY, v128* vtxW)
        {
            // Project vertices and transform to screen space. Snap to sub-pixel coordinates with FP_BITS precision.
            for (int i = 0; i < 3; i++)
            {
                int idx = vertexOrder[i];
                v128 rcpW = X86.Sse.div_ps(X86.Sse.set1_ps(1f), vtxW[i]);

                v128 screenX = IntrinsicUtils._mmw_fmadd_ps(X86.Sse.mul_ps(vtxX[i], mHalfWidth), rcpW, mCenterX);
                v128 screenY = IntrinsicUtils._mmw_fmadd_ps(X86.Sse.mul_ps(vtxY[i], mHalfHeight), rcpW, mCenterY);

                ipVtxX[idx] = X86.Sse2.cvtps_epi32(X86.Sse.mul_ps(screenX, X86.Sse.set1_ps((float)(1 << FP_BITS))));
                ipVtxY[idx] = X86.Sse2.cvtps_epi32(X86.Sse.mul_ps(screenY, X86.Sse.set1_ps((float)(1 << FP_BITS))));

                pVtxX[idx] = X86.Sse.mul_ps(X86.Sse2.cvtepi32_ps(ipVtxX[idx]), X86.Sse.set1_ps(FP_INV));
                pVtxY[idx] = X86.Sse.mul_ps(X86.Sse2.cvtepi32_ps(ipVtxY[idx]), X86.Sse.set1_ps(FP_INV));
                pVtxZ[idx] = rcpW;
            }
        }

#else

        unsafe void ProjectVertices(v128* pVtxX, v128* pVtxY, v128* pVtxZ, v128* vtxX, v128* vtxY, v128* vtxW)
        {
            // Project vertices and transform to screen space. Round to nearest integer pixel coordinate
            for (int i = 0; i < 3; i++)
            {
                int idx = vertexOrder[i];
                v128 rcpW = X86.Sse.div_ps(X86.Sse.set1_ps(1.0f), vtxW[i]);

                // The rounding modes are set to match HW rasterization with OpenGL. In practice our samples are placed
                // in the (1,0) corner of each pixel, while HW rasterizer uses (0.5, 0.5). We get (1,0) because of the
                // floor used when interpolating along triangle edges. The rounding modes match an offset of (0.5, -0.5)
                pVtxX[idx] = X86.Sse4_1.ceil_ps(IntrinsicUtils._mmw_fmadd_ps(X86.Sse.mul_ps(vtxX[i], mHalfWidth), rcpW, mCenterX));
                pVtxY[idx] = X86.Sse4_1.floor_ps(IntrinsicUtils._mmw_fmadd_ps(X86.Sse.mul_ps(vtxY[i], mHalfHeight), rcpW, mCenterY));
                pVtxZ[idx] = rcpW;
            }
        }

#endif

        ////// GatherTransformClip //////

        static unsafe int ClipPolygon(v128* outVtx, v128* inVtx, v128 plane, int n)
        {
            v128 p0 = inVtx[n - 1];
            v128 dist0 = IntrinsicUtils._mmx_dp4_ps(p0, plane);

            // Loop over all polygon edges and compute intersection with clip plane (if any)
            int nout = 0;

            for (int k = 0; k < n; k++)
            {
                v128 p1 = inVtx[k];
                v128 dist1 = IntrinsicUtils._mmx_dp4_ps(p1, plane);
                int dist0Neg = X86.Sse.movemask_ps(dist0);

                if (dist0Neg == 0)  // dist0 > 0.0f
                {
                    outVtx[nout++] = p0;
                }

                // Edge intersects the clip plane if dist0 and dist1 have opposing signs
                if (X86.Sse.movemask_ps(X86.Sse.xor_ps(dist0, dist1)) > 0)
                {
                    // Always clip from the positive side to avoid T-junctions
                    if (dist0Neg == 0)
                    {
                        v128 t = X86.Sse.div_ps(dist0, X86.Sse.sub_ps(dist0, dist1));
                        outVtx[nout++] = IntrinsicUtils._mmw_fmadd_ps(X86.Sse.sub_ps(p1, p0), t, p0);
                    }
                    else
                    {
                        v128 t = X86.Sse.div_ps(dist1, X86.Sse.sub_ps(dist1, dist0));
                        outVtx[nout++] = IntrinsicUtils._mmw_fmadd_ps(X86.Sse.sub_ps(p0, p1), t, p1);
                    }
                }

                dist0 = dist1;
                p0 = p1;
            }

            return nout;
        }

        void TestClipPlane(ClipPlanes CLIP_PLANE, v128* vtxX, v128* vtxY, v128* vtxW, out uint straddleMask, ref uint triMask, ClipPlanes clipPlaneMask)
        {
            straddleMask = 0;

            // Skip masked clip planes
            if (((uint)clipPlaneMask & (uint)CLIP_PLANE) == 0)
            {
                return;
            }

            // Evaluate all 3 vertices against the frustum plane
            v128* planeDp = stackalloc v128[3];

            for (int i = 0; i < 3; ++i)
            {
                switch (CLIP_PLANE)
                {
                    case ClipPlanes.CLIP_PLANE_LEFT: planeDp[i] = X86.Sse.add_ps(vtxW[i], vtxX[i]); break;
                    case ClipPlanes.CLIP_PLANE_RIGHT: planeDp[i] = X86.Sse.sub_ps(vtxW[i], vtxX[i]); break;
                    case ClipPlanes.CLIP_PLANE_BOTTOM: planeDp[i] = X86.Sse.add_ps(vtxW[i], vtxY[i]); break;
                    case ClipPlanes.CLIP_PLANE_TOP: planeDp[i] = X86.Sse.sub_ps(vtxW[i], vtxY[i]); break;
                    case ClipPlanes.CLIP_PLANE_NEAR: planeDp[i] = X86.Sse.sub_ps(vtxW[i], X86.Sse.set1_ps(mNearDist)); break;
                }
            }

            // Look at FP sign and determine if tri is inside, outside or straddles the frustum plane
            v128 inside = X86.Sse.andnot_ps(planeDp[0], X86.Sse.andnot_ps(planeDp[1], X86.Sse.xor_ps(planeDp[2], X86.Sse2.set1_epi32(~0))));
            v128 outside = X86.Sse.and_ps(planeDp[0], X86.Sse.and_ps(planeDp[1], planeDp[2]));
            uint inMask = (uint)X86.Sse.movemask_ps(inside);
            uint outMask = (uint)X86.Sse.movemask_ps(outside);
            straddleMask = (~outMask) & (~inMask);
            triMask &= ~outMask;
        }


        unsafe void ClipTriangleAndAddToBuffer(v128* vtxX, v128* vtxY, v128* vtxW, v128* clippedTrisBuffer, ref int clipWriteIdx, ref uint triMask, uint triClipMask, ClipPlanes clipPlaneMask)
        {
            if (triClipMask == 0)
            {
                return;
            }

            // Inside test all 3 triangle vertices against all active frustum planes
            uint* straddleMask = stackalloc uint[5];
            TestClipPlane(ClipPlanes.CLIP_PLANE_NEAR, vtxX, vtxY, vtxW, out straddleMask[0], ref triMask, clipPlaneMask);
            TestClipPlane(ClipPlanes.CLIP_PLANE_LEFT, vtxX, vtxY, vtxW, out straddleMask[1], ref triMask, clipPlaneMask);
            TestClipPlane(ClipPlanes.CLIP_PLANE_RIGHT, vtxX, vtxY, vtxW, out straddleMask[2], ref triMask, clipPlaneMask);
            TestClipPlane(ClipPlanes.CLIP_PLANE_BOTTOM, vtxX, vtxY, vtxW, out straddleMask[3], ref triMask, clipPlaneMask);
            TestClipPlane(ClipPlanes.CLIP_PLANE_TOP, vtxX, vtxY, vtxW, out straddleMask[4], ref triMask, clipPlaneMask);

            // Clip triangle against straddling planes and add to the clipped triangle buffer

            // DS: out of jagged [][] vs. multidim [,] vs. singledim [], singledim seemed best choice,
            // but for Burst need to use NativeArray or much better stackalloc

            {

#if MOC_CLIPPING_PRESERVES_ORDER

                uint clipMask = triClipMask & triMask;
                uint clipAndStraddleMask = (straddleMask[0] | straddleMask[1] | straddleMask[2] | straddleMask[3] | straddleMask[4]) & clipMask;

                // no clipping needed after all - early out
                if (clipAndStraddleMask == 0)
                {
                    return;
                }

                const int row = 2;
                const int col = 8;
                v128* vtxBuf = stackalloc v128[row * col];

                while (clipMask > 0)
                {
                    // Find and setup next triangle to clip
                    uint triIdx = (uint)IntrinsicUtils.find_clear_lsb(ref clipMask);
                    uint triBit = 1u << (int)triIdx;
                    Debug.Assert(triIdx < SIMD_LANES);

                    int bufIdx = 0;
                    int nClippedVerts = 3;

                    for (int i = 0; i < 3; i++)
                    {
                        vtxBuf[i] = X86.Sse.setr_ps(
                            IntrinsicUtils.getFloatLane(vtxX[i], triIdx),
                            IntrinsicUtils.getFloatLane(vtxY[i], triIdx),
                            IntrinsicUtils.getFloatLane(vtxW[i], triIdx),
                            1f);
                    }

                    // Clip triangle with straddling planes.
                    for (int i = 0; i < 5; ++i)
                    {
                        if (((straddleMask[i] & triBit) != 0) && (((uint)clipPlaneMask & (1 << i)) != 0)) // <- second part maybe not needed?
                        {
                            nClippedVerts = ClipPolygon(vtxBuf + ((bufIdx ^ 1) * col), vtxBuf + (bufIdx * col), CSFrustumPlanes[i], nClippedVerts);
                            bufIdx ^= 1;
                        }
                    }

                    if (nClippedVerts >= 3)
                    {
                        // Write all triangles into the clip buffer and process them next loop iteration
                        clippedTrisBuffer[clipWriteIdx * 3 + 0] = vtxBuf[bufIdx * col + 0];
                        clippedTrisBuffer[clipWriteIdx * 3 + 1] = vtxBuf[bufIdx * col + 1];
                        clippedTrisBuffer[clipWriteIdx * 3 + 2] = vtxBuf[bufIdx * col + 2];
                        clipWriteIdx = (clipWriteIdx + 1) & (MAX_CLIPPED - 1);

                        for (int i = 2; i < nClippedVerts - 1; i++)
                        {
                            clippedTrisBuffer[clipWriteIdx * 3 + 0] = vtxBuf[bufIdx * col + 0];
                            clippedTrisBuffer[clipWriteIdx * 3 + 1] = vtxBuf[bufIdx * col + i];
                            clippedTrisBuffer[clipWriteIdx * 3 + 2] = vtxBuf[bufIdx * col + i + 1];
                            clipWriteIdx = (clipWriteIdx + 1) & (MAX_CLIPPED - 1);
                        }
                    }
                }

                // since all triangles were copied to clip buffer for next iteration, skip further processing
                triMask = 0;

#else

                const int row = 2;
                const int col = 8;
                v128* vtxBuf = stackalloc v128[row * col];

                uint clipMask = (straddleMask[0] | straddleMask[1] | straddleMask[2] | straddleMask[3] | straddleMask[4]) & (triClipMask & triMask);

                while (clipMask > 0)
                {
                    // Find and setup next triangle to clip
                    uint triIdx = (uint)IntrinsicUtils.find_clear_lsb(ref clipMask);
                    uint triBit = 1u << (int)triIdx;
                    Debug.Assert(triIdx < SIMD_LANES);

                    int bufIdx = 0;
                    int nClippedVerts = 3;

                    for (int i = 0; i < 3; i++)
                    {
                        vtxBuf[i] = X86.Sse.setr_ps(
                            IntrinsicUtils.getFloatLane(vtxX[i], triIdx),
                            IntrinsicUtils.getFloatLane(vtxY[i], triIdx),
                            IntrinsicUtils.getFloatLane(vtxW[i], triIdx),
                            1f);
                    }

                    // Clip triangle with straddling planes.
                    for (int i = 0; i < 5; ++i)
                    {
                        if (((straddleMask[i] & triBit) != 0) && (((uint)clipPlaneMask & (1 << i)) != 0))
                        {
                            nClippedVerts = ClipPolygon(vtxBuf + (bufIdx ^ 1), vtxBuf + bufIdx, CSFrustumPlanes[i], nClippedVerts);
                            bufIdx ^= 1;
                        }
                    }

                    if (nClippedVerts >= 3)
                    {
                        // Write the first triangle back into the list of currently processed triangles
                        for (int i = 0; i < 3; i++)
                        {
                            vtxX[i] = IntrinsicUtils.getCopyWithFloatLane(vtxX[i], triIdx, IntrinsicUtils.getFloatLane(vtxBuf[bufIdx * col + i], 0));
                            vtxY[i] = IntrinsicUtils.getCopyWithFloatLane(vtxY[i], triIdx, IntrinsicUtils.getFloatLane(vtxBuf[bufIdx * col + i], 1));
                            vtxW[i] = IntrinsicUtils.getCopyWithFloatLane(vtxW[i], triIdx, IntrinsicUtils.getFloatLane(vtxBuf[bufIdx * col + i], 2));
                        }

                        // Write the remaining triangles into the clip buffer and process them next loop iteration
                        for (int i = 2; i < nClippedVerts - 1; i++)
                        {
                            clippedTrisBuffer[clipWriteIdx * 3 + 0] = vtxBuf[bufIdx * col + 0];
                            clippedTrisBuffer[clipWriteIdx * 3 + 1] = vtxBuf[bufIdx * col + i];
                            clippedTrisBuffer[clipWriteIdx * 3 + 2] = vtxBuf[bufIdx * col + (i + 1)];
                            clipWriteIdx = (clipWriteIdx + 1) & (MAX_CLIPPED - 1);
                        }
                    }
                    else // Kill triangles that was removed by clipping
                    {
                        triMask &= ~triBit;
                    }
                }

#endif

            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void TransformVerts(v128* vtxX, v128* vtxY, v128* vtxW, float* modelToClipMatrix)
        {
            if (modelToClipMatrix != null)
            {
                for (int i = 0; i < 3; ++i)
                {
                    v128 tmpX = IntrinsicUtils._mmw_fmadd_ps(vtxX[i],
                                                             X86.Sse.set1_ps(modelToClipMatrix[0]),
                                                             IntrinsicUtils._mmw_fmadd_ps(vtxY[i],
                                                                                          X86.Sse.set1_ps(modelToClipMatrix[4]),
                                                                                          IntrinsicUtils._mmw_fmadd_ps(vtxW[i],
                                                                                                                       X86.Sse.set1_ps(modelToClipMatrix[8]),
                                                                                                                       X86.Sse.set1_ps(modelToClipMatrix[12]))));
                    v128 tmpY = IntrinsicUtils._mmw_fmadd_ps(vtxX[i],
                                                             X86.Sse.set1_ps(modelToClipMatrix[1]),
                                                             IntrinsicUtils._mmw_fmadd_ps(vtxY[i],
                                                                                          X86.Sse.set1_ps(modelToClipMatrix[5]),
                                                                                          IntrinsicUtils._mmw_fmadd_ps(vtxW[i],
                                                                                                                       X86.Sse.set1_ps(modelToClipMatrix[9]),
                                                                                                                       X86.Sse.set1_ps(modelToClipMatrix[13]))));
                    v128 tmpW = IntrinsicUtils._mmw_fmadd_ps(vtxX[i],
                                                             X86.Sse.set1_ps(modelToClipMatrix[3]),
                                                             IntrinsicUtils._mmw_fmadd_ps(vtxY[i],
                                                                                          X86.Sse.set1_ps(modelToClipMatrix[7]),
                                                                                          IntrinsicUtils._mmw_fmadd_ps(vtxW[i],
                                                                                                                       X86.Sse.set1_ps(modelToClipMatrix[11]),
                                                                                                                       X86.Sse.set1_ps(modelToClipMatrix[15]))));

                    vtxX[i] = tmpX;
                    vtxY[i] = tmpY;
                    vtxW[i] = tmpW;
                }
            }
        }

        // DS: used to be compile time recursion
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void VtxFetch4(int N, v128* v, uint* inTrisPtr, int triVtx, float* inVtx, int numLanes)
        {
            if (N == 0)
            {
                return;
            }

            // Fetch 4 vectors (matching 1 sse part of the SIMD register), and continue to the next
            int ssePart = (SIMD_LANES / 4) - N;

            for (int k = 0; k < 4; k++)
            {
                int lane = 4 * ssePart + k;

                if (numLanes > lane)
                {
                    uint idx = inTrisPtr[lane * 3 + triVtx] << 2;
                    v[k] = X86.Sse.loadu_ps((void*)(&inVtx[idx]));
                }
            }

            VtxFetch4(N - 1, v, inTrisPtr, triVtx, inVtx, numLanes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void GatherVerticesFast(v128* vtxX, v128* vtxY, v128* vtxW, float* inVtx, uint* inTrisPtr, int numLanes)
        {
            // This function assumes that the vertex layout is four packed x, y, z, w-values.
            // Since the layout is known we can get some additional performance by using a
            // more optimized gather strategy.
            Debug.Assert(numLanes >= 1);

            // Gather vertices
            v128* v = stackalloc v128[4];
            v128* swz = stackalloc v128[4];

            for (int i = 0; i < 3; i++)
            {
                // Load 4 (x,y,z,w) vectors per SSE part of the SIMD register (so 4 vectors for SSE, 8 vectors for AVX)
                // this fetch uses templates to unroll the loop
                VtxFetch4(SIMD_LANES / 4, v, inTrisPtr, i, inVtx, numLanes);

                // Transpose each individual SSE part of the SSE/AVX register (similar to _MM_TRANSPOSE4_PS)
                swz[0] = X86.Sse.shuffle_ps(v[0], v[1], 0x44);
                swz[2] = X86.Sse.shuffle_ps(v[0], v[1], 0xEE);
                swz[1] = X86.Sse.shuffle_ps(v[2], v[3], 0x44);
                swz[3] = X86.Sse.shuffle_ps(v[2], v[3], 0xEE);

                vtxX[i] = X86.Sse.shuffle_ps(swz[0], swz[1], 0x88);
                vtxY[i] = X86.Sse.shuffle_ps(swz[0], swz[1], 0xDD);
                vtxW[i] = X86.Sse.shuffle_ps(swz[2], swz[3], 0xDD);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void GatherVertices(v128* vtxX, v128* vtxY, v128* vtxW, float* inVtx, uint* inTrisPtr, int numLanes, VertexLayout vtxLayout)
        {
            for (uint lane = 0; lane < numLanes; lane++)
            {
                for (int i = 0; i < 3; i++)
                {
                    char* vPtrX = (char*)inVtx + inTrisPtr[lane * 3 + i] * vtxLayout.mStride;
                    char* vPtrY = vPtrX + vtxLayout.mOffsetY;
                    char* vPtrW = vPtrX + vtxLayout.mOffsetZ;

                    vtxX[i] = IntrinsicUtils.getCopyWithFloatLane(vtxX[i], lane, *((float*)vPtrX));
                    vtxY[i] = IntrinsicUtils.getCopyWithFloatLane(vtxY[i], lane, *((float*)vPtrY));
                    vtxW[i] = IntrinsicUtils.getCopyWithFloatLane(vtxW[i], lane, *((float*)vPtrW));
                }
            }
        }

        unsafe void GatherTransformClip(
            int FAST_GATHER,
            ref int clipHead, ref int clipTail,
            out int numLanes,
            int nTris, ref int triIndex,
            v128* vtxX, v128* vtxY, v128* vtxW,
            float* inVtx, uint** inTrisPtr,
            VertexLayout vtxLayout, float* modelToClipMatrix,
            v128* clipTriBuffer, out uint triMask, ClipPlanes clipPlaneMask)
        {
            //////////////////////////////////////////////////////////////////////////////
            // Assemble triangles from the index list
            //////////////////////////////////////////////////////////////////////////////
            uint triClipMask = SIMD_ALL_LANES_MASK;

            if (clipHead != clipTail)
            {
                int clippedTris = (clipHead > clipTail)
                    ? (clipHead - clipTail)
                    : (MAX_CLIPPED + clipHead - clipTail);
                clippedTris = Mathf.Min(clippedTris, SIMD_LANES);

#if MOC_CLIPPING_PRESERVES_ORDER
                // if preserving order, don't mix clipped and new triangles, handle the clip buffer fully
                // and then continue gathering; this is not as efficient - ideally we want to gather
                // at the end (if clip buffer has less than SIMD_LANES triangles) but that requires
                // more modifications below - something to do in the future.
                numLanes = 0;
#else
                // Fill out SIMD registers by fetching more triangles.
                numLanes = Mathf.Max(0, Mathf.Min(SIMD_LANES - clippedTris, nTris - triIndex));
#endif

                if (numLanes > 0)
                {
                    if (FAST_GATHER != 0)
                    {
                        GatherVerticesFast(vtxX, vtxY, vtxW, inVtx, *inTrisPtr, numLanes);
                    }
                    else
                    {
                        GatherVertices(vtxX, vtxY, vtxW, inVtx, *inTrisPtr, numLanes, vtxLayout);
                    }

                    TransformVerts(vtxX, vtxY, vtxW, modelToClipMatrix);
                }

                for (uint clipTri = (uint)numLanes; clipTri < (numLanes + clippedTris); clipTri++)
                {
                    int triIdx = clipTail * 3;

                    for (int i = 0; i < 3; i++)
                    {
                        float* vtxX_i_clipTri = (float*)((v128*)vtxX + i) + clipTri;
                        *vtxX_i_clipTri = *((float*)((v128*)clipTriBuffer + (triIdx + i)) + 0);
                        float* vtxY_i_clipTri = (float*)((v128*)vtxY + i) + clipTri;
                        *vtxY_i_clipTri = *((float*)((v128*)clipTriBuffer + (triIdx + i)) + 1);
                        float* vtxW_i_clipTri = (float*)((v128*)vtxW + i) + clipTri;
                        *vtxW_i_clipTri = *((float*)((v128*)clipTriBuffer + (triIdx + i)) + 2);
                    }
                    clipTail = (clipTail + 1) & (MAX_CLIPPED - 1);
                }

                triIndex += numLanes;
                inTrisPtr += numLanes * 3;

                triMask = (1u << (clippedTris + numLanes)) - 1;
                triClipMask = (1u << numLanes) - 1; // Don't re-clip already clipped triangles
            }
            else
            {
                numLanes = Mathf.Min(SIMD_LANES, nTris - triIndex);
                triMask = (1u << numLanes) - 1;
                triClipMask = triMask;

                if (FAST_GATHER != 0)
                {
                    GatherVerticesFast(vtxX, vtxY, vtxW, inVtx, *inTrisPtr, numLanes);
                }
                else
                {
                    GatherVertices(vtxX, vtxY, vtxW, inVtx, *inTrisPtr, numLanes, vtxLayout);
                }

                TransformVerts(vtxX, vtxY, vtxW, modelToClipMatrix);

                triIndex += SIMD_LANES;
                (*inTrisPtr) += SIMD_LANES * 3;
            }

            //////////////////////////////////////////////////////////////////////////////
            // Clip transformed triangles
            //////////////////////////////////////////////////////////////////////////////

            if (clipPlaneMask != ClipPlanes.CLIP_PLANE_NONE)
            {
                ClipTriangleAndAddToBuffer(vtxX, vtxY, vtxW, clipTriBuffer, ref clipHead, ref triMask, triClipMask, clipPlaneMask);
            }
        }

        ////// RenderTriangles //////

        public unsafe CullingResult RenderTriangles(float* inVtx, uint* inTris, int nTris)
        {
            Debug.Assert(inVtx != null);
            Debug.Assert(inTris != null);
            Debug.Assert(nTris > 0);
            Debug.Assert(mMaskedHiZBuffer != null);

            // DS TODO: do we need this configurable?
            // defaults via optional params:
            float* modelToClipMatrix = null;
            var bfWinding = BackfaceWinding.BACKFACE_CW;
            var clipPlaneMask = ClipPlanes.CLIP_PLANE_ALL;
            VertexLayout vtxLayout = new VertexLayout() { mStride = 16, mOffsetY = 4, mOffsetZ = 12 };

            // if (vtxLayout.mStride == 16 && vtxLayout.mOffsetY == 4 && vtxLayout.mOffsetW == 12)
            //  RenderTriangles < 0, 1 > (inVtx, inTris, nTris, modelToClipMatrix, bfWinding, clipPlaneMask, vtxLayout);    // <--- DS: we are using this right now
            // else
            //  RenderTriangles < 0, 0 > (inVtx, inTris, nTris, modelToClipMatrix, bfWinding, clipPlaneMask, vtxLayout);

            // former template params
            const int TEST_Z = 0;
            const int FAST_GATHER = 1;

#if MOC_ENABLE_STATS
            if (TEST_Z != 0)
            {
                STATS_ADD(ref mStats.mOccludees.mNumProcessedTriangles, nTris);
            }
            else
            {
                STATS_ADD(ref mStats.mOccluders.mNumProcessedTriangles, nTris);
            }
#endif

#if MOC_PRECISE_COVERAGE
            // DS: TODO: UNITY BURST FIX
            //using (var roundingMode = new X86.RoundingScope(X86.MXCSRBits.RoundToNearest))
            const X86.MXCSRBits roundingMode = X86.MXCSRBits.RoundToNearest;
            X86.MXCSRBits OldBits = X86.MXCSR;
            X86.MXCSR = (OldBits & ~X86.MXCSRBits.RoundingControlMask) | roundingMode;
#endif

            int clipHead = 0;
            int clipTail = 0;
            v128* clipTriBuffer = stackalloc v128[MAX_CLIPPED * 3];
            int cullResult = (int)CullingResult.VIEW_CULLED;

            uint* inTrisPtr = inTris;
            int numLanes = SIMD_LANES;  // DS: I don't think this matters and could be moved more narrow scope
            int triIndex = 0;


            v128* vtxX_prealloc = stackalloc v128[3];
            v128* vtxY_prealloc = stackalloc v128[3];
            v128* vtxW_prealloc = stackalloc v128[3];

            v128* pVtxX_prealloc = stackalloc v128[3];
            v128* pVtxY_prealloc = stackalloc v128[3];
            v128* pVtxZ_prealloc = stackalloc v128[3];

#if MOC_PRECISE_COVERAGE
            v128* ipVtxX_prealloc = stackalloc v128[3];
            v128* ipVtxY_prealloc = stackalloc v128[3];
#endif

            while (triIndex < nTris || clipHead != clipTail)
            {
                v128* vtxX = vtxX_prealloc;
                v128* vtxY = vtxY_prealloc;
                v128* vtxW = vtxW_prealloc;

                uint triMask = SIMD_ALL_LANES_MASK;

                GatherTransformClip(
                    FAST_GATHER,
                    ref clipHead, ref clipTail,
                    out numLanes,
                    nTris, ref triIndex,
                    vtxX, vtxY, vtxW,
                    inVtx, &inTrisPtr,
                    vtxLayout, modelToClipMatrix,
                    clipTriBuffer, out triMask, clipPlaneMask);

                if (triMask == 0x0)
                {
                    continue;
                }

                //////////////////////////////////////////////////////////////////////////////
                // Project, transform to screen space and perform backface culling. Note
                // that we use z = 1.0 / vtx.w for depth, which means that z = 0 is far and
                // z = 1 is near. We must also use a greater than depth test, and in effect
                // everything is reversed compared to regular z implementations.
                //////////////////////////////////////////////////////////////////////////////

                v128* pVtxX = pVtxX_prealloc;
                v128* pVtxY = pVtxY_prealloc;
                v128* pVtxZ = pVtxZ_prealloc;

#if MOC_PRECISE_COVERAGE
                v128* ipVtxX = ipVtxX_prealloc;
                v128* ipVtxY = ipVtxY_prealloc;
                ProjectVertices(ipVtxX, ipVtxY, pVtxX, pVtxY, pVtxZ, vtxX, vtxY, vtxW);
#else
                ProjectVertices(pVtxX, pVtxY, pVtxZ, vtxX, vtxY, vtxW);
#endif

                // Perform backface test.
                v128 triArea1 = X86.Sse.mul_ps(X86.Sse.sub_ps(pVtxX[1], pVtxX[0]), X86.Sse.sub_ps(pVtxY[2], pVtxY[0]));
                v128 triArea2 = X86.Sse.mul_ps(X86.Sse.sub_ps(pVtxX[0], pVtxX[2]), X86.Sse.sub_ps(pVtxY[0], pVtxY[1]));
                v128 triArea = X86.Sse.sub_ps(triArea1, triArea2);
                v128 ccwMask = X86.Sse.cmpgt_ps(triArea, X86.Sse2.setzero_si128());

#if MOC_PRECISE_COVERAGE
                triMask &= (uint)CullBackfaces(ipVtxX, ipVtxY, pVtxX, pVtxY, pVtxZ, ccwMask, bfWinding);
#else
                triMask &= (uint)CullBackfaces(pVtxX, pVtxY, pVtxZ, ccwMask, bfWinding);
#endif

                if (triMask == 0x0)
                {
                    continue;
                }

                //////////////////////////////////////////////////////////////////////////////
                // Setup and rasterize a SIMD batch of triangles
                //////////////////////////////////////////////////////////////////////////////
#if MOC_PRECISE_COVERAGE
                cullResult &= RasterizeTriangleBatch(TEST_Z, ipVtxX, ipVtxY, pVtxX, pVtxY, pVtxZ, triMask, mFullscreenScissor);
#else
                cullResult &= RasterizeTriangleBatch(TEST_Z, pVtxX, pVtxY, pVtxZ, triMask, mFullscreenScissor);
#endif

                if (TEST_Z != 0 && cullResult == (int)CullingResult.VISIBLE)
                {
#if MOC_PRECISE_COVERAGE
                    // DS: TODO: UNITY BURST FIX
                    X86.MXCSR = OldBits;
#endif
                    return (int)CullingResult.VISIBLE;
                }
            }

#if MOC_PRECISE_COVERAGE
            // DS: TODO: UNITY BURST FIX
            X86.MXCSR = OldBits;
#endif

            return (CullingResult)cullResult;
        }

        //void BinTriangles(const float* inVtx, const unsigned int* inTris, int nTris, TriList *triLists, unsigned int nBinsW, unsigned int nBinsH, const float* modelToClipMatrix, BackfaceWinding bfWinding, ClipPlanes clipPlaneMask, const VertexLayout &vtxLayout) override
        public unsafe void BinTriangles(float* inVtx, uint* inTris, int nTris, TriList* triLists, int nBinsW, int nBinsH)
        {
            Debug.Assert(inVtx != null);
            Debug.Assert(inTris != null);
            Debug.Assert(nTris > 0);
            Debug.Assert(triLists != null);
            Debug.Assert(nBinsW > 0);
            Debug.Assert(nBinsH > 0);

            //if (vtxLayout.mStride == 16 && vtxLayout.mOffsetY == 4 && vtxLayout.mOffsetW == 12)
            //    BinTriangles<true>(inVtx, inTris, nTris, triLists, nBinsW, nBinsH, modelToClipMatrix, bfWinding, clipPlaneMask, vtxLayout);
            //else
            //    BinTriangles<false>(inVtx, inTris, nTris, triLists, nBinsW, nBinsH, modelToClipMatrix, bfWinding, clipPlaneMask, vtxLayout);

            // former template param
            const int FAST_GATHER = 1;

            // defaults via optional params:
            float* modelToClipMatrix = null;
            var bfWinding = BackfaceWinding.BACKFACE_CW;
            var clipPlaneMask = ClipPlanes.CLIP_PLANE_ALL;
            VertexLayout vtxLayout = new VertexLayout() { mStride = 16, mOffsetY = 4, mOffsetZ = 12 };

#if MOC_PRECISE_COVERAGE
            // DS: TODO: UNITY BURST FIX
            //using (var roundingMode = new X86.RoundingScope(X86.MXCSRBits.RoundToNearest))
            const X86.MXCSRBits roundingMode = X86.MXCSRBits.RoundToNearest;
            X86.MXCSRBits OldBits = X86.MXCSR;
            X86.MXCSR = (OldBits & ~X86.MXCSRBits.RoundingControlMask) | roundingMode;
#endif

#if MOC_ENABLE_STATS
            STATS_ADD(ref mStats.mOccluders.mNumProcessedTriangles, nTris);
#endif

            int clipHead = 0;
            int clipTail = 0;
            v128* clipTriBuffer = stackalloc v128[MAX_CLIPPED * 3];

            uint* inTrisPtr = inTris;
            int numLanes = SIMD_LANES;
            int triIndex = 0;


            v128* vtxX_prealloc = stackalloc v128[3];
            v128* vtxY_prealloc = stackalloc v128[3];
            v128* vtxW_prealloc = stackalloc v128[3];

            v128* pVtxX_prealloc = stackalloc v128[3];
            v128* pVtxY_prealloc = stackalloc v128[3];
            v128* pVtxZ_prealloc = stackalloc v128[3];

#if MOC_PRECISE_COVERAGE
            v128* ipVtxX_prealloc = stackalloc v128[3];
            v128* ipVtxY_prealloc = stackalloc v128[3];
#endif

            while (triIndex < nTris || clipHead != clipTail)
            {
                uint triMask = SIMD_ALL_LANES_MASK;

                v128* vtxX = vtxX_prealloc;
                v128* vtxY = vtxY_prealloc;
                v128* vtxW = vtxW_prealloc;

                GatherTransformClip(
                    FAST_GATHER,
                    ref clipHead, ref clipTail,
                    out numLanes,
                    nTris, ref triIndex,
                    vtxX, vtxY, vtxW,
                    inVtx, &inTrisPtr,
                    vtxLayout, modelToClipMatrix,
                    clipTriBuffer, out triMask, clipPlaneMask);

                if (triMask == 0x0)
                {
                    continue;
                }

                //////////////////////////////////////////////////////////////////////////////
                // Project, transform to screen space and perform backface culling. Note
                // that we use z = 1.0 / vtx.w for depth, which means that z = 0 is far and
                // z = 1 is near. We must also use a greater than depth test, and in effect
                // everything is reversed compared to regular z implementations.
                //////////////////////////////////////////////////////////////////////////////

                v128* pVtxX = pVtxX_prealloc;
                v128* pVtxY = pVtxY_prealloc;
                v128* pVtxZ = pVtxZ_prealloc;

#if MOC_PRECISE_COVERAGE
                v128* ipVtxX = ipVtxX_prealloc;
                v128* ipVtxY = ipVtxY_prealloc;
                ProjectVertices(ipVtxX, ipVtxY, pVtxX, pVtxY, pVtxZ, vtxX, vtxY, vtxW);
#else
                ProjectVertices(pVtxX, pVtxY, pVtxZ, vtxX, vtxY, vtxW);
#endif

                // Perform backface test.
                v128 triArea1 = X86.Sse.mul_ps(X86.Sse.sub_ps(pVtxX[1], pVtxX[0]), X86.Sse.sub_ps(pVtxY[2], pVtxY[0]));
                v128 triArea2 = X86.Sse.mul_ps(X86.Sse.sub_ps(pVtxX[0], pVtxX[2]), X86.Sse.sub_ps(pVtxY[0], pVtxY[1]));
                v128 triArea = X86.Sse.sub_ps(triArea1, triArea2);
                v128 ccwMask = X86.Sse.cmpgt_ps(triArea, X86.Sse.setzero_ps());

#if MOC_PRECISE_COVERAGE
                triMask &= (uint)CullBackfaces(ipVtxX, ipVtxY, pVtxX, pVtxY, pVtxZ, ccwMask, bfWinding);
#else
                triMask &= (uint)CullBackfaces(pVtxX, pVtxY, pVtxZ, ccwMask, bfWinding);
#endif

                if (triMask == 0x0)
                {
                    continue;
                }

                //////////////////////////////////////////////////////////////////////////////
                // Bin triangles
                //////////////////////////////////////////////////////////////////////////////

                int binWidth;
                int binHeight;
                ComputeBinWidthHeight(nBinsW, nBinsH, out binWidth, out binHeight);

                // Compute pixel bounding box
                v128 bbPixelMinX;
                v128 bbPixelMinY;
                v128 bbPixelMaxX;
                v128 bbPixelMaxY;
                ComputeBoundingBox(pVtxX, pVtxY, ref mFullscreenScissor, out bbPixelMinX, out bbPixelMinY, out bbPixelMaxX, out bbPixelMaxY);

                while (triMask > 0)
                {
                    uint triIdx = (uint)IntrinsicUtils.find_clear_lsb(ref triMask);

                    // Clamp bounding box to bins
                    int startX = Mathf.Min(nBinsW - 1, IntrinsicUtils.getIntLane(bbPixelMinX, triIdx) / binWidth);
                    int startY = Mathf.Min(nBinsH - 1, IntrinsicUtils.getIntLane(bbPixelMinY, triIdx) / binHeight);
                    int endX = Mathf.Min(nBinsW, (IntrinsicUtils.getIntLane(bbPixelMaxX, triIdx) + binWidth - 1) / binWidth);
                    int endY = Mathf.Min(nBinsH, (IntrinsicUtils.getIntLane(bbPixelMaxY, triIdx) + binHeight - 1) / binHeight);

                    for (int y = startY; y < endY; ++y)
                    {
                        for (int x = startX; x < endX; ++x)
                        {
                            int binIdx = x + y * nBinsW;
                            uint writeTriIdx = triLists[binIdx].mNextTriIdx;
                            for (int i = 0; i < 3; ++i)
                            {
#if MOC_PRECISE_COVERAGE
                                ((int*)triLists[binIdx].mDataBufferPtr)[i * 3 + writeTriIdx * 9 + 0] = IntrinsicUtils.getIntLane(ipVtxX[i], triIdx);
                                ((int*)triLists[binIdx].mDataBufferPtr)[i * 3 + writeTriIdx * 9 + 1] = IntrinsicUtils.getIntLane(ipVtxY[i], triIdx);
#else
                                triLists[binIdx].mDataBufferPtr[i * 3 + writeTriIdx * 9 + 0] = IntrinsicUtils.getFloatLane(pVtxX[i], triIdx);
                                triLists[binIdx].mDataBufferPtr[i * 3 + writeTriIdx * 9 + 1] = IntrinsicUtils.getFloatLane(pVtxY[i], triIdx);
#endif
                                triLists[binIdx].mDataBufferPtr[i * 3 + writeTriIdx * 9 + 2] = IntrinsicUtils.getFloatLane(pVtxZ[i], triIdx);
                            }
                            triLists[binIdx].mNextTriIdx++;
                        }
                    }
                }
            }

#if MOC_PRECISE_COVERAGE
            // DS: TODO: UNITY BURST FIX
            X86.MXCSR = OldBits;
#endif
        }

        public unsafe void RenderTrilist(TriList* triList, ScissorRect* scissor)
        {
            Debug.Assert(triList != null);
            Debug.Assert(scissor != null);

            // Setup fullscreen scissor rect as default
            ScissorRect usedScissor = scissor == null ? mFullscreenScissor : *scissor;

            v128* pVtxX_prealloc = stackalloc v128[3];
            v128* pVtxY_prealloc = stackalloc v128[3];
            v128* pVtxZ_prealloc = stackalloc v128[3];

#if MOC_PRECISE_COVERAGE

            v128* ipVtxX_prealloc = stackalloc v128[3];
            v128* ipVtxY_prealloc = stackalloc v128[3];
#endif

            for (uint i = 0; i < triList->mNextTriIdx; i += SIMD_LANES)
            {
                //////////////////////////////////////////////////////////////////////////////
                // Fetch triangle vertices
                //////////////////////////////////////////////////////////////////////////////

                uint numLanes = (uint)Mathf.Min(SIMD_LANES, (int)(triList->mNextTriIdx - i));
                uint triMask = (1u << (int)numLanes) - 1;

                v128* pVtxX = pVtxX_prealloc;
                v128* pVtxY = pVtxY_prealloc;
                v128* pVtxZ = pVtxZ_prealloc;

#if MOC_PRECISE_COVERAGE

                v128* ipVtxX = ipVtxX_prealloc;
                v128* ipVtxY = ipVtxY_prealloc;

                for (uint l = 0; l < numLanes; ++l)
                {
                    uint triIdx = i + l;
                    for (int v = 0; v < 3; ++v)
                    {
                        ipVtxX[v] = IntrinsicUtils.getCopyWithIntLane(ipVtxX[v], l, ((int*)triList->mDataBufferPtr)[v * 3 + triIdx * 9 + 0]);
                        ipVtxY[v] = IntrinsicUtils.getCopyWithIntLane(ipVtxY[v], l, ((int*)triList->mDataBufferPtr)[v * 3 + triIdx * 9 + 1]);
                        pVtxZ[v] = IntrinsicUtils.getCopyWithFloatLane(pVtxZ[v], l, triList->mDataBufferPtr[v * 3 + triIdx * 9 + 2]);
                    }
                }

                for (int v = 0; v < 3; ++v)
                {
                    pVtxX[v] = X86.Sse.mul_ps(X86.Sse2.cvtepi32_ps(ipVtxX[v]), X86.Sse.set1_ps(FP_INV));
                    pVtxY[v] = X86.Sse.mul_ps(X86.Sse2.cvtepi32_ps(ipVtxY[v]), X86.Sse.set1_ps(FP_INV));
                }

                //////////////////////////////////////////////////////////////////////////////
                // Setup and rasterize a SIMD batch of triangles
                //////////////////////////////////////////////////////////////////////////////

                int TEST_Z = 0;
                RasterizeTriangleBatch(TEST_Z, ipVtxX, ipVtxY, pVtxX, pVtxY, pVtxZ, triMask, usedScissor);

#else

                for (uint l = 0; l < numLanes; ++l)
                {
                    uint triIdx = i + l;
                    for (int v = 0; v < 3; ++v)
                    {
                        pVtxX[v] = IntrinsicUtils.getCopyWithFloatLane(pVtxX[v], l, triList->mDataBufferPtr[v * 3 + triIdx * 9 + 0]);
                        pVtxY[v] = IntrinsicUtils.getCopyWithFloatLane(pVtxY[v], l, triList->mDataBufferPtr[v * 3 + triIdx * 9 + 1]);
                        pVtxZ[v] = IntrinsicUtils.getCopyWithFloatLane(pVtxZ[v], l, triList->mDataBufferPtr[v * 3 + triIdx * 9 + 2]);
                    }
                }

                //////////////////////////////////////////////////////////////////////////////
                // Setup and rasterize a SIMD batch of triangles
                //////////////////////////////////////////////////////////////////////////////

                int TEST_Z = 0;
                RasterizeTriangleBatch(TEST_Z, pVtxX, pVtxY, pVtxZ, triMask, usedScissor);

#endif

            }
        }


        public unsafe void ComputeBinWidthHeight(int nBinsW, int nBinsH, out int outBinWidth, out int outBinHeight)
        {
            outBinWidth = (mWidth / nBinsW) - ((mWidth / nBinsW) % TILE_WIDTH);
            outBinHeight = (mHeight / nBinsH) - ((mHeight / nBinsH) % TILE_HEIGHT);
        }
    }

#endif
}
#endif
