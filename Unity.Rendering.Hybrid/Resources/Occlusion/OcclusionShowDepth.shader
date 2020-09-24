Shader "Hidden/OcclusionShowDepth"
{
    Properties
    {
        _Depth("Depth", 2D) = "white" {}
        _Test("Test", 2D) = "white" {}
        _Bounds("Bounds", 2D) = "white" {}
    }

    SubShader
    {
        Lighting Off
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Fog { Mode Off }
        ZTest Always

        Pass
        {
            //Name "ForwardOnly"
            //Tags { "LightMode" = "ForwardOnly" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            //Default is depth only
            #pragma multi_compile __ DEPTH_WITH_TEST BOUNDS_ONLY

            #include "UnityCG.cginc"

            sampler2D _Depth;
            sampler2D _Test;
            sampler2D _Bounds;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                //UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                //UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            v2f vert(appdata v)
            {
                v2f o;
                //UNITY_SETUP_INSTANCE_ID(v);
                //UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.vertex = v.vertex;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            { 
            #if BOUNDS_ONLY
                fixed4 col = fixed4(tex2D(_Bounds, i.uv).rrr,1);
                if (col.r < 0.5f) col.a = 0;
            #else
                fixed4 col = fixed4(tex2D(_Depth, i.uv).rrr,1) * fixed4(-1.0f, -1.0f, -1.0f, 1.0f);
                #if  DEPTH_WITH_TEST
                    col.r = max(col.r, tex2D(_Test, i.uv).r);
                #endif 
            #endif

                return col;
            }
            ENDCG
        }
    }
}
