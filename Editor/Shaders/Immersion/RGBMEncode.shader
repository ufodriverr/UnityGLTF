Shader "Hidden/RGBMEncode"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _MaxRange ("Max Range", Float) = 8.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float _MaxRange;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                // Important: source must be sampled as linear data (not sRGB).
                float4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                real4 decodeInstructions = real4(LIGHTMAP_HDR_MULTIPLIER, LIGHTMAP_HDR_EXPONENT, 0.0, 0.0);
                c.rgb = DecodeLightmap(c, decodeInstructions); // Decode lightmap (if any)
                c.rgb = max(c.rgb, 0.0);
                
                float maxChannel = max(c.r, max(c.g, c.b));
                float m = saturate(maxChannel / max(_MaxRange, 1e-6));

                // Quantize multiplier to 8-bit and ensure non-zero when needed
                m = ceil(m * 255.0) / 255.0;
                if (m <= 0.0 && maxChannel > 0.0) m = 1.0 / 255.0;

                float3 rgb = saturate(c / (m * _MaxRange));

                // RGBM decode: hdr = packed.rgb * (packed.a * _MaxRange)
                return float4(rgb, m);
            }
            ENDHLSL
        }
    }
}