## Mesh Deformations
This page describes the functionality to deform meshes using skinning and blendshapes, similar to what [SkinnedMeshRenderer](https://docs.unity3d.com/Manual/class-SkinnedMeshRenderer.html) does. Generally, you want to use this in combination with DOTS Animation. Samples of setups and usage of the systems can be found in the  [DOTS Animation Samples](https://github.com/Unity-Technologies/Unity.Animation/blob/master/UnityAnimationHDRPExamples/README.md). 


## Disclaimer
This version is highly experimental. We want you to know it's there, we invite you to peek at it and consider what it might mean for you and the future of Unity. What we don't want you to do is to rely on it as if it's something ready for professional production.
- Not (yet!) for production use.
- Things will change.

## Setup
 SRP version 7.x.x or higher:
Create a ShaderGraph that will perform deformations
Either add the **Compute Deformation** or **Linear Blend Skinning** node to the shader graph.
Connect the position, normal and tangent outputs to the vertex position, normal and tangent slots in the master node. 
Create a material that uses the new shader graph
Enable GPU instancing checkbox on the material
Assign the material to the SkinnedMeshMeshRenderer
When rendered as Entity and if animated mesh deformations should be applied



### Vertex Shader Skinning
Skins the mesh on the GPU in the vertex shader. 
#### Features
- Linear blend skinning with **four influences per vertex**
- **no** blendshapes
#### Requirements
- Unity 2019.3b11 or newer (recommended)
- Hybrid Renderer 0.4.0 or higher (recommended)
- SRP version 7.x.x or higher (recommended)
- HDRP & URP support when used with Hybrid Renderer v2 (recommended)
- Only supports HDRP when used with Hybrid Renderer v1


### Compute Shader Deformation
Applies mesh deformations on the GPU using compute shaders. 
#### Features
- Linear blend skinning, supports up to 255 sparse influences per vertex 
- Supports sparse blendshapes
#### Requirements
- Add ‘ENABLE_COMPUTE_DEFORMATIONS’ define to ‘Scripting Define Symbols’ in ‘Edit>Project Settings>Player ’
- Unity 2020.1.0b6 or higher (recommended)
- Hybrid Renderer 0.5.0 or higher (recommended)
- SRP version 9.x.x or higher (recommended)
- With Hybrid Renderer v2 HDRP & URP is supported
- With Hybrid Renderer v1 only HDRP is supported

## Known Limitations
- Wire frame mode and other debug modes do not display mesh deformations.
- Render bounds are not resized or transformed based on the mesh deformations.
- No frustum or occlusion culling, everything present in the world is getting deformed.
- Visual glitches may appear on the first frame.
- Live link is still untested with many of the features.
- Deformed meshes can disappear or show in bindpose when rendered as GameObjects.
- Compute deformations performance will vary based on GPU.
