#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering.Occlusion
{
    [ExecuteAlways]
    [ExecuteInEditMode]
    public class OcclusionDebugVisualization : MonoBehaviour
    {
        public void OnEnable()
        {
            RenderPipelineManager.endFrameRendering += RenderOverlays;
        }

        public void OnDisable()
        {
            RenderPipelineManager.endFrameRendering -= RenderOverlays;
        }

        private void RenderOverlays(ScriptableRenderContext context, Camera[] cameras)
        {
            var occlusionSettings = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionSettingsSystem>();
            if (occlusionSettings.OcclusionEnabled)
            {
                var debugSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<OcclusionDebugRenderSystem>();
                debugSystem.Render();
            }
        }

        Mesh quad;
        struct QuadVertex
        {
            public float4 pos;
            public float2 uv;
        }

        Mesh GetQuadMesh()
        {
            if (quad != null)
            {
                return quad;
            }

            var layout = new[]
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            };

            quad = new Mesh();
            quad.SetVertexBufferParams(4, layout);

            var quadVerts = new NativeArray<QuadVertex>(4, Allocator.Temp);

            var margin = 1.0f;
            quadVerts[0] = new QuadVertex() { pos = new float4(-margin, -margin, 1, 1), uv = new float2(0, 0) };
            quadVerts[1] = new QuadVertex() { pos = new float4(margin, -margin, 1, 1), uv = new float2(1, 0) };
            quadVerts[2] = new QuadVertex() { pos = new float4(margin, margin, 1, 1), uv = new float2(1, 1) };
            quadVerts[3] = new QuadVertex() { pos = new float4(-margin, margin, 1, 1), uv = new float2(0, 1) };

            quad.SetVertexBufferData(quadVerts, 0, 0, 4);
            quadVerts.Dispose();

            var quadTris = new int[6] { 0, 1, 2, 0, 2, 3 };
            quad.SetIndices(quadTris, MeshTopology.Triangles, 0);
            quad.bounds = new Bounds(Vector3.zero, new Vector3(10000, 10000, 1000));

            return quad;
        }

        public void Update()
        {
            
        }
    }
}

#endif
