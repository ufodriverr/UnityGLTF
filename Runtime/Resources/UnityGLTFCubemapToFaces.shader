// Flattens a cubemap (reflection probe or baked skybox) into a horizontal 6x1 face strip
// (+X, -X, +Y, -Y, +Z, -Z), applying Unity's HDR decode instructions and clamping to LDR so
// the result can be written as a standard PNG (the render target is sRGB, so hardware applies
// the gamma).
//
// The per-face orientation follows the WebGL/three.js CubeTexture convention (face images
// stored top-down, GL cube space), including the X-axis flip between Unity's left-handed and
// glTF's right-handed coordinate system. The exported strip matches what the Immersion web
// editor's reflection loader expects (6 square faces, width = 6 x height).
Shader "Hidden/UnityGLTFCubemapToFaces"
{
	Properties
	{
		_CubeTex ("Cubemap", CUBE) = "" {}
		// Unity DecodeHDR instructions (probe.textureHDRDecodeValues): x = multiplier, y = exponent, w = alpha lerp
		_Decode ("HDR Decode Instructions", Vector) = (1, 1, 0, 0)
		_UseDecode ("Apply HDR Decode", Float) = 0
	}
	SubShader
	{
		Pass
		{
			ZTest Always Cull Off ZWrite Off

			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment frag
			#include "UnityCG.cginc"

			samplerCUBE _CubeTex;
			float4 _Decode;
			float _UseDecode;

			float4 frag(v2f_img i) : SV_Target
			{
				float fx = i.uv.x * 6.0;
				float face = floor(min(fx, 5.9999));
				float s = fx - face;
				// GL cube face images are stored top-down; the PNG top row is rt uv.y = 1
				float t = 1.0 - i.uv.y;
				float sp = s * 2.0 - 1.0;
				float tp = t * 2.0 - 1.0;

				// direction per GL cube map face convention (three.js CubeTexture order)
				float3 dir;
				if      (face < 0.5) dir = float3( 1.0, -tp,  -sp); // +X
				else if (face < 1.5) dir = float3(-1.0, -tp,   sp); // -X
				else if (face < 2.5) dir = float3( sp,   1.0,  tp); // +Y
				else if (face < 3.5) dir = float3( sp,  -1.0, -tp); // -Y
				else if (face < 4.5) dir = float3( sp,  -tp,   1.0); // +Z
				else                 dir = float3(-sp,  -tp,  -1.0); // -Z

				// glTF/three.js are right-handed; Unity is left-handed (X is mirrored)
				dir.x = -dir.x;

				float4 c = texCUBE(_CubeTex, dir);
				float3 lin = c.rgb;
				if (_UseDecode > 0.5)
				{
					float alpha = _Decode.w * (c.a - 1.0) + 1.0;
					lin = c.rgb * (_Decode.x * pow(max(alpha, 0.0001), _Decode.y));
				}
				return float4(saturate(lin), 1.0);
			}
			ENDCG
		}
	}
	Fallback Off
}
