using System.Collections.Generic;
using GLTF.Schema;
using UnityEngine;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that fixes up materials coming from the Reallusion/CC4 avatar shaders
	/// (RL_* Amplify shaders and the SG_FakeSSS* / SG_HairAniso* / SG_Avatars* shader graphs)
	/// so the exported glTF renders correctly in three.js:
	///
	///  - Hair: the RL/SG hair tint colors default to alpha 0 (meaningless in Unity — the
	///    shaders are opaque cutout and take per-pixel coverage from the diffuse texture's
	///    alpha), but the exporter copies that alpha into baseColorFactor, which multiplies
	///    the texture alpha to 0 in glTF viewers → invisible hair. Forces baseColorFactor
	///    alpha to 1 and exports alphaMode MASK with the shader's real cutoff
	///    (_CutOff / _AlphaClip2 — the generic probe only knows _Cutoff and misses both).
	///
	///  - Occlusion: the generic exporter binds _MaskMap directly as the occlusion texture,
	///    but glTF samples occlusion from the RED channel while these shaders derive AO from
	///    the GREEN channel — the red channel is unrelated data, so meshes lose nearly all
	///    ambient/IBL light and render almost black. Strips the occlusion texture for avatar
	///    shaders (and any material whose occlusion could only have come from a raw _MaskMap).
	///    When occlusion is kept, the shader's AO strength (_AOStrenght / _AOStrength) is
	///    exported as the glTF occlusion strength instead of the default 1.0.
	///
	///  - Transmission: MaterialExtensionsExport's "alpha blend with preserved specular"
	///    special case writes KHR_materials_transmission with factor = 1 - tint alpha; with
	///    the RL alpha-0 tints that yields transmission 1 → hair/beards render as invisible
	///    glass in three.js. Removes KHR_materials_transmission (and the companion
	///    KHR_materials_ior it injects with ior = 1) from every exported material.
	///
	///  - Eyes: RL_Cornea* materials go through the generic custom-shader fallback, which
	///    exports roughness 1.0 → flat, dead eyes. Exports roughness from the shader's
	///    Cornea Smoothness instead so eyes keep their gloss.
	/// </summary>
	public class AvatarMaterialFixExport : GLTFExportPlugin
	{
		[SerializeField]
		[Tooltip("Force hair materials (RL/SG hair shaders) to alphaMode MASK with the shader's real cutoff and a base-color alpha of 1. Without this the tint alpha of 0 exports fully invisible hair.")]
		private bool fixHairAlpha = true;

		[SerializeField]
		[Tooltip("Remove the occlusion texture from Reallusion/SG avatar materials (and any material whose occlusion was sourced from a raw _MaskMap). glTF reads AO from the red channel; these shaders keep AO in the green channel, so the export renders almost black otherwise.")]
		private bool stripAvatarOcclusion = true;

		[SerializeField]
		[Tooltip("Remove the occlusion texture from EVERY exported material, not just avatar shaders.")]
		private bool stripAllOcclusion = false;

		[SerializeField]
		[Tooltip("Remove KHR_materials_transmission (and the ior=1 KHR_materials_ior that accompanies it) from all exported materials. These are injected for 'preserve specular' blend materials and make hair/beards render as invisible glass in three.js.")]
		private bool stripTransmission = true;

		public override string DisplayName => "IMMERSION_avatar_material_fix";

		public override string Description =>
			"Fixes Reallusion/CC4 avatar materials on export (hair alpha, MaskMap-as-occlusion, " +
			"preserve-specular transmission, cornea gloss) so avatars render correctly in three.js.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new AvatarMaterialFixExportContext(fixHairAlpha, stripAvatarOcclusion, stripAllOcclusion, stripTransmission);
		}
	}

	public class AvatarMaterialFixExportContext : GLTFExportPluginContext
	{
		private readonly bool _fixHairAlpha;
		private readonly bool _stripAvatarOcclusion;
		private readonly bool _stripAllOcclusion;
		private readonly bool _stripTransmission;

		// Shader-name tokens identifying the custom avatar shader families.
		private static readonly string[] _avatarShaderTokens = { "RL_", "FakeSSS", "SG_Hair", "SG_Avatars" };

		public AvatarMaterialFixExportContext(bool fixHairAlpha, bool stripAvatarOcclusion, bool stripAllOcclusion, bool stripTransmission)
		{
			_fixHairAlpha = fixHairAlpha;
			_stripAvatarOcclusion = stripAvatarOcclusion;
			_stripAllOcclusion = stripAllOcclusion;
			_stripTransmission = stripTransmission;
		}

		public override void AfterMaterialExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Material material, GLTFMaterial materialNode)
		{
			if (!material || !material.shader || materialNode == null) return;

			if (_fixHairAlpha) FixHairAlpha(material, materialNode);
			FixOcclusion(material, materialNode);
			FixCorneaGloss(material, materialNode);
		}

		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			if (!_stripTransmission || gltfRoot?.Materials == null) return;

			// Runs after every material (and every other plugin's AfterMaterialExport) so the
			// result does not depend on plugin ordering vs. MaterialExtensionsExport.
			foreach (var node in gltfRoot.Materials)
			{
				if (node?.Extensions == null) continue;
				node.Extensions.Remove(KHR_materials_transmission_Factory.EXTENSION_NAME);
				// only drop the companion ior the transmission hack injects (ior == 1);
				// a deliberate non-default ior is left alone.
				if (node.Extensions.TryGetValue(KHR_materials_ior_Factory.EXTENSION_NAME, out var iorExt) &&
				    iorExt is KHR_materials_ior ior && Mathf.Approximately(ior.ior, 1f))
					node.Extensions.Remove(KHR_materials_ior_Factory.EXTENSION_NAME);
			}

			PruneUnusedExtension(gltfRoot, KHR_materials_transmission_Factory.EXTENSION_NAME);
			PruneUnusedExtension(gltfRoot, KHR_materials_ior_Factory.EXTENSION_NAME);
		}

		// ─── hair ───

		// SG_HairAnisoFakeSSS: alpha = _AlbedoTransparency.a clipped at _CutOff.
		// RL_Amplify_HairShader_*: alpha = pow(_DiffuseMap.a / _AlphaRemap, _AlphaPower) clipped at _AlphaClip2.
		private static bool IsHairMaterial(Material material, out string cutoffProp)
		{
			cutoffProp = null;
			if (material.HasProperty("_AlbedoTransparency") && material.HasProperty("_CutOff"))
			{
				cutoffProp = "_CutOff";
				return true;
			}
			if (material.HasProperty("_AlphaClip2") && material.HasProperty("_AlphaRemap"))
			{
				cutoffProp = "_AlphaClip2";
				return true;
			}
			return false;
		}

		private static void FixHairAlpha(Material material, GLTFMaterial node)
		{
			if (!IsHairMaterial(material, out var cutoffProp)) return;

			node.AlphaMode = AlphaMode.MASK;
			node.AlphaCutoff = Mathf.Clamp(material.GetFloat(cutoffProp), 0.01f, 1f);
			node.DoubleSided = true; // hair cards are Cull Off / rendered double-sided in Unity

			// The tint alpha is meaningless in Unity (opaque cutout surface); coverage comes
			// from the texture alpha. An exported alpha of 0 would clip every pixel.
			var pbr = node.PbrMetallicRoughness;
			if (pbr != null)
			{
				var c = pbr.BaseColorFactor;
				pbr.BaseColorFactor = new GLTF.Math.Color(c.R, c.G, c.B, 1f);
			}
		}

		// ─── occlusion ───

		private static bool IsAvatarShader(Material material)
		{
			var shaderName = material.shader.name;
			foreach (var token in _avatarShaderTokens)
			{
				if (shaderName.Contains(token)) return true;
			}
			return false;
		}

		private static bool HasAssignedTexture(Material material, string prop)
		{
			return material.HasProperty(prop) && material.GetTexture(prop);
		}

		private void FixOcclusion(Material material, GLTFMaterial node)
		{
			if (node.OcclusionTexture == null) return;

			// A material with no real occlusion property can only have gotten its occlusion
			// from the exporter's raw _MaskMap fallback — which is wrong-channel data in glTF.
			var occlusionFromMaskMap =
				HasAssignedTexture(material, "_MaskMap") &&
				!HasAssignedTexture(material, "occlusionTexture") &&
				!HasAssignedTexture(material, "_OcclusionTexture") &&
				!HasAssignedTexture(material, "_OcclusionMap");

			if (_stripAllOcclusion || (_stripAvatarOcclusion && (IsAvatarShader(material) || occlusionFromMaskMap)))
			{
				node.OcclusionTexture = null;
				return;
			}

			// Occlusion kept: honor the shader's AO strength instead of the glTF default 1.0
			// (note the Reallusion/SG shader graphs spell it "_AOStrenght").
			var strengthProp = material.HasProperty("_AOStrenght") ? "_AOStrenght" :
				material.HasProperty("_AOStrength") ? "_AOStrength" : null;
			if (strengthProp != null)
				node.OcclusionTexture.Strength = Mathf.Clamp01(material.GetFloat(strengthProp));
		}

		// ─── eyes ───

		private static void FixCorneaGloss(Material material, GLTFMaterial node)
		{
			if (!material.shader.name.Contains("RL_Cornea")) return;
			var pbr = node.PbrMetallicRoughness;
			if (pbr == null) return;

			pbr.MetallicFactor = 0;
			if (material.HasProperty("_CorneaSmoothness"))
				pbr.RoughnessFactor = Mathf.Clamp01(1f - material.GetFloat("_CorneaSmoothness"));
		}

		// ─── extension bookkeeping ───

		private static void PruneUnusedExtension(GLTFRoot gltfRoot, string extensionName)
		{
			if (gltfRoot.ExtensionsUsed == null) return;
			foreach (var node in gltfRoot.Materials)
			{
				if (node?.Extensions != null && node.Extensions.ContainsKey(extensionName)) return;
			}
			gltfRoot.ExtensionsUsed.Remove(extensionName);
		}
	}
}
