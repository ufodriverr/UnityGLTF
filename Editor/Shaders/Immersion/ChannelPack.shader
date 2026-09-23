// Generic channel packer for glTF ORM textures (R = occlusion, G = roughness, B = metallic).
// Each output channel is fed from one channel of one of two source textures, remapped
// linearly (min + c * (max - min)) and optionally inverted (smoothness -> roughness).
// A negative source-texture index makes the channel a constant (= min).
//
// Per output channel: _PackR / _PackG / _PackB = (sourceTexture 0|1|-1, channel 0..3, min, max)
// _Invert = (invertR, invertG, invertB, 0)
//
// Sources are sampled exactly as the Unity shader would sample them (the importer's sRGB flag
// applies), so the packed values match what Unity's material graph computed.
Shader "Hidden/IMMERSION_ChannelPack"
{
    Properties
    {
        _TexA ("Source A", 2D) = "white" {}
        _TexB ("Source B", 2D) = "white" {}
        _PackR ("Pack R (tex, channel, min, max)", Vector) = (-1, 0, 1, 1)
        _PackG ("Pack G (tex, channel, min, max)", Vector) = (-1, 0, 1, 1)
        _PackB ("Pack B (tex, channel, min, max)", Vector) = (-1, 0, 0, 0)
        _Invert ("Invert (r, g, b, 0)", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_TexA); SAMPLER(sampler_TexA);
            TEXTURE2D(_TexB); SAMPLER(sampler_TexB);
            float4 _PackR;
            float4 _PackG;
            float4 _PackB;
            float4 _Invert;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float Pick(float4 a, float4 b, float4 pack, float invert)
            {
                float4 src = pack.x < 0.5 ? a : b;
                float c = pack.y < 0.5 ? src.r : (pack.y < 1.5 ? src.g : (pack.y < 2.5 ? src.b : src.a));
                float v = pack.x < -0.5 ? pack.z : (pack.z + c * (pack.w - pack.z));
                v = saturate(v);
                return invert > 0.5 ? 1.0 - v : v;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float4 a = SAMPLE_TEXTURE2D(_TexA, sampler_TexA, i.uv);
                float4 b = SAMPLE_TEXTURE2D(_TexB, sampler_TexB, i.uv);
                return float4(
                    Pick(a, b, _PackR, _Invert.x),
                    Pick(a, b, _PackG, _Invert.y),
                    Pick(a, b, _PackB, _Invert.z),
                    1.0);
            }
            ENDHLSL
        }
    }
}
