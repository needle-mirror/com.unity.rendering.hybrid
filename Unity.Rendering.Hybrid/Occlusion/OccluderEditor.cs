#if UNITY_EDITOR && ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Unity.Rendering.Occlusion
{
    class OccluderVolumeEditingContext
    {
        public OccluderVolumeEditingContext(Occluder occluder)
        {
            Occluder = occluder;
            VertexSelection = new HashSet<int>();
        }

        public Occluder Occluder
        {
            get { return occluder; }
            set
            {
                occluder = value;
                Dragging = -1;
                ComputeEdges();
                ComputeNormals();
                ComputeFaces();
            }
        }

        Occluder occluder;

        public int Dragging;
        public HashSet<int> VertexSelection;

        public struct Edge
        {
            public Edge(int a, int b)
            {
                v = new int2(a, b);
                t = -1;
            }

            public int GetOpposite(int tri)
            {
                if (t[0] == tri) return t[1];
                if (t[1] == tri) return t[0];
                return -1;
            }

            public int2 v; // endpoints
            public int2 t; // tris
        }
        public struct Triangle
        {
            public Triangle(int a, int b, int c)
            {
                v = new int3(a, b, c);
                edge = -1;
                normal = Vector3.zero;
                isFace = false;
            }

            public void AddEdge(int idx)
            {
                if (edge[0] == -1) edge[0] = idx;
                else if (edge[1] == -1) edge[1] = idx;
                else if (edge[2] == -1) edge[2] = idx;
            }

            public void ComputeNormal(Vector3[] verts)
            {
                normal = Vector3.Normalize(Vector3.Cross(verts[v[1]] - verts[v[0]], verts[v[2]] - verts[v[0]]));
            }

            public int3 v;
            public int3 edge;
            public float3 normal;
            public bool isFace;
        }

        public struct Face
        {
            public int[] vertices;
            public int[] tris;
            public float3 normal;
            public float3 centroid;
        }

        public Triangle[] tris;
        public Edge[] edges;
        public List<Face> faces;

        public void ComputeNormals()
        {
            var mesh = occluder.Mesh;
            var verts = mesh.vertices;

            int numTris = tris.Length;
            for (int i = 0; i < numTris; i++)
            {
                var tri = tris[i];
                tri.ComputeNormal(verts);
                tris[i] = tri;
            }
        }

        public void ComputeEdges()
        {
            Dictionary<int, Edge> edgeMap = new Dictionary<int, Edge>();
            var mesh = occluder.Mesh;

            var verts = mesh.vertices;
            var indices = mesh.GetIndices(0);

            tris = new Triangle[indices.Length / 3];
            for (int i = 0; i < indices.Length; i += 3)
            {
                tris[i / 3] = new Triangle(indices[i], indices[i + 1], indices[i + 2]);
            }

            for (int i = 0; i < indices.Length; i += 3)
            {
                var tri = tris[i / 3];
                for (int k = 0; k < 3; k++)
                {
                    int a = indices[i + k];
                    int b = indices[i + (k + 1) % 3];
                    int hash = Mathf.Min(a, b) * verts.Length + Mathf.Max(a, b);

                    if (edgeMap.TryGetValue(hash, out var edge))
                    {
                        // both endpoints and one triangle have been filled
                        edge.t[1] = i / 3;
                        edgeMap[hash] = edge;
                    }
                    else
                    {
                        edge = new Edge(a, b);
                        edge.t[0] = i / 3;
                        edgeMap[hash] = edge;
                    }
                }
            }

            edges = new Edge[edgeMap.Values.Count];
            edgeMap.Values.CopyTo(edges, 0);

            for (int i = 0; i < edges.Length; i++)
            {
                tris[edges[i].t[0]].AddEdge(i);
                tris[edges[i].t[1]].AddEdge(i);
            }
        }

        public void ComputeFaces()
        {
            faces = new List<Face>();
            for (int i = 0; i < tris.Length; i++)
            {
                if (!tris[i].isFace)
                {
                    faces.Add(GetFace(i));
                }
            }
        }

        Face GetFace(int startTri)
        {
            List<int> faceTris;
            float3 avgNormal;

            ExtractCoplanar(startTri, out faceTris, out avgNormal);

            List<int> edgeLoop = ExtractExteriorEdges(faceTris);

            int edgeCount = edgeLoop.Count;
            float3 center = 0;

            var verts = occluder.Mesh.vertices;
            var indices = occluder.Mesh.GetIndices(0);

            // compute the centroid and the vertex set of the edge loop
            var uniqueVertices = new HashSet<int>();
            foreach (var e in edgeLoop)
            {
                center += (float3)verts[edges[e].v[0]] + (float3)verts[edges[e].v[1]];
                uniqueVertices.Add(edges[e].v[0]);
                uniqueVertices.Add(edges[e].v[1]);
            }
            center /= edgeLoop.Count * 2;

            float3 u = math.normalize((float3)verts[edges[edgeLoop[0]].v[0]] - center);
            float3 v = math.normalize(math.cross(avgNormal, u));

            // sort the vertex loop.. not remotely the most efficient way to do this,
            // but we don't expect huge faces
            edgeLoop.Sort((a, b) =>
            {
                var edge1 = edges[a];
                var edge2 = edges[b];
                float alpha1 = EdgeAngle((float3)verts[edge1.v[0]] - center, (float3)verts[edge1.v[1]] - center, u, v);
                float alpha2 = EdgeAngle((float3)verts[edge2.v[0]] - center, (float3)verts[edge2.v[1]] - center, u, v);
                return alpha2.CompareTo(alpha1);
            });

            // verify edge loop integrity
            int loopSize = edgeLoop.Count;
            VerifyEdgeIntegrity(edgeLoop);

            var face = new Face();
            face.vertices = new int[loopSize];
            uniqueVertices.CopyTo(face.vertices, 0);
            Array.Sort(face.vertices, (a, b) =>
            {
                float alpha1 = math.atan2(math.dot(verts[a], u), math.dot(verts[a], v));
                float alpha2 = math.atan2(math.dot(verts[b], u), math.dot(verts[b], v));
                return alpha2.CompareTo(alpha1);
            });
            face.tris = faceTris.ToArray();
            face.normal = avgNormal;
            face.centroid = center;
            return face;
        }

        private void ExtractCoplanar(int startTri, out List<int> faceTris, out float3 avgNormal)
        {
            faceTris = new List<int>();
            var visited = new bool[tris.Length];

            var stack = new Stack<int>();
            stack.Push(startTri);
            var normal = tris[startTri].normal;

            avgNormal = 0;
            while (stack.Count > 0)
            {
                var currentTri = stack.Pop();

                if (tris[currentTri].isFace || Vector3.Dot(normal, tris[currentTri].normal) < 0.9999f)
                {
                    continue;
                }

                faceTris.Add(currentTri);
                tris[currentTri].isFace = true;
                avgNormal += normal;

                if (!visited[currentTri])
                {
                    visited[currentTri] = true;
                    var tri = tris[currentTri];

                    for (int i = 0; i < 3; i++)
                    {
                        int opp = edges[tri.edge[i]].GetOpposite(currentTri);
                        if (!visited[opp])
                        {
                            stack.Push(opp);
                        }
                    }
                }
            }
            avgNormal = math.normalize(avgNormal);
        }

        private void VerifyEdgeIntegrity(List<int> edgeLoop)
        {
            int loopSize = edgeLoop.Count;
            for (int i = 0; i < loopSize; i++)
            {
                var e1 = edges[edgeLoop[i]];
                var e2 = edges[edgeLoop[(i + 1) % loopSize]];
                Debug.Assert(math.any(e1.v.xy == e2.v.xy) || math.any(e1.v.xy == e2.v.yx), "Bad edge loop found!");
            }
        }

        private List<int> ExtractExteriorEdges(List<int> faceTris)
        {
            var adjCount = new Dictionary<int, int>();
            foreach (var tri in faceTris)
            {
                for (int i = 0; i < 3; i++)
                {
                    var idx = tris[tri].edge[i];
                    adjCount.TryGetValue(idx, out var count);
                    adjCount[idx] = count + 1;
                }
            }

            List<int> edgeLoop = new List<int>();
            foreach (var count in adjCount)
            {
                if (count.Value == 1)
                    edgeLoop.Add(count.Key);
            }

            return edgeLoop;
        }

        float EdgeAngle(float3 a, float3 b, float3 u, float3 v)
        {
            var c = (a + b) / 2;
            return math.atan2(math.dot(c, u), math.dot(c, v));
        }

        internal void UpdateFaces()
        {
            var verts = occluder.Mesh.vertices;
            for (int i = 0; i < faces.Count; i++)
            {
                var face = faces[i];
                face.centroid = 0;
                for (int k = 0; k < face.vertices.Length; k++)
                {
                    face.centroid += (float3)verts[face.vertices[k]];
                }
                face.centroid /= face.vertices.Length;

                face.normal = 0;
                for (int k = 0; k < face.tris.Length; k++)
                {
                    face.normal += tris[face.tris[k]].normal;
                }
                face.normal = math.normalize(face.normal);

                faces[i] = face;
            }

        }
    }

    [EditorTool("Edit Occluder", typeof(Occluder))]
    class OccluderTool : EditorTool
    {
        class Contents
        {

            public GUIContent toolbarButton = new GUIContent(
                        EditorGUIUtility.IconContent("EditCollider").image,
                        EditorGUIUtility.TrTextContent("Edit occluder").text
                    );

            public GUIContent editMeshIcon = EditorGUIUtility.IconContent("Grid.MoveTool");
            public GUIContent editVertexIcon = EditorGUIUtility.IconContent("RectTool@2x");
            public GUIContent editFaceIcon = EditorGUIUtility.IconContent("PreMatCube@2x");
        }
        static Contents s_Contents;
        static Contents GUIContents
        {
            get
            {
                if (s_Contents == null)
                    s_Contents = new Contents();
                return s_Contents;
            }
        }

        public override GUIContent toolbarIcon
        {
            get { return GUIContents.toolbarButton; }
        }



        enum EditMode
        {
            Mesh,
            Vertex,
            Face
        }
        EditMode m_editMode = EditMode.Mesh;
        bool marqueeSelection = false;
        bool marqueeSelectionEnded = true;
        Vector2 marqueeStart;
        Vector2 marqueeEnd;


        public override void OnToolGUI(EditorWindow window)
        {
            if (s_Contents == null)
                s_Contents = new Contents();

            Occluder occluder = (Occluder)target;

            switch (occluder.Type)
            {
                case Occluder.OccluderType.Mesh:
                    EditMesh(occluder);
                    break;

                case Occluder.OccluderType.Volume:
                    int background = GUIUtility.GetControlID(FocusType.Passive);
                    HandleUtility.AddDefaultControl(background);

                    Handles.BeginGUI();
                    switch (m_editMode)
                    {
                        case EditMode.Mesh:
                            GUILayout.BeginArea(new Rect { x = 10, y = 10, width = 250, height = 85 }, "Mesh mode", GUI.skin.window);
                            GUILayout.Label("Use shift to align the transform gizmo");
                            GUILayout.Label("to the view");
                            break;
                        case EditMode.Face:
                            GUILayout.BeginArea(new Rect { x = 10, y = 10, width = 250, height = 85 }, "Face mode", GUI.skin.window);
                            break;
                        case EditMode.Vertex:
                            GUILayout.BeginArea(new Rect { x = 10, y = 10, width = 250, height = 85 }, "Vertex mode", GUI.skin.window);
                            GUILayout.Label("Use shift to (de)select multiple vertices.");
                            GUILayout.Label("Click and drag for marquee selection.");
                            break;
                    }
                    GUILayout.EndArea();

                    Rect p = window.position;
                    int toolBarWidth = 110;
                    GUILayout.BeginArea(new Rect { x = (p.width - toolBarWidth) / 2, y = 10, width = toolBarWidth, height = 80 });
                    using (new GUILayout.HorizontalScope())
                    {
                        bool editMesh = (m_editMode == EditMode.Mesh);
                        bool editVerts = (m_editMode == EditMode.Vertex);
                        bool editFaces = (m_editMode == EditMode.Face);

                        var newEditMesh = !GUILayout.Toggle(!editMesh, GUIContents.editMeshIcon, "Button");
                        var newEditVerts = !GUILayout.Toggle(!editVerts, GUIContents.editVertexIcon, "Button");
                        var newEditFaces = !GUILayout.Toggle(!editFaces, GUIContents.editFaceIcon, "Button");

                        if (newEditMesh && !editMesh)
                        {
                            m_editMode = EditMode.Mesh;
                        }
                        else if (newEditVerts && !editVerts)
                        {
                            m_editMode = EditMode.Vertex;
                        }
                        else if (newEditFaces && !editFaces)
                        {
                            m_editMode = EditMode.Face;
                        }
                    }
                    GUILayout.EndArea();
                    Handles.EndGUI();

                    if (m_editMode == EditMode.Vertex)
                    {
                        UpdateSelection();
                    }

                    EditVolume(occluder);
                    break;
            }
        }

        private void UpdateSelection()
        {
            if (GUIUtility.hotControl == 0)
            {
                // a click on the default control means deselect everything, UNLESS the add to selection key is held
                if (Event.current.modifiers != EventModifiers.Shift && Event.current.type == EventType.MouseUp && !marqueeSelection)
                {
                    ((Occluder)target).Context.VertexSelection.Clear();
                }

                // did we just start dragging?
                if (!marqueeSelection && Event.current.type == EventType.MouseDrag && Event.current.button == 0)
                {
                    marqueeSelection = true;
                    marqueeSelectionEnded = false;
                    marqueeStart = marqueeEnd = Event.current.mousePosition;
                }

                // did we just stop dragging?
                if (marqueeSelection && Event.current.type == EventType.MouseUp)
                {
                    // select everything in the (marqueeStart,marqueeEnd) rectangle
                    marqueeSelection = false;
                    marqueeSelectionEnded = true;
                }
            }

            if (marqueeSelection)
            {
                marqueeEnd = Event.current.mousePosition;

                var corners = new Vector3[4];
                corners[0] = GUIToWorld(marqueeStart);
                corners[1] = GUIToWorld(new Vector2(marqueeStart.x, marqueeEnd.y));
                corners[2] = GUIToWorld(marqueeEnd);
                corners[3] = GUIToWorld(new Vector2(marqueeEnd.x, marqueeStart.y));

                Handles.DrawDottedLine(corners[0], corners[1], 1.0f);
                Handles.DrawDottedLine(corners[1], corners[2], 1.0f);
                Handles.DrawDottedLine(corners[2], corners[3], 1.0f);
                Handles.DrawDottedLine(corners[3], corners[0], 1.0f);
            }
        }

        private Vector3 GUIToWorld(Vector3 pos)
        {
            Ray startRay = HandleUtility.GUIPointToWorldRay(pos);
            float t = (1 - startRay.origin.z) / startRay.direction.z;
            return startRay.origin + startRay.direction;
        }

        internal void EditMesh(Occluder occluder)
        {
            EditorGUI.BeginChangeCheck();

            occluder.DebugRender();

            var pos = occluder.LocalPosition;
            var rot = occluder.LocalRotation;
            var scale = occluder.LocalScale;
            Handles.TransformHandle(ref pos, ref rot, ref scale);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(occluder, "Modify occluder transform");
                PrefabUtility.RecordPrefabInstancePropertyModifications(occluder);

                occluder.LocalPosition = pos;
                occluder.LocalRotation = rot;
                occluder.LocalScale = scale;
            }
        }

        internal void EditVolume(Occluder occluder)
        {
            if (occluder.Mesh == null)
            {
                return;
            }

            var ctx = occluder.Context;
            ctx.ComputeEdges();
            ctx.ComputeNormals();

            if (ctx.Dragging <= 0)
            {
                //ctx.ComputeFaces();
            }
            else
            {
                ctx.UpdateFaces();
            }

            int currentFaceId = ctx.Dragging;
            ctx.Dragging = -1;
            occluder.DebugRender();

            var mesh = occluder.Mesh;

            var verts = mesh.vertices;
            var inverse = occluder.localTransform.inverse;

            // Render the back faces first, so that the hidden edges appear behind the handles
            RenderWireframe(ctx, occluder, mesh, verts, false);

            EditorGUI.BeginChangeCheck();

            var transform = occluder.localTransform;

            if (m_editMode == EditMode.Vertex)
            {
                EditVertices(ctx, mesh, verts, inverse, transform);
            }

            if (m_editMode == EditMode.Face)
            {
                EditFaces(ctx, currentFaceId, verts, inverse, transform);
            }

            // Render the front faces
            RenderWireframe(ctx, occluder, mesh, verts, true);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RegisterCompleteObjectUndo(occluder.Mesh, "Edit occluder volume");
                //            Undo.RecordObject(occluder.Mesh, "Edit occluder vertices");

                occluder.Mesh.SetVertices(verts);
                occluder.Mesh.RecalculateNormals();
                occluder.Mesh.RecalculateBounds();
                occluder.Mesh.MarkModified();

                PrefabUtility.RecordPrefabInstancePropertyModifications(occluder.Mesh);
            }

            if (m_editMode == EditMode.Mesh)
            {
                EditorGUI.BeginChangeCheck();
                var pos = occluder.LocalPosition;
                var rot = occluder.LocalRotation;
                var scale = occluder.LocalScale;

                Handles.TransformHandle(ref pos, ref rot, ref scale);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(occluder, "Modify occluder transform");

                    occluder.LocalPosition = pos;
                    occluder.LocalRotation = rot;
                    occluder.LocalScale = scale;

                    PrefabUtility.RecordPrefabInstancePropertyModifications(occluder);
                }
            }
        }

        private static void EditFaces(OccluderVolumeEditingContext ctx, int currentFaceId, Vector3[] verts, Matrix4x4 inverse, Matrix4x4 transform)
        {
            foreach (var face in ctx.faces)
            {
                var id = GUIUtility.GetControlID(FocusType.Passive);

                var centroid = transform.MultiplyPoint(face.centroid);
                var normal = Vector3.Normalize(transform.MultiplyVector(face.normal));

                if (currentFaceId != id && Vector3.Dot(normal, Camera.current.transform.position - centroid) < 0)
                {
                    continue;
                }

                float size = HandleUtility.GetHandleSize(centroid) * 0.5f;
                Vector3 snap = Vector3.one * 0.5f;

                Handles.color = Color.yellow;

                // draw a small X at the arrow origin to make it easier to see where it starts
                var u = Vector3.Cross(normal, transform.GetColumn(1));
                if (Vector3.SqrMagnitude(u) < 0.1f)
                    u = Vector3.Cross(normal, transform.GetColumn(0));
                u = Vector3.Normalize(u);
                var v = Vector3.Normalize(Vector3.Cross(normal, u));

                Handles.DrawLine(centroid - (u + v) * 0.1f, centroid + (u + v) * 0.1f);
                Handles.DrawLine(centroid - (u - v) * 0.1f, centroid + (u - v) * 0.1f);
                Handles.CircleHandleCap(-1, centroid, Quaternion.LookRotation(normal, u), 0.1f * Mathf.Sqrt(2), EventType.Repaint);

                var newCentroid = Handles.Slider(id, centroid, normal, size * 1.5f, Handles.ArrowHandleCap, 0.5f);

                if (GUIUtility.hotControl == id)
                {
                    ctx.Dragging = id;
                }

                newCentroid = centroid + math.dot(newCentroid - centroid, normal) * normal;
                var dn = inverse.MultiplyVector(newCentroid - centroid);

                for (int i = 0; i < face.vertices.Length; i++)
                {
                    verts[face.vertices[i]] += dn;
                }
            }
        }

        private void EditVertices(OccluderVolumeEditingContext ctx, Mesh mesh, Vector3[] verts, Matrix4x4 inverse, Matrix4x4 transform)
        {
            var transformed = new Vector3[mesh.vertexCount];

            var marqueeSize = (marqueeEnd - marqueeStart);
            marqueeSize.Set(Mathf.Abs(marqueeSize.x), Mathf.Abs(marqueeSize.y));

            Rect marqueeRect = new Rect
            {
                x = Mathf.Min(marqueeStart.x, marqueeEnd.x),
                y = Mathf.Min(marqueeStart.y, marqueeEnd.y),
                width = marqueeSize.x,
                height = marqueeSize.y
            };

            for (int i = 0; i < mesh.vertexCount; i++)
            {
                var p = transform.MultiplyPoint(verts[i]);
                transformed[i] = p;
            }

            if (marqueeSelectionEnded)
            {
                marqueeSelectionEnded = false;

                for (int i = 0; i < mesh.vertexCount; i++)
                {
                    var screenPoint = HandleUtility.WorldToGUIPoint(transformed[i]);
                    if (marqueeRect.Contains(screenPoint))
                    {
                        ctx.VertexSelection.Add(i);
                    }
                }
            }

            for (int i = 0; i < mesh.vertexCount; i++)
            {
                var p = transformed[i];

                float size = HandleUtility.GetHandleSize(p) * 0.5f;
                Vector3 snap = Vector3.one * 0.5f;

                bool isSelected = ctx.VertexSelection.Contains(i);
                Handles.color = isSelected ? Handles.selectedColor : Color.grey;

                if (marqueeSelection)
                {
                    var screenPoint = HandleUtility.WorldToGUIPoint(p);
                    if (marqueeRect.Contains(screenPoint))
                    {
                        Handles.color = Handles.preselectionColor;
                    }
                }

                if (isSelected)
                    size *= 1.2f;

                if (Handles.Button(p, Quaternion.identity, 0.5f * size, size, Handles.SphereHandleCap))
                {
                    if (Event.current.modifiers == EventModifiers.Shift)
                    {
                        if (isSelected)
                        {
                            // deselect in a multiselection
                            ctx.VertexSelection.Remove(i);
                        }
                        else
                        {
                            ctx.VertexSelection.Add(i);
                        }
                    }
                    else
                    {
                        ctx.VertexSelection.Clear();
                        if (!isSelected)
                        {
                            ctx.VertexSelection.Add(i);
                        }
                    }
                }

                verts[i] = inverse.MultiplyPoint(p);
            }

            var selectedVertexCount = ctx.VertexSelection.Count;
            if (selectedVertexCount > 0)
            {
                var center = Vector3.zero;
                foreach (var v in ctx.VertexSelection)
                {
                    center += transformed[v];
                }
                center /= selectedVertexCount;

                float size = HandleUtility.GetHandleSize(center) * 0.5f;
                Vector3 snap = Vector3.one * 0.5f;

                foreach (var v in ctx.VertexSelection)
                {
                    var p = transformed[v];
                    Handles.color = Color.magenta;
                    Handles.DrawDottedLine(p, center, 1);
                }


                var delta = Vector3.zero;

                foreach (var v in ctx.VertexSelection)
                {
                    var p = transformed[v];

                    Handles.color = Color.red;
                    var newX = Handles.Slider(p, transform.GetColumn(0), size * 1.5f, Handles.ArrowHandleCap, 0.5f);
                    Handles.color = Color.green;
                    var newY = Handles.Slider(p, transform.GetColumn(1), size * 1.5f, Handles.ArrowHandleCap, 0.5f);
                    Handles.color = Color.blue;
                    var newZ = Handles.Slider(p, transform.GetColumn(2), size * 1.5f, Handles.ArrowHandleCap, 0.5f);

                    delta += newX + newY + newZ - p * 3;
                }

                delta = inverse.MultiplyVector(delta);
                foreach (var v in ctx.VertexSelection)
                {
                    verts[v] += delta;
                }
            }
        }

        private static void RenderWireframe(OccluderVolumeEditingContext ctx, Occluder occluder, Mesh mesh, Vector3[] verts, bool frontFacing)
        {
            var transform = occluder.localTransform;
            foreach (var face in ctx.faces)
            {
                var p = transform.MultiplyPoint(face.centroid);

                if (Vector3.Dot(transform.MultiplyVector(face.normal), Camera.current.transform.position - p) < 0)
                {
                    if (!frontFacing)
                    {
                        Handles.color = Color.white;
                        for (int i = 0; i < face.vertices.Length; i++)
                        {
                            var a = verts[face.vertices[i]];
                            var b = verts[face.vertices[(i + 1) % face.vertices.Length]];
                            Handles.DrawDottedLine(transform.MultiplyPoint(a), transform.MultiplyPoint(b), 2);
                        }
                    }
                }
                else if (frontFacing)
                {
                    Handles.color = Color.white;
                    for (int i = 0; i < face.vertices.Length; i++)
                    {
                        var a = transform.MultiplyPoint(verts[face.vertices[i]]);
                        var b = transform.MultiplyPoint(verts[face.vertices[(i + 1) % face.vertices.Length]]);
                        Handles.DrawBezier(a, b, a, b, Color.white, null, 6);
                    }
                }
            }

            /*        foreach (var edge in ctx.edges)
                    {
                        var t1 = ctx.tris[edge.t[0]];
                        var t2 = ctx.tris[edge.t[1]];

                        if (Vector3.Dot(t1.normal, t2.normal) > 0.99f)
                        {
                            continue;
                        }

                        var n1 = occluder.localTransform.MultiplyVector(t1.normal);
                        var n2 = occluder.localTransform.MultiplyVector(t2.normal);

                        var a = occluder.localTransform.MultiplyPoint(verts[edge.v[0]]);
                        var b = occluder.localTransform.MultiplyPoint(verts[edge.v[1]]);
                        var d = Camera.current.transform.position - a;

                        if (Vector3.Dot(n1, d) < 0 && Vector3.Dot(n2, d) < 0)
                        {
                            if (!frontFacing)
                            {
                                Handles.color = Color.white;// grey;
                                Handles.DrawDottedLine(a, b, 2);
                            }
                        }
                        else if (frontFacing)
                        {
                            Handles.DrawBezier(a, b, a, b, Color.white, null, 6);
                        }
                    }*/
        }
    }

    public partial class Occluder
    {
        internal bool m_Editing = false;
        internal int m_PrismSides = 4;
        internal OccluderVolumeEditingContext m_context;

        internal OccluderVolumeEditingContext Context
        {
            get
            {
                if (m_context == null)
                    m_context = new OccluderVolumeEditingContext(this);
                return m_context;
            }
        }

        private void OnDrawGizmos()
        {
            DrawGizmos(false);
        }


        private void OnDrawGizmosSelected()
        {
            DrawGizmos(true);
        }

        private void DrawGizmos(bool selected)
        {
            if (Mesh == null)
            {
                return;
            }

            if (gameObject.TryGetComponent<MeshRenderer>(out var renderer))
            {
                //return;
            }

            if (Mesh.vertexCount == 0)
                return;

            Gizmos.color = selected ? Color.yellow : Color.white;
            Gizmos.DrawWireMesh(Mesh, LocalPosition, LocalRotation, LocalScale);
        }
    }
}
#endif

