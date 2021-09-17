# Hybrid Renderer versions

This package used to contain two versions of Hybrid Renderer, called V1 and V2. This is no longer the case,
and the package now contains only a single version of Hybrid Renderer (Hybrid Renderer V2) which is always enabled.

A future version of the package will contain a new version of Hybrid Renderer that will support all the features marked as "No" on this page.

## Feature support

The tables in this section show the render pipeline feature support of Hybrid Renderer.

### URP features

| **Feature**                     | **Supported by Hybrid Renderer** |
| ------------------------------- |----------------------------------|
| **Material property overrides** | Yes                              |
| **Built-in property overrides** | Yes                              |
| **Shader Graph**                | Yes                              |
| **Lit shader**                  | Yes                              |
| **Unlit shader**                | Yes                              |
| **RenderLayer**                 | Yes                              |
| **TransformParams**             | Yes                              |
| **DisableRendering**            | Yes                              |
| **Sun light**                   | Yes                              |
| **Point + spot lights**         | No                               |
| **Ambient probe**               | Yes                              |
| **Light probes**                | Yes                              |
| **Reflection probes**           | No                               |
| **Lightmaps**                   | Yes                              |
| **LOD crossfade**               | No                               |
| **Viewport shader override**    | No                               |
| **Transparencies (sorted)**     | Yes                              |
| **Occlusion culling (dynamic)** | Experimental                     |
| **Skinning / mesh deform**      | Experimental                     |

### HDRP features

| **Feature**                     | **Supported by Hybrid Renderer** |
| ------------------------------- |--------------|
| **Material property overrides** | Yes          |
| **Built-in property overrides** | Yes          |
| **Shader Graph**                | Yes          |
| **Lit shader**                  | Yes          |
| **Unlit shader**                | Yes          |
| **Decal shader**                | Yes          |
| **LayeredLit shader**           | Yes          |
| **RenderLayer**                 | Yes          |
| **TransformParams**             | Yes          |
| **DisableRendering**            | Yes          |
| **Motion blur**                 | Yes          |
| **Temporal AA**                 | Yes          |
| **Sun light**                   | Yes          |
| **Point + spot lights**         | Yes          |
| **Ambient probe**               | Yes          |
| **Light probes**                | Yes          |
| **Reflection probes**           | Yes          |
| **Lightmaps**                   | Yes          |
| **LOD crossfade**               | No           |
| **Viewport shader override**    | No           |
| **Transparencies (sorted)**     | Yes          |
| **Occlusion culling (dynamic)** | Experimental |
| **Skinning / mesh deform**      | Experimental |
