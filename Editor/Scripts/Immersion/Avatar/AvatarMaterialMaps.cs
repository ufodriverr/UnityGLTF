using System;
using System.Collections.Generic;
using System.Globalization;
using GLTF.Schema;
using UnityEditor;
using UnityEngine;
using UnityGLTF;
using UnityGLTF.Extensions;
using UnityGLTF.Plugins;

namespace Immersion.Export
{
	/// <summary>
	/// Declarative glTF export of the avatar shaders (CC/Reallusion): the Immersion shader graphs
	/// (<c>SG_FakeSSSSkin</c>, <c>SG_FakeSSS</c>, <c>SG_HairAnisoFakeSSS</c>,
	/// <c>SG_AvatarsClothes</c>), the Reallusion URP graphs (<c>RL_CorneaShaderBasic_URP</c>,
	/// <c>RL_TearlineShader_URP</c>) and the Reallusion Amplify shaders.
	///
	/// glTF cannot infer these materials from the material alone: the shaders pack their mask
	/// channels differently from glTF's ORM layout (e.g. AO in G, smoothness in A, remapped by
	/// min/max), decide alpha clipping and culling in shader code, and use base-colour tints
	/// whose alpha is meaningless. So the knowledge lives in ONE table (<see cref="Maps"/>): per
	/// shader, which property feeds which glTF slot and how each ORM channel is built. Every
	/// VALUE still comes from the material. Matched materials are exported completely by this
	/// plugin (<c>BeforeMaterialExport</c> takeover); everything else keeps UnityGLTF's generic
	/// path.
	///
	/// The mask channels are re-packed at export time into a proper glTF ORM texture
	/// (R occlusion, G roughness, B metallic — one texture shared by the metallicRoughness and
	/// occlusion slots), see <see cref="ChannelPacker"/> / <c>Hidden/IMMERSION_ChannelPack</c>.
	/// </summary>
	public class AvatarMaterialMaps : GLTFExportPlugin
	{
		public override string DisplayName => "IMMERSION_avatar_materials";

		public override string Description =>
			"Exports the CC/Reallusion avatar shaders (SG_* graphs, RL_* graphs, Amplify) to glTF " +
			"from a per-shader table: base colour, normal, packed ORM, alpha and culling straight " +
			"from the material.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new AvatarMaterialMapsContext();
		}

		// ─────────────────────────── the table ───────────────────────────

		/// <summary>One ORM output channel: which channel of which source texture, remapped.</summary>
		public struct ChannelSource
		{
			/// <summary>0 = <see cref="OrmSpec.TexA"/>, 1 = <see cref="OrmSpec.TexB"/>, -1 = constant (<see cref="Min"/>).</summary>
			public int Tex;
			/// <summary>0..3 = R,G,B,A of the source texture.</summary>
			public int Channel;
			/// <summary>value = Min + channel * (Max - Min), then optionally 1 - value.</summary>
			public float Min, Max;
			/// <summary>Smoothness-style channels: glTF wants roughness = 1 - smoothness.</summary>
			public bool Invert;

			public static ChannelSource Const(float value) => new ChannelSource { Tex = -1, Min = value, Max = value };

			public static ChannelSource From(int tex, int channel, float min = 0f, float max = 1f, bool invert = false)
				=> new ChannelSource { Tex = tex, Channel = channel, Min = min, Max = max, Invert = invert };

			/// <summary>The channel's value when the source texture is missing (Unity samples its
			/// default texture, which is white for every mask slot these shaders use).</summary>
			public float FactorWithoutTexture()
			{
				var v = Tex < 0 ? Min : Max;
				v = Mathf.Clamp01(v);
				return Invert ? 1f - v : v;
			}

