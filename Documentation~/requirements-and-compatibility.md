# Requirements and compatibility

This page contains information on system requirements and compatibility of the Hybrid Renderer package.

### Hybrid Renderer render pipeline compatibility

The following table shows the compatibility of Hybrid Renderer with different render pipelines.

| **Render pipeline**                        | **Compatibility**                                         |
| ------------------------------------------ | --------------------------------------------------------- |
| **Built-in Render Pipeline**               | Not supported                                             |
| **High Definition Render Pipeline (HDRP)** | HDRP version 9.0.0 and above, with Unity 2020.1 and above |
| **Universal Render Pipeline (URP)**        | URP version 9.0.0 and above, with Unity 2020.1 and above  |

 

## Unity Player system requirements

This section describes the Hybrid Renderer package’s target platform requirements. For platforms or use cases not covered in this section, general system requirements for the Unity Player apply.

For more information, see [System requirements for Unity](https://docs.unity3d.com/Manual/system-requirements.html).

Currently, Hybrid Renderer does not support desktop OpenGL or GLES. For Android and Linux, you should use Vulkan.
However, be aware that the Vulkan drivers on many older Android devices are in a bad shape and will never be upgraded.
This limits the platform coverage Hybrid Renderer can currently offer on Android devices. OpenGL and GLES3.1 support are planned for a future version of Hybrid Renderer.

Hybrid Renderer is not yet validated on mobile and console platforms. The main focus of the team is to improve the editor platforms, support the remaining URP and HDRP features, and continue to improve the stability, performance, test coverage, and documentation to make Hybrid Renderer production ready. For mobile and console platforms, the aim is to gradually improve test coverage.

Hybrid Renderer is not yet tested or supported on [XR](https://docs.unity3d.com/Manual/XR.html) devices. XR support is intended in a later version.

Hybrid Renderer does not currently support ray-tracing (DXR).

## DOTS feature compatibility

Hybrid Renderer does not support multiple DOTS [Worlds](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/world.html). Limited support for multiple Worlds is intended in a later version. The current plan is to add support for creating multiple rendering systems, one per renderable World, but then only have one World active for rendering at once.
