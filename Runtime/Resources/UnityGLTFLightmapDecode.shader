// Decodes a Unity baked lightmap (BC6H/float raw HDR, or RGBM-encoded LDR formats) and encodes
// it to LDR with the "Photopea curve": the sRGB transfer curve sampled at 17 knots (i/16) with
// linear interpolation between them. This matches what Photopea (and the reference manual
// HDR->PNG conversions) produce, and is deliberately DARKER than the exact sRGB formula for
// linear values below ~0.06 (chord undershoot in the sRGB toe, up to 16/255) — exporting with
// hardware sRGB conversion instead makes the web scene render visibly lighter than Unity in
// dark areas. Derivation: MetaCoach 531 lightmap fix, 2026-08-27 (HelperTools
// lightmap-tools/hdr2png.py carries the same curve for offline conversions).
// The render target must be LINEAR (no hardware gamma) — the shader output is already encoded.
Shader "Hidden/UnityGLTFLightmapDecode"
{
	Properties
	{
		_MainTex ("Lightmap", 2D) = "white" {}
		// Unity DecodeLightmap instructions: x = multiplier, y = exponent applied to alpha.
		_Decode ("Decode Instructions", Vector) = (1, 1, 0, 0)
		// 0 = raw HDR lightmap (values already linear), 1 = RGBM/dLDR encoded (use _Decode)
		_Mode ("Mode", Float) = 0
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

			sampler2D _MainTex;
			float4 _Decode;
			float _Mode;

			// sRGB transfer curve sampled at i/16, i = 0..16
			static const float _Knots[17] =
			{
				0.000000000,
				0.277304177,
				0.388572859,
				0.470214035,
				0.537098730,
				0.594790417,
				0.646076626,
				0.692583975,
				0.735356983,
				0.775112294,
				0.812366145,
				0.847504589,
				0.880825021,
				0.912562134,
				0.942904884,
				0.972007996,
				1.000000000
			};

			float encodeChannel(float x)
			{
				float t = saturate(x) * 16.0;
				float idx = min(floor(t), 15.0);
				return lerp(_Knots[(int)idx], _Knots[(int)idx + 1], t - idx);
			}

			float4 frag(v2f_img i) : SV_Target
			{
				float4 c = tex2D(_MainTex, i.uv);
				float3 lin = c.rgb;
				if (_Mode > 0.5)
					lin = c.rgb * (_Decode.x * pow(max(c.a, 0.0001), _Decode.y));
				return float4(encodeChannel(lin.r), encodeChannel(lin.g), encodeChannel(lin.b), 1.0);
			}
			ENDCG
		}
	}
	Fallback Off
}
