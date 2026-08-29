Shader "Hidden/NormalDecodeBlit"
{
    Properties
    {
        _MainTex ("Normal Map", 2D) = "bump" {}
        _FlipY ("Flip Green (Y)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "NormalDecodeBlit"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_ST;
            float _FlipY;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            Varyings Vert (Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 Frag (Varyings i) : SV_Target
            {
                // Sample the normal map as Unity expects it, then decode.
                // UnpackNormal handles common Unity normal encodings (DXT5nm/BC5) correctly.
                half4 packed = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                half3 n = UnpackNormal(packed); // returns tangent-space normal in [-1..1]

                // Optional: flip green for glTF/OpenGL vs DirectX convention mismatches.
                if (_FlipY > 0.5)
                    n.y = -n.y;

                // Pack to "viewable" RGB normal map.
                half3 rgb = n * 0.5h + 0.5h;
                return half4(rgb, 1.0h);
            }
            ENDHLSL
        }
    }
}