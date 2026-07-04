// Decodes a Unity baked lightmap (BC6H/float raw HDR, or RGBM-encoded LDR formats) to plain
// linear color, clamped to [0..1]. Used by the IMMERSION_lightmaps export plugin to write
// lightmaps as standard PNG files (the render target is sRGB, so hardware applies the gamma).
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

			float4 frag(v2f_img i) : SV_Target
			{
				float4 c = tex2D(_MainTex, i.uv);
				float3 lin = c.rgb;
				if (_Mode > 0.5)
					lin = c.rgb * (_Decode.x * pow(max(c.a, 0.0001), _Decode.y));
				return float4(saturate(lin), 1.0);
			}
			ENDCG
		}
	}
	Fallback Off
}
