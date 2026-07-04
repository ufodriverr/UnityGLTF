// Flattens a cubemap (reflection probe or baked skybox) into an equirectangular panorama,
// applying Unity's HDR decode instructions and clamping to LDR so the result can be written
// as a standard PNG (the render target is sRGB, so hardware applies the gamma).
//
// The direction math matches the three.js equirectUv() convention for textures loaded with
// flipY = false (which is how GLTFLoader loads textures), including the X-axis flip between
// Unity's left-handed and glTF's right-handed coordinate system. The exported image can be
// used directly with THREE.EquirectangularReflectionMapping / PMREMGenerator.
Shader "Hidden/UnityGLTFCubemapToEquirect"
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
				// three.js: u = atan2(dir.z, dir.x) / 2pi + 0.5; v = asin(dir.y) / pi + 0.5, with
				// v = 1 at "up". With flipY = false the top PNG row is v = 0, hence the (0.5 - uv.y).
				float theta = (i.uv.x - 0.5) * 2.0 * UNITY_PI;
				float phi = (0.5 - i.uv.y) * UNITY_PI;

				float cp = cos(phi);
				float3 dir = float3(cp * cos(theta), sin(phi), cp * sin(theta));
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
