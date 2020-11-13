Shader "Hidden/OcclusionDebug"
{
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
    }
        SubShader
    {

        Lighting Off
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
        Cull Off
        Fog { Mode Off }
        ZTest LEqual

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile __ WIRE_ON

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float z : TexCoord0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                o.vertex = v.vertex;
                o.z = 1 / v.vertex.w;
                return o;
            }

            struct FrameOut
            {
                fixed4 color : SV_Target;
                float depth : SV_Depth;
            };

            FrameOut frag(v2f i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                fixed4 col = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);

                #ifdef WIRE_ON
                    col.a = 1.0f;
                #endif

                float dx = ddx(i.z);
                float dy = ddy(i.z);
                col.rgb *= 0.00001 / (abs(dx) + abs(dy));

                FrameOut result;
                result.color = col;
                result.depth = i.z;
                return result;
            }
            ENDCG
        }
    }
}
