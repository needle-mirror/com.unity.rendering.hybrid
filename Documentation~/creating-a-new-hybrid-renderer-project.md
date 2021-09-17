# Creating a new Hybrid Renderer project

## Hybrid Renderer

1. Create a new project. Depending on which render pipeline you want to use with Hybrid Renderer, the project should use a specific template:
	* For the Built-in Render Pipeline, use the **3D** template.
	* For the Universal Render Pipeline (URP), use the **Universal Render Pipeline** template.
	* For the High Definition Render Pipeline (HDRP), use the **High Definition RP** template.
3. Install the Hybrid Renderer package. Since this is an experimental package, later versions of Unity do not show it in the Package Manager window. The most consistent way to install this package for all versions of Unity is to use the [manifest.json](https://docs.unity3d.com/Manual/upm-manifestPrj.html).
	1. In the Project window, go to **Packages** and right-click in an empty space.
	2. Click **Show in Explorer** then, in the File Explorer window, open **Packages > manifest.json**.
	3. Add `"com.unity.rendering.hybrid": "*<package version>*"` to the list of dependencies where \<version number> is the version of the Hybrid Renderer Package you want to install. For example:<br/>`"com.unity.rendering.hybrid": "0.14.0-preview.27"`
	4. Installing the Hybrid Renderer package also installs all of its dependencies including the DOTS packages.
5. If you are using either URP or HDRP, make sure SRP Batcher is enabled in your Project's URP or HDRP Assets. Creating a Project from the URP or HDRP  template enables SRP Batcher automatically.
	* **URP**: Select the [URP Asset](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@latest?subfolder=/manual/universalrp-asset.html) and view it in the Inspector, go to **Advanced** and make sure **SRP Batcher** is enabled.
	* **HDRP**: Select the [HDRP Asset](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@latest?subfolder=/manual/index.html) and view it in the Inspector, enter [Debug Mode ](https://docs.unity3d.com/Manual/InspectorOptions.html)for the Inspector, and make sure **SRP Batcher** is enabled.
4. Hybrid Renderer does not support gamma space, so your Project must use linear color space, To do this:
   1. Go to **Edit > Project Settings > Player > Other Settings** and locate the **Color Space** property.
   2. Select **Linear** from the **Color Space** drop-down.
5. Hybrid Renderer is now installed. As of Hybrid Renderer version 0.14 and above, Hybrid Renderer V2 is the default.