			public override string ToString() =>
				string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:R}:{3:R}:{4}", Tex, Channel, Min, Max, Invert ? 1 : 0);
		}

		/// <summary>How the glTF ORM texture is built for one material.</summary>
		public sealed class OrmSpec
		{
			public Texture TexA;
			public Texture TexB;
			public ChannelSource Occlusion = ChannelSource.Const(1f);
			public ChannelSource Roughness = ChannelSource.Const(1f);
			public ChannelSource Metallic = ChannelSource.Const(0f);
			/// <summary>False when the shader has no occlusion input: the R channel is 1 and the
			/// glTF occlusionTexture slot is left empty.</summary>
			public bool HasOcclusion = true;

			public bool HasTexture => TexA != null || TexB != null;
		}

		/// <summary>Everything the plugin writes for one material.</summary>
		public sealed class MaterialSpec
		{
			public Texture BaseColorTexture;
			public string BaseColorProperty;
			public Color BaseColor = Color.white;

			public Texture NormalTexture;
			public float NormalScale = 1f;

			public Texture EmissiveTexture;
			public Color Emissive = Color.black;

			/// <summary>Null = no mask input on this shader; the factors below are used instead.</summary>
			public OrmSpec Orm;
			public float Metallic;
			public float Roughness = 1f;

			public AlphaMode AlphaMode = AlphaMode.OPAQUE;
			public float AlphaCutoff = 0.5f;
			/// <summary>Base-colour alpha for BLEND materials; OPAQUE/MASK always export alpha 1.</summary>
			public float Opacity = 1f;
			public bool DoubleSided;
		}

		public sealed class ShaderMap
		{
			public string[] ShaderNames;
			public Func<Material, MaterialSpec> Build;
		}

		// Unity channel indices
		private const int R = 0, G = 1, B = 2, A = 3;

		/// <summary>
		/// The per-shader table. Channel layouts are read off the shader graphs / Amplify sources:
		///  - SG_FakeSSSSkin, RL_SkinShader (HDRP-style _MaskMap): R metallic, G AO, A smoothness
		///    with Occlusion = 1 - (1 - G) * AOStrength and Smoothness = lerp(min, max, A).
		///  - SG_FakeSSS (_RMA): R smoothness * _Smoothness, G metallic, B AO.
		///  - SG_AvatarsClothes: _MetallicGlossMap G * _Metallic = metallic, A * _GlossMapScale =
		///    smoothness; _OcclusionMap G with _OcclusionStrenght.
		///  - SG_HairAnisoFakeSSS: alpha = _AlbedoTransparency.a clipped at _AlphaClip, specular
		///    workflow (metallic 0), smoothness = lerp(_SmoothnesPowerMin, Max, A^power) (power
		///    ignored: no per-texel pow in the pack), no AO input, RenderFace from _Cull.
		///  - RL_CorneaShaderBasic_URP: procedural iris/sclera blend — the cornea diffuse map IS the
		///    full eye texture, cornea smoothness is the visible gloss; the 2x-tiled sclera
		///    micro-normal is not exported (no per-slot tiling in the base spec).
		///  - RL_TearlineShader_URP: constant transparent shell.
		///  - Reallusion Amplify hair: alpha = pow(saturate(a / _AlphaRemap), _AlphaPower) clipped at
		///    _AlphaClip2, i.e. the raw-alpha cutoff is _AlphaRemap * _AlphaClip2^(1/_AlphaPower);
		///    Cull Off is hard-coded in the pass.
		/// </summary>
		public static readonly IReadOnlyList<ShaderMap> Maps = new List<ShaderMap>
		{
			new ShaderMap
			{
				ShaderNames = new[] { "Shader Graphs/SG_FakeSSSSkin" },
				Build = m =>
				{
					var ao = F(m, "_AOStrenght", 1f);
					return new MaterialSpec
					{
						BaseColorTexture = Tex(m, "_AlbedoMap"), BaseColorProperty = "_AlbedoMap",
						BaseColor = Col(m, "_BaseColor", Color.white),
						NormalTexture = Tex(m, "_NormalMap"),
						Orm = new OrmSpec
						{
							TexA = Tex(m, "_MaskMap"),
							Occlusion = ChannelSource.From(0, G, 1f - ao, 1f),
							Roughness = ChannelSource.From(0, A, F(m, "_SmoothnessMin", 0f), F(m, "_SmoothnessMax", 1f), invert: true),
							Metallic = ChannelSource.From(0, R),
						},
						DoubleSided = CullDoubleSided(m, false),
					};
				}
			},
			new ShaderMap
			{
				ShaderNames = new[] { "Shader Graphs/SG_FakeSSS" },
				Build = m =>
				{
					var ao = F(m, "_AOStrenght", 1f);
					return new MaterialSpec
					{
						BaseColorTexture = Tex(m, "_AlbedoMap"), BaseColorProperty = "_AlbedoMap",
						BaseColor = Col(m, "_BaseColor", Color.white),
						NormalTexture = Tex(m, "_NormalMap"),
						Orm = new OrmSpec
						{
							TexA = Tex(m, "_RMA"),
							Occlusion = ChannelSource.From(0, B, 1f - ao, 1f),
							Roughness = ChannelSource.From(0, R, 0f, F(m, "_Smoothness", 1f), invert: true),
							Metallic = ChannelSource.From(0, G),
						},
						DoubleSided = CullDoubleSided(m, false),
					};
				}
			},
			new ShaderMap
			{
				ShaderNames = new[] { "Shader Graphs/SG_HairAnisoFakeSSS" },
				Build = m => new MaterialSpec
				{
					BaseColorTexture = Tex(m, "_AlbedoTransparency"), BaseColorProperty = "_AlbedoTransparency",
					BaseColor = Col(m, "_Color", Color.white),
					Orm = new OrmSpec
					{
						TexA = Tex(m, "_MaskMap"),
						HasOcclusion = false,
						Roughness = ChannelSource.From(0, A, F(m, "_SmoothnesPowerMin", 0f), F(m, "_SmoothnesPowerMax", 1f), invert: true),
						Metallic = ChannelSource.Const(0f),
					},
					AlphaMode = AlphaMode.MASK,
					AlphaCutoff = m.HasProperty("_AlphaClip") ? F(m, "_AlphaClip", 0.5f) : F(m, "_CutOff", 0.5f),
					DoubleSided = CullDoubleSided(m, true),
				}
			},
			new ShaderMap
			{
				ShaderNames = new[] { "Shader Graphs/SG_AvatarsClothes" },
				Build = m =>
				{
					var occlusionTex = Tex(m, "_OcclusionMap");
					var os = F(m, "_OcclusionStrenght", 1f);
					return new MaterialSpec
					{
						BaseColorTexture = Tex(m, "_BaseMap"), BaseColorProperty = "_BaseMap",
						BaseColor = Col(m, "_BaseColor", Color.white),
						NormalTexture = Tex(m, "_NormalMap"), NormalScale = F(m, "_NormalScale", 1f),
						Orm = new OrmSpec
						{
							TexA = Tex(m, "_MetallicGlossMap"),
							TexB = occlusionTex,
							HasOcclusion = occlusionTex != null,
							Occlusion = occlusionTex != null ? ChannelSource.From(1, G, 1f - os, 1f) : ChannelSource.Const(1f),
							Roughness = ChannelSource.From(0, A, 0f, F(m, "_GlossMapScale", 1f), invert: true),
							Metallic = ChannelSource.From(0, G, 0f, F(m, "_Metallic", 1f)),
						},
						DoubleSided = CullDoubleSided(m, false),
					};
				}
			},
			new ShaderMap
			{
				ShaderNames = new[] { "Shader Graphs/RL_CorneaShaderBasic_URP" },
				Build = m =>
				{
					var ao = F(m, "_AOStrength", 0.2f);
					return new MaterialSpec
					{
						BaseColorTexture = Tex(m, "_CorneaDiffuseMap"), BaseColorProperty = "_CorneaDiffuseMap",
						Orm = new OrmSpec
						{
							TexA = Tex(m, "_MaskMap"),
							Occlusion = ChannelSource.From(0, G, 1f - ao, 1f),
							Roughness = ChannelSource.Const(1f - F(m, "_CorneaSmoothness", 1f)),
							Metallic = ChannelSource.Const(0f),
						},
						EmissiveTexture = Tex(m, "_EmissionMap"),
						Emissive = Col(m, "_EmissiveColor", Color.black),
					};
				}
			},
			new ShaderMap
			{
				ShaderNames = new[] { "Shader Graphs/RL_TearlineShader_URP" },
				Build = m =>
				{
					var alpha = Mathf.Clamp01(F(m, "_Alpha", 0f));
					var c = Col(m, "_BaseColor", Color.black);
					return new MaterialSpec
					{
						BaseColor = new Color(c.r * alpha, c.g * alpha, c.b * alpha, 1f),
						Metallic = F(m, "_Metallic", 0f),
						Roughness = 1f - F(m, "_Smoothness", 1f),
						AlphaMode = AlphaMode.BLEND,
						Opacity = alpha,
					};
				}
			},
			new ShaderMap
			{
				ShaderNames = new[]
				{
					"Reallusion/Amplify/RL_SkinShader_Variants_URP",
					"Reallusion/Amplify/RL_HeadShaderWrinkle_Baked_URP",
				},
				Build = m =>
				{
					var ao = F(m, "_AOStrength", 1f);
					return new MaterialSpec
					{
						BaseColorTexture = Tex(m, "_DiffuseMap"), BaseColorProperty = "_DiffuseMap",
						BaseColor = Col(m, "_DiffuseColor", Color.white),
						NormalTexture = Tex(m, "_NormalMap"), NormalScale = F(m, "_NormalStrength", 1f),
						Orm = new OrmSpec
						{
							TexA = Tex(m, "_MaskMap"),
							Occlusion = ChannelSource.From(0, G, 1f - ao, 1f),
							Roughness = ChannelSource.From(0, A, F(m, "_SmoothnessMin", 0f), F(m, "_SmoothnessMax", 1f), invert: true),
							Metallic = ChannelSource.From(0, R),
						},
						EmissiveTexture = Tex(m, "_EmissionMap"),
						Emissive = Col(m, "_EmissiveColor", Color.black),
					};
				}
			},
			new ShaderMap
			{
				ShaderNames = new[]
				{
					"Reallusion/Amplify/RL_HairShader_Variants_URP",
					"Reallusion/Amplify/RL_HairShader_1st_Pass_Variants_URP",
					"Reallusion/Amplify/RL_HairShader_2nd_Pass_Variants_URP",
					"Reallusion/Amplify/RL_HairShader_Coverage_URP",
					"Reallusion/Amplify/RL_HairShader_Baked_URP",
					"Reallusion/Amplify/RL_HairShader_1st_Pass_Baked_URP",
					"Reallusion/Amplify/RL_HairShader_2nd_Pass_Baked_URP",
					"Reallusion/Amplify/RL_HairShader_Coverge_Baked_URP",
				},
				Build = m =>
				{
					var ao = F(m, "_AOStrength", 1f);
					var remap = Mathf.Max(0.01f, F(m, "_AlphaRemap", 1f));
					var power = Mathf.Max(0.01f, F(m, "_AlphaPower", 1f));
					var clip = Mathf.Clamp01(F(m, "_AlphaClip2", 0.15f));
					return new MaterialSpec
					{
						BaseColorTexture = Tex(m, "_DiffuseMap"), BaseColorProperty = "_DiffuseMap",
						BaseColor = Col(m, "_DiffuseColor", Color.white),
						NormalTexture = Tex(m, "_NormalMap"), NormalScale = F(m, "_NormalStrength", 1f),
						Orm = new OrmSpec
						{
							TexA = Tex(m, "_MaskMap"),
							Occlusion = ChannelSource.From(0, G, 1f - ao, 1f),
							Roughness = ChannelSource.From(0, A, F(m, "_SmoothnessMin", 0f), F(m, "_SmoothnessMax", 1f), invert: true),
							Metallic = ChannelSource.Const(0f),
						},
						AlphaMode = AlphaMode.MASK,
						AlphaCutoff = remap * Mathf.Pow(clip, 1f / power),
						DoubleSided = true,
					};
				}
			},
			new ShaderMap
			{
				ShaderNames = new[] { "Reallusion/Amplify/RL_TeethShader_URP", "Reallusion/Amplify/RL_TongueShader_URP" },
				Build = m =>
				{
					var ao = F(m, "_AOStrength", 1f);
					return new MaterialSpec
					{
						BaseColorTexture = Tex(m, "_DiffuseMap"), BaseColorProperty = "_DiffuseMap",
						NormalTexture = Tex(m, "_NormalMap"), NormalScale = F(m, "_NormalStrength", 1f),
						Orm = new OrmSpec
						{
							TexA = Tex(m, "_MaskMap"),
							Occlusion = ChannelSource.From(0, G, 1f - ao, 1f),
							Roughness = ChannelSource.From(0, A, 0f, F(m, "_SmoothnessMax", 0.88f), invert: true),
							Metallic = ChannelSource.Const(0f),
						},
						EmissiveTexture = Tex(m, "_EmissionMap"),
						Emissive = Col(m, "_EmissiveColor", Color.black),
					};
				}
			},
			new ShaderMap
			{
				ShaderNames = new[] { "Reallusion/Amplify/RL_CorneaShaderParallax_URP", "Reallusion/Amplify/RL_CorneaShaderParallax_Baked_URP" },
				Build = m =>
				{
					var ao = F(m, "_AOStrength", 0.2f);
					return new MaterialSpec
					{
						BaseColorTexture = Tex(m, "_CorneaDiffuseMap"), BaseColorProperty = "_CorneaDiffuseMap",
						Orm = new OrmSpec
						{
							TexA = Tex(m, "_MaskMap"),
							Occlusion = ChannelSource.From(0, G, 1f - ao, 1f),
							Roughness = ChannelSource.Const(1f - F(m, "_CorneaSmoothness", 1f)),
							Metallic = ChannelSource.Const(0f),
						},
						EmissiveTexture = Tex(m, "_EmissionMap"),
						Emissive = Col(m, "_EmissiveColor", Color.black),
					};
				}
			},
		};

		public static ShaderMap Find(Shader shader)
		{
			if (!shader) return null;
			var name = shader.name;
			foreach (var map in Maps)
				foreach (var candidate in map.ShaderNames)
					if (string.Equals(candidate, name, StringComparison.Ordinal))
						return map;
			return null;
		}

		// ─────────────────────── material readers ───────────────────────

		private static Texture Tex(Material m, string prop) => m.HasProperty(prop) ? m.GetTexture(prop) : null;
		private static float F(Material m, string prop, float fallback) => m.HasProperty(prop) ? m.GetFloat(prop) : fallback;
		private static Color Col(Material m, string prop, Color fallback) => m.HasProperty(prop) ? m.GetColor(prop) : fallback;

		/// <summary>URP material override: _Cull 0 = Off (double-sided), 1 = Front, 2 = Back.</summary>
		private static bool CullDoubleSided(Material m, bool graphDefault)
		{
			return m.HasProperty("_Cull") ? Mathf.RoundToInt(m.GetFloat("_Cull")) == 0 : graphDefault;
		}
	}

	public class AvatarMaterialMapsContext : GLTFExportPluginContext
	{
		private readonly Dictionary<string, TextureId> _packedByKey = new Dictionary<string, TextureId>();

		public override void BeforeSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			// The exporter encodes textures after all plugin callbacks have run; packed textures of
			// the previous export are only safe to destroy now.
			ChannelPacker.DestroyPending();
			_packedByKey.Clear();
		}

		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			EditorApplication.delayCall += ChannelPacker.DestroyPending;
		}

		public override bool BeforeMaterialExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Material material, GLTFMaterial materialNode)
		{
			if (!material || !material.shader) return false;
			var map = AvatarMaterialMaps.Find(material.shader);
			if (map == null) return false;

			var spec = map.Build(material);
			if (spec == null) return false;

			Apply(exporter, gltfRoot, material, materialNode, spec);
			return true;
		}

		private void Apply(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Material material, GLTFMaterial node, AvatarMaterialMaps.MaterialSpec spec)
		{
			node.AlphaMode = spec.AlphaMode;
			if (spec.AlphaMode == AlphaMode.MASK)
				node.AlphaCutoff = Mathf.Clamp(spec.AlphaCutoff, 0.01f, 1f);
			node.DoubleSided = spec.DoubleSided;

			var pbr = new PbrMetallicRoughness();
			var c = spec.BaseColor;
			// Opaque and cutout shaders ignore the tint alpha (the CC tints author it as 0); only a
			// BLEND material carries a meaningful alpha.
			var alpha = spec.AlphaMode == AlphaMode.BLEND ? Mathf.Clamp01(spec.Opacity) : 1f;
			var linear = new Color(c.r, c.g, c.b, alpha).ToNumericsColorLinear();
			pbr.BaseColorFactor = new GLTF.Math.Color(linear.R, linear.G, linear.B, alpha);

			if (spec.BaseColorTexture)
			{
				pbr.BaseColorTexture = exporter.ExportTextureInfo(spec.BaseColorTexture, GLTFSceneExporter.TextureMapType.BaseColor);
				ApplyTextureTransform(gltfRoot, pbr.BaseColorTexture, material, spec.BaseColorProperty);
			}

			if (spec.Orm != null && spec.Orm.HasTexture)
			{
				var id = ExportPackedOrm(exporter, material, spec.Orm);
				if (id != null)
				{
					pbr.MetallicRoughnessTexture = new TextureInfo { Index = id };
					pbr.MetallicFactor = 1f;
					pbr.RoughnessFactor = 1f;
					if (spec.Orm.HasOcclusion)
						node.OcclusionTexture = new OcclusionTextureInfo { Index = id, Strength = 1f };
				}
				else
				{
					ApplyFactorsWithoutTexture(pbr, spec.Orm);
				}
			}
			else if (spec.Orm != null)
			{
				ApplyFactorsWithoutTexture(pbr, spec.Orm);
			}
			else
			{
				pbr.MetallicFactor = Mathf.Clamp01(spec.Metallic);
				pbr.RoughnessFactor = Mathf.Clamp01(spec.Roughness);
			}
			node.PbrMetallicRoughness = pbr;

			if (spec.NormalTexture)
			{
				node.NormalTexture = exporter.ExportNormalTextureInfo(spec.NormalTexture, GLTFSceneExporter.TextureMapType.Normal, material);
				node.NormalTexture.Scale = spec.NormalScale;
			}

			if (spec.Emissive.maxColorComponent > 0f)
			{
				var e = spec.Emissive.ToNumericsColorLinear();
				node.EmissiveFactor = new GLTF.Math.Color(e.R, e.G, e.B, 1f);
				if (spec.EmissiveTexture)
					node.EmissiveTexture = exporter.ExportTextureInfo(spec.EmissiveTexture, GLTFSceneExporter.TextureMapType.Emissive);
			}
		}

		private static void ApplyFactorsWithoutTexture(PbrMetallicRoughness pbr, AvatarMaterialMaps.OrmSpec orm)
		{
			pbr.MetallicFactor = orm.Metallic.FactorWithoutTexture();
			pbr.RoughnessFactor = orm.Roughness.FactorWithoutTexture();
		}

		private TextureId ExportPackedOrm(GLTFSceneExporter exporter, Material material, AvatarMaterialMaps.OrmSpec orm)
		{
			var key = string.Join("|",
				orm.TexA ? orm.TexA.GetInstanceID().ToString() : "-",
				orm.TexB ? orm.TexB.GetInstanceID().ToString() : "-",
				orm.Occlusion.ToString(), orm.Roughness.ToString(), orm.Metallic.ToString());
			if (_packedByKey.TryGetValue(key, out var cached))
				return cached;

			var source = orm.TexA ? orm.TexA : orm.TexB;
			var packed = ChannelPacker.Pack(orm, source.name + "_ORM");
			if (packed == null) return null;

			// MetallicRoughness slot: linear, alpha dropped, no channel conversion — the pack IS the glTF layout.
			var id = exporter.ExportTexture(packed, GLTFSceneExporter.TextureMapType.MetallicRoughness);
			_packedByKey[key] = id;
			return id;
		}

		/// <summary>Same convention as UnityGLTF's own texture transform export (V flipped to glTF's top-left origin).</summary>
		private static void ApplyTextureTransform(GLTFRoot root, TextureInfo info, Material material, string property)
		{
			if (info == null || string.IsNullOrEmpty(property) || !material.HasProperty(property)) return;
			var offset = material.GetTextureOffset(property);
			var scale = material.GetTextureScale(property);
			if (offset == Vector2.zero && scale == Vector2.one) return;

			if (root.ExtensionsUsed == null) root.ExtensionsUsed = new List<string>();
			if (!root.ExtensionsUsed.Contains(ExtTextureTransformExtensionFactory.EXTENSION_NAME))
				root.ExtensionsUsed.Add(ExtTextureTransformExtensionFactory.EXTENSION_NAME);
			if (info.Extensions == null) info.Extensions = new Dictionary<string, IExtension>();
			info.Extensions[ExtTextureTransformExtensionFactory.EXTENSION_NAME] = new ExtTextureTransformExtension(
				new GLTF.Math.Vector2(offset.x, 1 - offset.y - scale.y),
				0,
				new GLTF.Math.Vector2(scale.x, scale.y),
				0);
		}
	}

	/// <summary>
	/// GPU re-pack of shader mask channels into a glTF ORM texture (<c>Hidden/IMMERSION_ChannelPack</c>).
	/// The result is a readable linear RGBA32 <see cref="Texture2D"/> WITH a mip chain (the exporter
	/// derives the glTF sampler's minFilter from <c>mipmapCount</c>; a mip-less texture would ship
	/// LINEAR filtering and alias). Packed textures must stay alive until the exporter has encoded
	/// them, so they are pooled and released by <see cref="DestroyPending"/>.
	/// </summary>
	public static class ChannelPacker
	{
		private const string ShaderName = "Hidden/IMMERSION_ChannelPack";
		private static readonly List<Texture2D> _pending = new List<Texture2D>();

		private static readonly int TexAId = Shader.PropertyToID("_TexA");
		private static readonly int TexBId = Shader.PropertyToID("_TexB");
		private static readonly int PackRId = Shader.PropertyToID("_PackR");
		private static readonly int PackGId = Shader.PropertyToID("_PackG");
		private static readonly int PackBId = Shader.PropertyToID("_PackB");
		private static readonly int InvertId = Shader.PropertyToID("_Invert");

		public static Texture2D Pack(AvatarMaterialMaps.OrmSpec orm, string name)
		{
			var shader = Shader.Find(ShaderName);
			if (!shader)
			{
				Debug.LogError($"[IMMERSION_avatar_materials] shader not found: {ShaderName} — ORM texture skipped for {name}.");
				return null;
			}

			var a = orm.TexA;
			var b = orm.TexB;
			var width = Mathf.Max(a ? a.width : 0, b ? b.width : 0);
			var height = Mathf.Max(a ? a.height : 0, b ? b.height : 0);
			if (width <= 0 || height <= 0) return null;

			var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
			mat.SetTexture(TexAId, a ? a : Texture2D.whiteTexture);
			mat.SetTexture(TexBId, b ? b : Texture2D.whiteTexture);
			mat.SetVector(PackRId, Vec(orm.Occlusion));
			mat.SetVector(PackGId, Vec(orm.Roughness));
			mat.SetVector(PackBId, Vec(orm.Metallic));
			mat.SetVector(InvertId, new Vector4(orm.Occlusion.Invert ? 1 : 0, orm.Roughness.Invert ? 1 : 0, orm.Metallic.Invert ? 1 : 0, 0));

			var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0)
			{
				msaaSamples = 1,
				depthBufferBits = 0,
				sRGB = false, // data, not colour
				useMipMap = false,
				autoGenerateMips = false,
			};
			var rt = RenderTexture.GetTemporary(desc);
			var prev = RenderTexture.active;
			try
			{
				Graphics.Blit(null, rt, mat, 0);
				RenderTexture.active = rt;
				var tex = new Texture2D(width, height, TextureFormat.RGBA32, true, true) { name = name };
				tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
				tex.Apply(true, false);
				tex.wrapMode = a ? a.wrapMode : (b ? b.wrapMode : TextureWrapMode.Repeat);
				tex.filterMode = FilterMode.Trilinear;
				_pending.Add(tex);
				return tex;
			}
			finally
			{
				RenderTexture.active = prev;
				RenderTexture.ReleaseTemporary(rt);
				UnityEngine.Object.DestroyImmediate(mat);
			}
		}

		private static Vector4 Vec(AvatarMaterialMaps.ChannelSource s) => new Vector4(s.Tex, s.Channel, s.Min, s.Max);

		public static void DestroyPending()
		{
			foreach (var tex in _pending)
				if (tex) UnityEngine.Object.DestroyImmediate(tex);
			_pending.Clear();
		}
	}
}
