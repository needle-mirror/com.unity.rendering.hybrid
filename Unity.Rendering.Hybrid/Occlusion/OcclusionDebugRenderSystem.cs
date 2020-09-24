#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Unity.Rendering.Occlusion
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [ExecuteAlways]
    [AlwaysUpdateSystem]
    [AlwaysSynchronizeSystem]
    [UpdateAfter(typeof(HybridRendererSystem))]
   unsafe public class OcclusionDebugRenderSystem : JobComponentSystem
    {
#if UNITY_MOC_NATIVE_AVAILABLE

        [NativeDisableUnsafePtrRestriction]
        [ReadOnly]
        private NativeArray<IntPtr> m_MocNativePtrArray;
#endif
        [ReadOnly]
        private NativeArray<IntPtr> m_BurstIntrinsicsPtrArray;

        private OcclusionSettingsSystem.MOCOcclusionMode m_MocOcclusionMode;

        //MOC plugin return the depth as an array of float, we need an array of float4 for setting the texture data
        private NativeArray<float> m_VisualizeDepthBuffer;
        private NativeArray<float> m_VisualizeTestBuffer;
        private NativeArray<float> m_VisualizeBoundsBuffer;

        Texture2D m_DepthTexture;
        Texture2D m_TestTexture;
        Texture2D m_BoundsTexture;

        Mesh m_QuadMesh;

        private int m_Size;

        private int m_TotalOcclusionDrawPerFrame = 1;
        public int TotalOcclusionDrawPerFrame
        {
            get { return m_TotalOcclusionDrawPerFrame; }
        }
        private int m_CurrentOcclusionDraw = 0;

        private int m_WantedOcclusionDraw = 1;
        public int WantedOcclusionDraw
        {
            get { return m_WantedOcclusionDraw; }
            set { m_WantedOcclusionDraw = Math.Min(value, m_TotalOcclusionDrawPerFrame); }
        }

        struct QuadVertex
        {
            public float4 pos;
            public float2 uv;
        }

        public enum DebugRenderMode
        {
            None = 0,
            Mesh = 1,
            Depth = 2,
            Bounds = 3,
            Test = 4
        }
        public DebugRenderMode m_DebugRenderMode = DebugRenderMode.None;

        EntityQuery m_Occluders, m_Occludees;
        GameObject m_DebugGO;

        protected override void OnCreate()
        {
            m_Occluders = GetEntityQuery(ComponentType.ReadOnly<OcclusionMesh>());
            m_Occludees = GetEntityQuery(ComponentType.ReadOnly<OcclusionTest>());

            m_DebugGO = new GameObject("Occlusion Debug Visualizer", typeof(OcclusionDebugVisualization));
            m_DebugGO.hideFlags = HideFlags.HideAndDontSave;
        }

        protected override void OnDestroy()
        {
            if (m_VisualizeDepthBuffer.IsCreated)
            {
                m_VisualizeDepthBuffer.Dispose();
            }

            if(m_VisualizeTestBuffer.IsCreated)
            {
                m_VisualizeTestBuffer.Dispose();
            }

            if (m_VisualizeBoundsBuffer.IsCreated)
            {
                m_VisualizeBoundsBuffer.Dispose();
            }


            GameObject.DestroyImmediate(m_DebugGO);
        }




#if UNITY_MOC_NATIVE_AVAILABLE
        public void RenderMOCInstances(NativeArray<IntPtr> mocNativePtrArray_, NativeArray<IntPtr> mocBurstIntrinsicsPtrArray_, OcclusionSettingsSystem.MOCOcclusionMode mocOcclusionMode_, JobHandle renderDependency)
#else
        public void RenderMOCInstances(NativeArray<IntPtr> mocBurstIntrinsicsPtrArray_, OcclusionSettingsSystem.MOCOcclusionMode mocOcclusionMode_, JobHandle renderDependency)
#endif
        {
            m_CurrentOcclusionDraw++;
            if(m_CurrentOcclusionDraw == m_WantedOcclusionDraw && m_DebugRenderMode != DebugRenderMode.None)
            {
                Profiler.BeginSample("Debug RenderMOCInstances");

                //Sync point is needed as the RenderMOCInstances is called from the main thread
                renderDependency.Complete();

#if UNITY_MOC_NATIVE_AVAILABLE
                m_MocNativePtrArray = mocNativePtrArray_;
#endif
                m_BurstIntrinsicsPtrArray = mocBurstIntrinsicsPtrArray_;
                m_MocOcclusionMode = mocOcclusionMode_;

                RenderDepthBoundTexture();

                Profiler.EndSample();

            }
        }

        public void InitDepthBoundTexture()
        {
            uint depthWidth = 0;
            uint depthHeight = 0;

            if (m_MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Intrinsic)
            {
                MOC.BurstIntrinsics* mocBurstIntrinsic = (MOC.BurstIntrinsics*)m_BurstIntrinsicsPtrArray[0];
                mocBurstIntrinsic->GetResolution(out depthWidth, out depthHeight);
            }
#if UNITY_MOC_NATIVE_AVAILABLE
            else if (m_MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Native)
            {
                void* mocNative = (void*)m_MocNativePtrArray[0];
                INTEL_MOC.MOCNative.GetResolution(mocNative, out depthWidth, out depthHeight);
            }
#endif

            if (depthWidth == 0 && depthHeight == 0)
            {
                Debug.LogError("moc instance not set up");
                return;
            }

            if (depthWidth != depthHeight)
            {
                Debug.LogError("Currently occlusion debug render system only support same size width and depth");
            }

            m_Size = (int)depthWidth;

            if (m_DepthTexture == null || m_DepthTexture.width != depthWidth || m_DepthTexture.height != depthHeight)
            {
                m_DepthTexture = new Texture2D((int)depthWidth, (int)depthHeight, TextureFormat.RFloat, false);
                m_TestTexture = new Texture2D((int)depthWidth, (int)depthHeight, TextureFormat.RFloat, false);
                m_BoundsTexture = new Texture2D((int)depthWidth, (int)depthHeight, TextureFormat.RFloat, false);
            }

            if (!m_VisualizeDepthBuffer.IsCreated || m_VisualizeDepthBuffer.Length != depthWidth * depthHeight)
            {
                if (m_VisualizeDepthBuffer.IsCreated)
                {
                    m_VisualizeDepthBuffer.Dispose();
                }

                if (m_VisualizeTestBuffer.IsCreated)
                {
                    m_VisualizeTestBuffer.Dispose();
                }

                if (m_VisualizeBoundsBuffer.IsCreated)
                {
                    m_VisualizeBoundsBuffer.Dispose();
                }


                m_VisualizeDepthBuffer = new NativeArray<float>((int)(depthWidth * depthHeight), Allocator.Persistent);
                m_VisualizeTestBuffer = new NativeArray<float>(m_VisualizeDepthBuffer.Length, Allocator.Persistent);
                m_VisualizeBoundsBuffer = new NativeArray<float>(m_VisualizeDepthBuffer.Length, Allocator.Persistent);
            }
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            //Reset our total amount based on the previous frame total draw calls
            //Currently there's no way to know how many calls to OnPerformCulling we will have, counting the calls to RenderMOCInstances(...) on the previous frame allow us to get an idea
            m_TotalOcclusionDrawPerFrame = m_CurrentOcclusionDraw;
            if(m_WantedOcclusionDraw > m_TotalOcclusionDrawPerFrame)
            {
                m_WantedOcclusionDraw = 1;
            }

            m_CurrentOcclusionDraw = 0;
            return new JobHandle();
        }

        unsafe public void RenderDepthBoundTexture()
        {
            InitDepthBoundTexture();

            if (m_MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Intrinsic)
            {
                if (m_BurstIntrinsicsPtrArray == null || m_BurstIntrinsicsPtrArray[0] == IntPtr.Zero)
                {
                    return;
                }
            }
#if UNITY_MOC_NATIVE_AVAILABLE
            else if (m_MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Native)
            {
                if (m_MocNativePtrArray == null || m_MocNativePtrArray[0] == IntPtr.Zero)
                {
                    return;
                }
            }
#endif

            UnsafeUtility.MemClear(m_VisualizeDepthBuffer.GetUnsafePtr(), m_VisualizeDepthBuffer.Length * sizeof(float));
            UnsafeUtility.MemClear(m_VisualizeTestBuffer.GetUnsafePtr(), m_VisualizeDepthBuffer.Length * sizeof(float));
            UnsafeUtility.MemClear(m_VisualizeBoundsBuffer.GetUnsafePtr(), m_VisualizeDepthBuffer.Length * sizeof(float));

            if (m_DebugRenderMode == DebugRenderMode.Depth)
            {
                ShowDepth();
            }
            else if (m_DebugRenderMode == DebugRenderMode.Bounds)
            {
                ShowBounds(false);
            }
            else if (m_DebugRenderMode == DebugRenderMode.Test)
            {
                ShowDepth();
                ShowBounds(true);
                //ShowBounds(ptr, false);
            }

            m_DepthTexture.SetPixelData(m_VisualizeDepthBuffer, 0);
            m_DepthTexture.Apply();

            m_TestTexture.SetPixelData(m_VisualizeTestBuffer, 0);
            m_TestTexture.Apply();

            m_BoundsTexture.SetPixelData(m_VisualizeBoundsBuffer, 0);
            m_BoundsTexture.Apply();

        }

        unsafe public void Render()
        {
            if (m_DebugRenderMode == DebugRenderMode.None)
                return;

            if (m_DebugRenderMode == DebugRenderMode.Mesh)
            {
                var OcclusionMesh = GetComponentTypeHandle<OcclusionMesh>();

                var material = new Material(Shader.Find("Hidden/OcclusionDebug"));

                var layout = new[]
                {
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 4)
                };

                var chunks = m_Occluders.CreateArchetypeChunkArray(Allocator.TempJob);

                Mathematics.Random rnd = new Mathematics.Random(1);
                for (int i = 0; i < chunks.Length; ++i)
                {
                    var chunk = chunks[i];
                    var meshes = chunk.GetNativeArray(OcclusionMesh);
                    for (int k = 0; k < meshes.Length; k++)
                    {
                        var m = meshes[k];

                        Mesh mesh = new Mesh();
                        mesh.SetVertexBufferParams(m.vertexCount, layout);

                        var verts = (float4*)m.transformedVertexData.GetUnsafePtr();
                        var tris = (int*)m.indexData.GetUnsafePtr();

                        var outVerts = new NativeArray<Vector4>(m.vertexCount, Allocator.Temp);
                        for (int vtx = 0; vtx < m.vertexCount; vtx++, ++verts)
                        {
                            outVerts[vtx] = new Vector4(verts->x, verts->y, verts->z, verts->w);
                        }
                        mesh.SetVertexBufferData(outVerts, 0, 0, m.vertexCount);
                        outVerts.Dispose();

                        var outTris = new int[m.indexCount];
                        for (int idx = 0; idx < m.indexCount; idx++, ++tris)
                        {
                            outTris[idx] = *tris;
                        }
                        mesh.SetIndices(outTris, MeshTopology.Triangles, 0);

                        mesh.name = $"Debug Occluder {i}:{k}";

                        // the vertices are already in screenspace and perspective projected
                        mesh.bounds = new Bounds(Vector3.zero, new Vector3(10000, 10000, 1000));

                        var color = Color.HSVToRGB(rnd.NextFloat(), 1.0f, 1.0f);
                        material.SetColor("_Color", color);

                        material.SetPass(0);
                        Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
                    }
                }

                chunks.Dispose();
            }
            else if (m_DepthTexture != null)
            {
                var material = new Material(Shader.Find("Hidden/OcclusionShowDepth"));
                Mesh quad = GetQuadMesh();


                if (m_DebugRenderMode == DebugRenderMode.Bounds)
                {
                    material.EnableKeyword("BOUNDS_ONLY");
                }
                else if (m_DebugRenderMode == DebugRenderMode.Test)
                {
                    material.EnableKeyword("DEPTH_WITH_TEST");
                }

                material.SetTexture("_Depth", m_DepthTexture);
                material.SetTexture("_Test", m_TestTexture);
                material.SetTexture("_Bounds", m_BoundsTexture);
                material.SetPass(0);
                Graphics.DrawMeshNow(quad, Matrix4x4.identity);
            }
        }




        unsafe protected void ShowDepth()
        {
            float* DepthBuffer = (float*)m_VisualizeDepthBuffer.GetUnsafePtr();


            if (m_MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Intrinsic)
            {
                MOC.BurstIntrinsics* mocBurstIntrinsic = (MOC.BurstIntrinsics*)m_BurstIntrinsicsPtrArray[0];
                mocBurstIntrinsic->ComputePixelDepthBuffer(DepthBuffer, true);
            }
#if UNITY_MOC_NATIVE_AVAILABLE
            else if (m_MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Native)
            {
                void* mocNative = (void*)m_MocNativePtrArray[0];
                INTEL_MOC.MOCNative.VisualizeDepthBuffer(mocNative, DepthBuffer, true);
            }
#endif

           /* for (int i = 0; i < m_VisualizeDepthBuffer.Length; i++)
            {
                m_VisualizeDepthBuffer[i] = -1.0f * m_VisualizeDepthBuffer[i];
            }*/
        }

        private void ShowBounds(bool showTest)
        {
            var OcclusionTest = GetComponentTypeHandle<OcclusionTest>();
            var chunks = m_Occludees.CreateArchetypeChunkArray(Allocator.TempJob);

            

            for (int i = 0; i < chunks.Length; ++i)
            {
                var chunk = chunks[i];
                var tests = chunk.GetNativeArray(OcclusionTest);
                for (int k = 0; k < tests.Length; k++)
                {
                    var test = tests[k];

                    if (test.screenMax.z < 0)
                        continue;

                    int margin = showTest ? 0 : 1;

                    var min = math.clamp((int3)((test.screenMin.xyz * m_Size + m_Size) * 0.5f), -margin, m_Size + margin - 1);
                    var max = math.clamp((int3)((test.screenMax.xyz * m_Size + m_Size) * 0.5f), -margin, m_Size + margin - 1);


                    if (showTest)
                    {
                        float* ptr = (float*)m_VisualizeTestBuffer.GetUnsafePtr();

                        bool visible = false;

                        if (m_MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Intrinsic)
                        {
                            MOC.BurstIntrinsics* mocBurstIntrinsic = (MOC.BurstIntrinsics*)m_BurstIntrinsicsPtrArray[0];

                            MOC.CullingResult cullingResult = MOC.CullingResult.VISIBLE;
                            cullingResult = mocBurstIntrinsic->TestRect(test.screenMin.x, test.screenMin.y, test.screenMax.x, test.screenMax.y, test.screenMin.w);
                            if (cullingResult == MOC.CullingResult.VISIBLE)
                            {
                                visible = true;
                            }
                        }
#if UNITY_MOC_NATIVE_AVAILABLE
                        else if (m_MocOcclusionMode == OcclusionSettingsSystem.MOCOcclusionMode.Native)
                        {
                            void* mocNative = (void*)m_MocNativePtrArray[0];

                            INTEL_MOC.CullingResult cullingResult = INTEL_MOC.CullingResult.VISIBLE;
                            cullingResult = INTEL_MOC.MOCNative.TestRect(mocNative, test.screenMin.x, test.screenMin.y, test.screenMax.x, test.screenMax.y, test.screenMin.w);
                            if (cullingResult == INTEL_MOC.CullingResult.VISIBLE)
                            {
                                visible = true;
                            }
                        }
#endif

                        if (!visible)
                        {
                            for (int y = min.y; y < max.y; y++)
                            {
                                var dst = ptr + min.x + y * m_Size;
                                for (int x = min.x; x < max.x; x++, dst++)
                                {
                                    *dst = 1.0f;// Math.Min(*dst + 0.5f, 1.0f); //new float4(1, dst->y, dst->z, 1);
                                }
                            }
                        }
                    }
                    else
                    {
                        float* ptr = (float*)m_VisualizeBoundsBuffer.GetUnsafePtr();

                        bool4 edges = new bool4(min.x >= 0 && min.x < m_Size, max.x >= 0 && max.x < m_Size,
                            min.y >= 0 && min.y < m_Size, max.y >= 0 && max.y < m_Size);

                        for (int x = min.x; x <= max.x; x++)
                        {
                            if (x >= 0 && x < m_Size)
                            {
                                if (edges[2])
                                    ptr[x + min.y * m_Size] = 1;

                                if (edges[3])
                                    ptr[x + max.y * m_Size] = 1;
                            }
                        }

                        for (int y = min.y; y <= max.y; y++)
                        {
                            if (y >= 0 && y < m_Size)
                            {
                                if (edges[0])
                                    ptr[min.x + y * m_Size] = 1;

                                if (edges[1])
                                    ptr[max.x + y * m_Size] = 1;
                            }
                        }
                    }
                }
            }

            chunks.Dispose();
        }

        

        Mesh GetQuadMesh()
        {
            if (m_QuadMesh != null)
            {
                return m_QuadMesh;
            }

            var layout = new[]
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            };

            m_QuadMesh = new Mesh();
            m_QuadMesh.SetVertexBufferParams(4, layout);

            var quadVerts = new NativeArray<QuadVertex>(4, Allocator.Temp);

            var margin = 1.0f;
            quadVerts[0] = new QuadVertex() { pos = new float4(-margin, -margin, 1, 1), uv = new float2(0, 0) };
            quadVerts[1] = new QuadVertex() { pos = new float4(margin, -margin, 1, 1), uv = new float2(1, 0) };
            quadVerts[2] = new QuadVertex() { pos = new float4(margin, margin, 1, 1), uv = new float2(1, 1) };
            quadVerts[3] = new QuadVertex() { pos = new float4(-margin, margin, 1, 1), uv = new float2(0, 1) };

            m_QuadMesh.SetVertexBufferData(quadVerts, 0, 0, 4);
            quadVerts.Dispose();

            var quadTris = new int[6] { 0, 1, 2, 0, 2, 3 };
            m_QuadMesh.SetIndices(quadTris, MeshTopology.Triangles, 0);
            m_QuadMesh.bounds = new Bounds(Vector3.zero, new Vector3(10000, 10000, 1000));

            return m_QuadMesh;
        }
    }
}

#endif
