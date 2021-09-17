# Material overrides using C#

Hybrid Renderer V2 supports per-entity overrides of various HDRP and URP material properties as well as overrides for custom Shader Graphs. You can write C#/Burst code to setup and animate material override values at runtime. For more information on Material overrides, see Material overrides.

# Built-in Material overrides

Hybrid Renderer contains a built-in library of IComponentData components that you can add to your entities to override their material properties.

**Supported HDRP Material overrides:**

- AlphaCutoff
- AORemapMax
- AORemapMin
- BaseColor
- DetailAlbedoScale
- DetailNormalScale
- DetailSmoothnessScale
- DiffusionProfileHash
- EmissiveColor
- Metallic
- Smoothness
- SmoothnessRemapMax
- SmoothnessRemapMin
- SpecularColor
- Thickness
- ThicknessRemap
- UnlitColor (HDRP/Unlit)

**Supported URP Material overrides:**

- BaseColor
- BumpScale
- Cutoff
- EmissionColor
- Metallic
- OcclusionStrength
- Smoothness
- SpecColor

If you want to override a built-in HDRP or URP property not listed here, you can do that with custom Shader Graph Material overrides.

## Custom Shader Graph Material overrides

You can create your own custom Shader Graph properties, and expose them to DOTS as IComponentData. This allows you to write C#/Burst code to setup and animate your own shader inputs. To do this, see the following steps:

### Shader Graph Asset

1. Select your Shader Graph custom property and view it in the **Graph Inspector**.
2. Open the **Node Settings** tab.
3. Next, the method changes depending on the Unity version:
   * Unity 2020.1: enable **Hybrid Instanced (experimental)**.<br/>![](images/HybridInstancingProperty.png)
   * From Unity 2020.2: Enable **Override Property Declaration** then set **Shader Declaration** to **Hybrid Per Instance**.<br/>![](images/HybridInstancingProperty2020-2.png)



### IComponentData

For the DOTS IComponentData struct, use the `MaterialProperty` Attribute, passing in the **Reference** and type for the Shader Graph property. For example, the IComponentData for the color (float4) property in the above step would be:

```
[MaterialProperty("_Color", MaterialPropertyFormat.Float4)]
public struct MyOwnColor : IComponentData
{
   public float4 Value;
}
```

Ensure that the *Reference* name in Shader Graph and the string name in MaterialProperty attribute match exactly. The type declared in the MaterialPropertyFormat should also be compatible with both the Shader Graph and the struct data layout. If the binary size doesn't match, you will see an error message in the console window.

### Burst C# system

Now you can write Burst C# system to animate your Material property:

```
class AnimateMyOwnColorSystem : SystemBase
{
   protected override void OnUpdate()
  {
       Entities.ForEach((ref MyOwnColor color, in MyAnimationTime t) =>
          {
               color.Value = new float4(
                   math.cos(t.Value + 1.0f),
                   math.cos(t.Value + 2.0f),
                   math.cos(t.Value + 3.0f),
                   1.0f);
          })
          .Schedule();
  }
}
```

**Important:** You need to create a matching IComponentData struct (described above) for every custom Shader Graph property that has **Hybrid Instanced (experimental)** enabled. If you fail to do so, Hybrid Renderer will not fill these properties: Hybrid Renderer V1 leaves the data uninitialized (flickering), and Hybrid Renderer V2 zero fills the data.