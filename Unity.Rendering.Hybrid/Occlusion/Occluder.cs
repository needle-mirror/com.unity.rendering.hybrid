#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering.Occlusion
{

    public class OccluderVolume
    {
        public Vector3[] vertices;
        public int[] indices;

        public void CreateCube()
        {
            CreatePrism(4);
        }

        public void CreatePrism(int sides)
        {
            vertices = new Vector3[sides * 2];

            float sideLength = Mathf.Sqrt(2);

            // compute an angle offset such that the bottom edge is horizontal
            float angleOffset = -Mathf.Atan2(Mathf.Sin(2 * Mathf.PI / sides), Mathf.Cos(2 * Mathf.PI / sides) - 1);
            for (int i = 0; i < sides; i++)
            {
                float t = (float)i * 2 * Mathf.PI / sides;
                float x = Mathf.Cos(angleOffset + t) / sideLength;
                float y = Mathf.Sin(angleOffset + t) / sideLength;

                vertices[i] = new Vector4(x, y, 0.5f);
                vertices[i + sides] = new Vector4(x, y, -0.5f);
            }

            // we're extruding polygons with N sides.  each polygon has N-2 triangles,
            // we have two of those, plus N*2 triangles connecting them, so the total is
            // 2(N-2)+2N = 4(N-1), and the total number of indices is then 12(N-1).
            indices = new int[12 * (sides - 1)];

            int[][] polyFaces = new int[2 + sides][];

            polyFaces[0] = new int[sides];
            polyFaces[1] = new int[sides];
            for (int i = 0; i < sides; i++)
            {
                polyFaces[0][i] = i;
                polyFaces[1][i] = sides * 2 - i - 1;

                polyFaces[i + 2] = new int[] { i, i + sides, sides + (i + 1) % sides, (i + 1) % sides };
            }

            // triangulate each face as a strip
            var idx = 0;
            foreach (var face in polyFaces)
            {
                int count = face.Length;

                int triBase = 0;
                for (int i = 0; i < count - 2; i++)
                {
                    if (i % 2 == 0)
                    {
                        indices[idx++] = face[triBase];
                        indices[idx++] = face[i / 2 + 1];
                        indices[idx++] = face[i / 2 + 2];
                    }
                    else
                    {
                        triBase = (triBase + count - 1) % count;
                        indices[idx++] = face[triBase];
                        indices[idx++] = face[(triBase + 1) % count];
                        indices[idx++] = face[i / 2 + 2];
                    }
                }
            }
        }

        public Mesh CalculateMesh()
        {
            var layout = new[]
            {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3)
        };

            Mesh mesh = new Mesh();
            mesh.SetVertexBufferParams(vertices.Length, layout);
            mesh.SetVertices(vertices);
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, true);
            mesh.RecalculateNormals();

            return mesh;
        }
    }

    public partial class Occluder : MonoBehaviour
    {
        public Mesh Mesh;
        public Vector3 relativePosition = Vector3.zero;
        public Quaternion relativeRotation = Quaternion.identity;
        public Vector3 relativeScale = Vector3.one;

        public enum OccluderType
        {
            Mesh,
            Volume
        }
        public OccluderType Type;

        public Vector3 LocalPosition
        {
            get { return transform.localToWorldMatrix.MultiplyPoint(relativePosition); }
            set { relativePosition = transform.worldToLocalMatrix.MultiplyPoint(value); }
        }
        public Quaternion LocalRotation
        {
            get { return Quaternion.Normalize(transform.rotation * relativeRotation); }
            set { relativeRotation = Quaternion.Normalize(Quaternion.Inverse(transform.rotation) * value); }
        }
        public Vector3 LocalScale
        {
            get { return Vector3.Scale(transform.localScale, relativeScale); }
            set
            {
                relativeScale = new Vector3(value.x / transform.localScale.x, value.y / transform.localScale.y, value.z / transform.localScale.z);
            }
        }

        public Matrix4x4 localTransform
        {
            get
            {
                return Matrix4x4.TRS(LocalPosition, LocalRotation, LocalScale);
            }
        }

        void Reset()
        {
            if (gameObject.TryGetComponent<MeshFilter>(out var meshFilter))
            {
                Mesh = meshFilter.sharedMesh;
            }

            relativePosition = Vector3.zero;
            relativeRotation = Quaternion.identity;
            relativeScale = Vector3.one;
        }

        // Update is called once per frame
        void Update()
        {
        }

        public void DebugRender()
        {
            if (Mesh == null)
                return;

            var material = new Material(Shader.Find("Shader Graphs/ShowOccluderMesh"));// GraphicsSettings.currentRenderPipeline.defaultMaterial;
            material.SetPass(0);
            Graphics.DrawMeshNow(Mesh, localTransform);
        }
    }
}

#endif
