using System;
using System.Collections.Generic;
using System.Linq;
using GLTF.Schema;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGLTF.Extensions;
using UnityGLTF.Plugins;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityGLTF
{
	public partial class GLTFSceneExporter
	{
		// Static callbacks
		[Obsolete("Use ExportPlugins on GLTFSettings instead")]
		public static event BeforeSceneExportDelegate BeforeSceneExport;
		[Obsolete("Use ExportPlugins on GLTFSettings instead")]
		public static event AfterSceneExportDelegate AfterSceneExport;
		[Obsolete("Use ExportPlugins on GLTFSettings instead")]
		public static event AfterNodeExportDelegate AfterNodeExport;
		/// <returns>True: material export is complete. False: continue regular export.</returns>
		[Obsolete("Use ExportPlugins on GLTFSettings instead")]
		public static event BeforeMaterialExportDelegate BeforeMaterialExport;
		[Obsolete("Use ExportPlugins on GLTFSettings instead")]
		public static event AfterMaterialExportDelegate AfterMaterialExport;
		
		// mock for Default material
		private static Material _defaultMaterial = null;
		public static Material DefaultMaterial
		{
			get
			{
				if (_defaultMaterial) return _defaultMaterial;

#if UNITY_2019_3_OR_NEWER
				var pipelineAsset = GraphicsSettings.currentRenderPipeline;
				if (pipelineAsset)
				{
					_defaultMaterial = pipelineAsset.defaultMaterial;
				}
				else
#endif
				{
					var shader = Shader.Find("Legacy Shaders/Diffuse"); // by default in the always included shaders list
					if (shader)
						_defaultMaterial = new Material(shader);
				}
				return _defaultMaterial;
			}
		}

		// These occlusion maps are baked into metallic roughness map (channel R)
		private HashSet<Material> _occlusionBakedTextures = new HashSet<Material>();

        public MaterialId ExportMaterial(Material materialObj)
		{
			MaterialId id = GetMaterialId(_root, materialObj);
			if (id != null)
			{
				return id;
			}

			var material = new GLTFMaterial();

            if (!materialObj)
            {
                if (ExportNames)
                {
                    material.Name = "default";
                }

                // create default material
                // TODO check why we need to do anything here
                material.PbrMetallicRoughness = new PbrMetallicRoughness() { MetallicFactor = 0, RoughnessFactor = 1.0f };
                return CreateAndAddMaterialId(materialObj, material);
            }

			if (ExportNames)
			{
				material.Name = materialObj.name;
			}

			foreach (var plugin in _plugins)
			{
				if (plugin == null) continue;
				beforeMaterialExportMarker.Begin();
				if (plugin.BeforeMaterialExport(this, _root, materialObj, material))
				{
					beforeMaterialExportMarker.End();
					return CreateAndAddMaterialId(materialObj, material);
				}
				else
				{
					beforeMaterialExportMarker.End();
				}
			}

			exportMaterialMarker.Begin();
			var isBirp =
#if UNITY_2019_3_OR_NEWER
				!GraphicsSettings.currentRenderPipeline;
#else
				true;
#endif

			switch (materialObj.GetTag("RenderType", false, ""))
			{
				case "TransparentCutout":
					if (materialObj.HasProperty("alphaCutoff"))
						material.AlphaCutoff = materialObj.GetFloat("alphaCutoff");
					else if (materialObj.HasProperty("_AlphaCutoff"))
						material.AlphaCutoff = materialObj.GetFloat("_AlphaCutoff");
					else if (materialObj.HasProperty("_Cutoff"))
						material.AlphaCutoff = materialObj.GetFloat("_Cutoff");
					material.AlphaMode = AlphaMode.MASK;
					break;
				case "Transparent":
				case "Fade":
					material.AlphaMode = AlphaMode.BLEND;
					break;
				default:
					if ((!isBirp && materialObj.IsKeywordEnabled("_ALPHATEST_ON")) ||
					    (isBirp && materialObj.IsKeywordEnabled("_BUILTIN_ALPHATEST_ON")) ||
					    materialObj.renderQueue == 2450)
					{
						if (materialObj.HasProperty("alphaCutoff"))
							material.AlphaCutoff = materialObj.GetFloat("alphaCutoff");
						else if (materialObj.HasProperty("_AlphaCutoff"))
							material.AlphaCutoff = materialObj.GetFloat("_AlphaCutoff");
						else if (materialObj.HasProperty("_Cutoff"))
							material.AlphaCutoff = materialObj.GetFloat("_Cutoff");
						material.AlphaMode = AlphaMode.MASK;
					}
					else
					{
						material.AlphaMode = AlphaMode.OPAQUE;
					}
					break;
			}

			var baseMap = new PBRGraphMap(materialObj);
			material.DoubleSided =  baseMap.DoubleSided || (materialObj.shader.name.EndsWith("-Double")); // workaround for exporting shaders that are set to double-sided on 2020.3

			if (materialObj.IsKeywordEnabled("_EMISSION") || materialObj.IsKeywordEnabled("EMISSION") || materialObj.HasProperty("emissiveTexture") || materialObj.HasProperty("_EmissiveTexture") || materialObj.HasProperty("_EmissiveColorMap"))
			{
				// In Gamma space, some materials treat their emissive color inputs differently than in Linear space.
				// This is super confusing when converting materials, but we also need to handle it correctly here.
				var isUnityMaterialWithWeirdColorspaceHandling = QualitySettings.activeColorSpace == ColorSpace.Gamma &&
					(materialObj.shader.name == "Standard" || materialObj.shader.name == "Standard (Specular setup)" ||
					materialObj.shader.name == "Standard (Roughness setup)" ||
					materialObj.shader.name == "Universal Render Pipeline/Lit" ||
					materialObj.shader.name == "Universal Render Pipeline/Simple Lit" ||
					materialObj.shader.name == "Universal Render Pipeline/Unlit");
				                                             
				if (materialObj.HasProperty("_EmissionColor") || materialObj.HasProperty("emissiveFactor") || materialObj.HasProperty("_EmissiveFactor") || materialObj.HasProperty("_UseEmissiveIntensity"))
				{
					var emissiveAmount = Color.black;
					var maxEmissiveAmount = 0f;
					if (materialObj.HasProperty("_UseEmissiveIntensity"))
					{
						// hdrp route uses its own color decomposition
						if (materialObj.GetFloat("_UseEmissiveIntensity") == 1)
						{
							emissiveAmount = materialObj.GetColor("_EmissiveColorLDR");
							maxEmissiveAmount = materialObj.GetFloat("_EmissiveIntensity");
						}
						else
						{
							var colorHdr = materialObj.GetColor("_EmissiveColor");
							ConvertHDRColorToLDR(colorHdr, out emissiveAmount, out maxEmissiveAmount);
						}
					}
					else
					{
						var c = materialObj.HasProperty("emissiveFactor") ? materialObj.GetColor("emissiveFactor") :
							materialObj.HasProperty("_EmissionColor") ? materialObj.GetColor("_EmissionColor") :
							materialObj.GetColor("_EmissiveFactor");
						DecomposeEmissionColor(c, out emissiveAmount, out maxEmissiveAmount);
					}
					
					if (isUnityMaterialWithWeirdColorspaceHandling)
						material.EmissiveFactor = emissiveAmount.ToNumericsColorRaw();
					else
						material.EmissiveFactor = emissiveAmount.ToNumericsColorGamma();

					if (maxEmissiveAmount > 1)
					{
						var materialSettings = (_plugins.FirstOrDefault(x => x is MaterialExtensionsExportContext) as MaterialExtensionsExportContext)?.settings;
						var emissiveStrengthSupported = materialSettings && materialSettings.KHR_materials_emissive_strength;
						if (emissiveStrengthSupported)
						{
							material.AddExtension(KHR_materials_emissive_strength_Factory.EXTENSION_NAME, new KHR_materials_emissive_strength() { emissiveStrength = maxEmissiveAmount });
							DeclareExtensionUsage(KHR_materials_emissive_strength_Factory.EXTENSION_NAME, false);
						}
					}
				}

				if (materialObj.HasProperty("_EmissionMap") || materialObj.HasProperty("_EmissiveMap") || materialObj.HasProperty("_EmissiveTexture") || materialObj.HasProperty("emissiveTexture") || materialObj.HasProperty("_EmissiveColorMap"))
				{
					var propName = materialObj.HasProperty("emissiveTexture") ? "emissiveTexture" :
						materialObj.HasProperty("_EmissiveTexture") ? "_EmissiveTexture" :
						materialObj.HasProperty("_EmissionMap") ? "_EmissionMap" :
						materialObj.HasProperty("_EmissiveColorMap") ? "_EmissiveColorMap" :
						"_EmissiveMap";

					var emissionTex = materialObj.GetTexture(propName);

					if (emissionTex)
					{
						if(emissionTex is Texture2D)
						{
							material.EmissiveTexture = ExportTextureInfo(emissionTex, TextureMapType.Emissive);

							ExportTextureTransform(material.EmissiveTexture, materialObj, propName);
						}
						else
						{
							Debug.LogFormat(LogType.Error, "Can't export a {0} emissive texture in material {1}", emissionTex.GetType(), materialObj.name);
						}

					}
				}
			}

			// workaround for glTFast roundtrip: has a _BumpMap but no _NORMALMAP or _BUMPMAP keyword.
			var is_glTFastShader = materialObj.shader.name.IndexOf("glTF", StringComparison.Ordinal) > -1;
			if (materialObj.HasProperty("_BumpMap") && (materialObj.IsKeywordEnabled("_NORMALMAP") || materialObj.IsKeywordEnabled("_BUMPMAP") || is_glTFastShader))
			{
				var normalTex = materialObj.GetTexture("_BumpMap");

				if (normalTex)
				{
					if(normalTex is Texture2D)
					{
						material.NormalTexture = ExportNormalTextureInfo(normalTex, TextureMapType.Normal, materialObj);
						ExportTextureTransform(material.NormalTexture, materialObj, "_BumpMap");
					}
					else
					{
						Debug.LogFormat(LogType.Error, "Can't export a {0} normal texture in material {1}", normalTex.GetType(), materialObj.name);
					}
				}
			}
			if (materialObj.HasProperty("normalTexture") || materialObj.HasProperty("_NormalTexture"))
			{
				var propName = materialObj.HasProperty("normalTexture") ? "normalTexture" : "_NormalTexture";
				var normalTex = materialObj.GetTexture(propName);

				if (normalTex)
				{
					if(normalTex is Texture2D)
					{
						material.NormalTexture = ExportNormalTextureInfo(normalTex, TextureMapType.Normal, materialObj);
						ExportTextureTransform(material.NormalTexture, materialObj, propName);
					}
					else
					{
						Debug.LogFormat(LogType.Error, "Can't export a {0} normal texture in material {1}", normalTex.GetType(), materialObj.name);
					}
				}
			}

			if (materialObj.HasProperty("_NormalMap"))
			{
				var propName = "_NormalMap";
				var normalTex = materialObj.GetTexture(propName);

				if (normalTex)
				{
					if (normalTex is Texture2D)
					{
						material.NormalTexture = ExportNormalTextureInfo(normalTex, TextureMapType.Normal, materialObj);
						ExportTextureTransform(material.NormalTexture, materialObj, propName);
					}
					else
					{
						Debug.LogFormat(LogType.Error, "Can't export a {0} normal texture in material {1}", normalTex.GetType(), materialObj.name);
					}
				}
			}

			// Generic fallback for custom shaders (ShaderGraph/Amplify/hand-written): find a
			// texture property with a normal-map-like name (e.g. _Normal, NormalTexture, or a
			// _BumpMap on a shader that doesn't use the _NORMALMAP keyword).
			if (material.NormalTexture == null && TryFindShaderProperty(materialObj, _customNormalNames, UnityEngine.Rendering.ShaderPropertyType.Texture, out var customNormalProp))
			{
				var normalTex = materialObj.GetTexture(customNormalProp);
				if (normalTex is Texture2D)
				{
					material.NormalTexture = ExportNormalTextureInfo(normalTex, TextureMapType.Normal, materialObj);
					ExportTextureTransform(material.NormalTexture, materialObj, customNormalProp);
				}
			}

			if (IsUnlit(materialObj))
			{
				ExportUnlit( material, materialObj );
			}
			else if (IsPBRMetallicRoughness(materialObj))
			{
				material.PbrMetallicRoughness = ExportPBRMetallicRoughness(materialObj);
			}
			else if (IsPBRSpecularGlossiness(materialObj))
			{
				ExportPBRSpecularGlossiness(material, materialObj);
			}
			else if (IsCommonConstant(materialObj))
			{
				material.CommonConstant = ExportCommonConstant(materialObj);
			}
			else if (materialObj.HasProperty("_BaseMap"))
			{
				var mainTex = materialObj.GetTexture("_BaseMap");
				material.PbrMetallicRoughness = new PbrMetallicRoughness()
				{
					// unknown custom shader: assume non-metallic, otherwise the glTF defaults
					// (metallic = 1) make the material render like dark metal in viewers
					MetallicFactor = 0,
					RoughnessFactor = 1.0f,
					BaseColorFactor = (materialObj.HasProperty("_BaseColor")
						? materialObj.GetColor("_BaseColor")
						: Color.white).ToNumericsColorLinear(),
					BaseColorTexture = mainTex ? ExportTextureInfo(mainTex, TextureMapType.BaseColor) : null
				};
				ExportTextureTransform(material.PbrMetallicRoughness.BaseColorTexture, materialObj, "_BaseMap");
			}
			else if (materialObj.HasProperty("_ColorTexture"))
			{
				var mainTex = materialObj.GetTexture("_ColorTexture");
				material.PbrMetallicRoughness = new PbrMetallicRoughness()
				{
					MetallicFactor = 0,
					RoughnessFactor = 1.0f,
					BaseColorFactor = (materialObj.HasProperty("_BaseColor")
						? materialObj.GetColor("_BaseColor")
						: Color.white).ToNumericsColorLinear(),
					BaseColorTexture = mainTex ? ExportTextureInfo(mainTex, TextureMapType.BaseColor) : null
				};
				ExportTextureTransform(material.PbrMetallicRoughness.BaseColorTexture, materialObj, "_ColorTexture");
			}
            else if (materialObj.HasProperty("_MainTex")) //else export main texture
            {
                var mainTex = materialObj.GetTexture("_MainTex");

                if (mainTex != null)
                {
                    material.PbrMetallicRoughness = new PbrMetallicRoughness() { MetallicFactor = 0, RoughnessFactor = 1.0f };
                    material.PbrMetallicRoughness.BaseColorTexture = ExportTextureInfo(mainTex, TextureMapType.BaseColor);
                    ExportTextureTransform(material.PbrMetallicRoughness.BaseColorTexture, materialObj, "_MainTex");
                }
                if (materialObj.HasProperty("_TintColor")) //particles use _TintColor instead of _Color
                {
                    if (material.PbrMetallicRoughness == null)
                        material.PbrMetallicRoughness = new PbrMetallicRoughness() { MetallicFactor = 0, RoughnessFactor = 1.0f };

                    material.PbrMetallicRoughness.BaseColorFactor = materialObj.GetColor("_TintColor").ToNumericsColorLinear();
                }
                else if (materialObj.HasProperty("_Color"))
                {
	                if (material.PbrMetallicRoughness == null)
		                material.PbrMetallicRoughness = new PbrMetallicRoughness() { MetallicFactor = 0, RoughnessFactor = 1.0f };

	                material.PbrMetallicRoughness.BaseColorFactor = materialObj.GetColor("_Color").ToNumericsColorLinear();
                }
                material.DoubleSided = true;
            }
			else if (TryFindCustomBaseColorTexture(materialObj, out var customAlbedoProp))
			{
				// generic fallback for custom shaders: albedo-like texture property by name
				// (e.g. _Albedo, AlbedoMap, _MainTexture, ColorTexture, _AlbedoTransparency,
				// _DiffuseMap, _CorneaDiffuseMap, _ScleraDiffuseMap for Reallusion eye/hair shaders)
				var mainTex = materialObj.GetTexture(customAlbedoProp);
				var baseColor = Color.white;
				if (TryFindShaderProperty(materialObj, _customColorFactorNames, UnityEngine.Rendering.ShaderPropertyType.Color, out var customColorProp, allowQualifierPrefix: false))
					baseColor = materialObj.GetColor(customColorProp);

				material.PbrMetallicRoughness = new PbrMetallicRoughness()
				{
					MetallicFactor = 0,
					RoughnessFactor = 1.0f,
					BaseColorFactor = baseColor.ToNumericsColorLinear(),
					BaseColorTexture = mainTex ? ExportTextureInfo(mainTex, TextureMapType.BaseColor) : null
				};
				ExportTextureTransform(material.PbrMetallicRoughness.BaseColorTexture, materialObj, customAlbedoProp);
			}

			if (materialObj.HasProperty("_OcclusionMap") || materialObj.HasProperty("occlusionTexture") || materialObj.HasProperty("_OcclusionTexture") || materialObj.HasProperty("_MaskMap"))
			{
				var propName = materialObj.HasProperty("occlusionTexture") ? "occlusionTexture" :
					materialObj.HasProperty("_OcclusionTexture") ? "_OcclusionTexture" :
					materialObj.HasProperty("_MaskMap") ? "_MaskMap" :
					"_OcclusionMap";

				var occTex = materialObj.GetTexture(propName);
				if (occTex)
				{
					if(occTex is Texture2D)
					{
						TextureId sharedTextureId = null;
						if (material.PbrMetallicRoughness != null &&
						    material.PbrMetallicRoughness.MetallicRoughnessTexture != null)
						{
							if (_occlusionBakedTextures.Contains(materialObj))
							{
								sharedTextureId = material.PbrMetallicRoughness.MetallicRoughnessTexture.Index;
							}
						}
						
						material.OcclusionTexture = ExportOcclusionTextureInfo(occTex, TextureMapType.Occlusion, materialObj, sharedTextureId);
						ExportTextureTransform(material.OcclusionTexture, materialObj, propName);
					}
					else
					{
						Debug.LogFormat(LogType.Error, "Can't export a {0} occlusion texture in material {1}", occTex.GetType(), materialObj.name);
					}
				}
			}
			exportMaterialMarker.End();

			return CreateAndAddMaterialId(materialObj, material);
		}

        private MaterialId CreateAndAddMaterialId(Material materialObj, GLTFMaterial material)
        {
	        var key = materialObj ? GetObjectId(materialObj) : 0;
	        if(!_exportedMaterials.ContainsKey(key))
				_exportedMaterials.Add(key, _root.Materials.Count);

	        var id = new MaterialId
	        {
		        Id = _root.Materials.Count,
		        Root = _root
	        };
	        _root.Materials.Add(material);

	        // after material export
	        if (materialObj)
	        {
		        afterMaterialExportMarker.Begin();
		        foreach (var plugin in _plugins)
			        plugin?.AfterMaterialExport(this, _root, materialObj, material);
		        afterMaterialExportMarker.End();
	        }

	        return id;
        }

		// Generic property-name matching for custom shaders (ShaderGraph/Amplify/hand-written),
		// so their materials still export albedo and normals without editing every material.
		// A property matches when its name — leading underscores stripped, case-insensitive —
		// equals one of the base names, optionally followed by Map/Texture/Tex/Transparency
		// (covers _Albedo, AlbedoMap, _MainTexture, ColorTex, _AlbedoTransparency, ...).
		private static readonly string[] _customAlbedoNames = { "albedo", "main", "color", "base", "basecolor", "diffuse" };
		private static readonly string[] _customNormalNames = { "normal", "bump" };
		private static readonly string[] _customColorFactorNames = { "basecolor", "color", "maincolor", "albedocolor", "tint", "tintcolor" };

		// Optional suffixes a texture property may carry after its base name
		// (e.g. Albedo -> AlbedoMap / AlbedoTexture / AlbedoTex / AlbedoTransparency).
		private static readonly string[] _customTextureSuffixes = { "", "map", "texture", "tex", "transparency" };

		// Explicit, prioritized base-color/diffuse texture properties used by known custom shaders
		// (e.g. Reallusion RL_* eye/hair/skin/teeth). Checked before the generic name matcher so that
		// eyes (cornea/sclera) and hair export their albedo instead of coming out untextured.
		private static readonly string[] _customBaseColorTextureNames =
		{
			"_DiffuseMap", "DiffuseMap",
			"_ScleraDiffuseMap", "ScleraDiffuseMap",
			"_CorneaDiffuseMap", "CorneaDiffuseMap",
			"_AlbedoTransparency", "AlbedoTransparency",
			"_BaseColorMap", "BaseColorMap"
		};

		// Finds a base-color/albedo texture for custom shaders that don't use the standard
		// _MainTex / _BaseMap / _BaseColorTexture names. Prefers the explicit known property names
		// (with an actual texture assigned), then falls back to the generic name matcher.
		private bool TryFindCustomBaseColorTexture(Material material, out string propertyName)
		{
			propertyName = null;
			if (!material) return false;

			foreach (var name in _customBaseColorTextureNames)
			{
				if (material.HasProperty(name) && material.GetTexture(name))
				{
					propertyName = name;
					return true;
				}
			}

			return TryFindShaderProperty(material, _customAlbedoNames, ShaderPropertyType.Texture, out propertyName);
		}

		// allowQualifierPrefix: when true, a leading qualifier before the base name is allowed
		// (e.g. _CorneaDiffuseMap / _ScleraDiffuseMap match "diffuse"). This is intended for texture
		// lookups; color-factor lookups should keep it false so unrelated colors such as _IrisColor
		// or _RootColor are not mistaken for the base color factor.
		private static bool TryFindShaderProperty(Material material, string[] baseNames, UnityEngine.Rendering.ShaderPropertyType type, out string propertyName, bool allowQualifierPrefix = true)
		{
			propertyName = null;
			var shader = material ? material.shader : null;
			if (!shader) return false;

			var count = shader.GetPropertyCount();
			for (var i = 0; i < count; i++)
			{
				if (shader.GetPropertyType(i) != type) continue;
				if (type == UnityEngine.Rendering.ShaderPropertyType.Texture &&
				    shader.GetPropertyTextureDimension(i) != UnityEngine.Rendering.TextureDimension.Tex2D) continue;

				var name = shader.GetPropertyName(i);
				if (MatchesCustomBaseName(name, baseNames, allowQualifierPrefix))
				{
					propertyName = name;
					return true;
				}
			}
			return false;
		}

		// A property matches when its name — leading underscores stripped, case-insensitive — is
		// <baseName><suffix>, optionally with a leading qualifier when allowQualifierPrefix is true
		// (matching on the END rather than only the start). This lets qualifier-prefixed names match:
		// _CorneaDiffuseMap / _ScleraDiffuseMap -> "diffuse", _AlbedoTransparency -> "albedo",
		// while still covering _Albedo, AlbedoMap, _MainTexture, ...
		private static bool MatchesCustomBaseName(string propertyName, string[] baseNames, bool allowQualifierPrefix)
		{
			if (string.IsNullOrEmpty(propertyName)) return false;
			var normalized = propertyName.TrimStart('_').ToLowerInvariant();
			foreach (var baseName in baseNames)
			{
				if (allowQualifierPrefix)
				{
					foreach (var suffix in _customTextureSuffixes)
					{
						var token = baseName + suffix;
						if (normalized == token || normalized.EndsWith(token, StringComparison.Ordinal))
							return true;
					}
				}
				else
				{
					if (!normalized.StartsWith(baseName, StringComparison.Ordinal)) continue;
					var suffix = normalized.Substring(baseName.Length);
					if (suffix.Length == 0 || suffix == "map" || suffix == "texture" || suffix == "tex" || suffix == "transparency")
						return true;
				}
			}
			return false;
		}

        private bool IsPBRMetallicRoughness(Material material)
        {
	        return (material.HasProperty("_Metallic") || material.HasProperty("_MetallicFactor") || material.HasProperty("metallicFactor")) &&
	               (material.HasProperty("_MetallicGlossMap") || material.HasProperty("_Glossiness") ||
	                material.HasProperty("_Roughness") || material.HasProperty("_RoughnessFactor") || material.HasProperty("roughnessFactor") ||
					material.HasProperty("_MetallicRoughnessTexture") || material.HasProperty("metallicRoughnessTexture") ||
					material.HasProperty("_Smoothness"));
        }

        private bool IsUnlit(Material material)
        {
	        return material.shader.name.ToLowerInvariant().Contains("unlit") || material.shader.name == "Sprites/Default";
        }

        private bool IsPBRSpecularGlossiness(Material material)
        {
	        return material.HasProperty("_SpecColor") && material.HasProperty("_SpecGlossMap");
        }

        private bool IsCommonConstant(Material material)
        {
	        return material.HasProperty("_AmbientFactor") &&
	               material.HasProperty("_LightMap") &&
	               material.HasProperty("_LightFactor");
        }

#if UNITY_2019_1_OR_NEWER
        private static bool CheckForPropertyInShader(Shader shader, string name, ShaderPropertyType type)
        {
	        // TODO result can be cached, we might do many similar checks for one export

	        var c = shader.GetPropertyCount();
	        var foundProperty = false;
	        for (var i = 0; i < c; i++)
	        {
		        if (shader.GetPropertyName(i) == name && shader.GetPropertyType(i) == type)
		        {
			        foundProperty = true;
			        break;
		        }
	        }
	        return foundProperty;
        }
#endif

		private int GetUvChannel(Material mat, string texName)
		{
			if (mat == null) return 0;

			var uvProp = texName + "TexCoord";
#if UNITY_2021_1_OR_NEWER
			if (mat.HasFloat(uvProp))
#else
			if (mat.HasProperty(uvProp)
#if UNITY_2019_1_OR_NEWER
				&& CheckForPropertyInShader(mat.shader, uvProp, ShaderPropertyType.Float)
#endif
			)
#endif
				return Mathf.RoundToInt(mat.GetFloat(uvProp));

			return 0;
		}

		private bool ExportTextureTransform(TextureInfo def, Material mat, string texName)
		{
			if (def == null) return false;

			// early out if texture transform is explicitly disabled
			if (mat.HasProperty("_TEXTURE_TRANSFORM") && !mat.IsKeywordEnabled("_TEXTURE_TRANSFORM_ON"))
			{
				// even without texture transform, set the uv channel in case the user has modified it (normally the slider is not visible in the inspector but if texture transform was enabled, uv modified and then disabled the uv channel setting will still be used by Unity and should then be exported)
				def.TexCoord = GetUvChannel(mat, texName);
				return false;
			}

			Vector2 offset = mat.GetTextureOffset(texName);
			Vector2 scale = mat.GetTextureScale(texName);
			//var rotationMatrix = mat.GetVector(texName + "Rotation");
			//var rotation = -Mathf.Atan2(offset.y, rotationMatrix.x);
			var rotProp = texName + "Rotation";
			var uvProp = texName + "TexCoord";
			var rotation = 0f;
			var uvChannel = 0f;

#if UNITY_2021_1_OR_NEWER
			if (mat.HasFloat(rotProp))
#else
			if (mat.HasProperty(rotProp)
#if UNITY_2019_1_OR_NEWER
				&& CheckForPropertyInShader(mat.shader, rotProp, ShaderPropertyType.Float)
#endif
			)
#endif
				rotation = mat.GetFloat(rotProp);

			uvChannel = GetUvChannel(mat, texName);



			if (offset == Vector2.zero && scale == Vector2.one && rotation == 0)
			{
				var checkForName = texName + "_ST";
				// Debug.Log("Checking for property: " + checkForName + " : " + mat.HasProperty(checkForName) + " == " + (mat.HasProperty(checkForName) ? mat.GetVector(checkForName) : "null"));
				var textureHasTilingOffset = mat.HasProperty(checkForName);

#if UNITY_2019_1_OR_NEWER
				// turns out we have to check extra hard if that property actually exists
				// the material ALWAYS says true for mat.HasProperty(someTex_ST) when someTex is defined and doesn't have [NoTextureScale] attribute
				if (textureHasTilingOffset)
				{
					if (!CheckForPropertyInShader(mat.shader, checkForName, ShaderPropertyType.Vector))
						textureHasTilingOffset = false;
				}
#endif

				if (textureHasTilingOffset)
				{
					// ignore, texture has explicit _ST property
				}
				else if(mat.HasProperty("_MainTex_ST") || mat.HasProperty("_BaseMap_ST") || mat.HasProperty("_BaseColorMap_ST") || mat.HasProperty("_BaseColorTexture_ST") || mat.HasProperty("baseColorTexture_ST"))
				{
					// difficult choice here: some shaders might support texture transform per-texture, others use the main transform.
					if (mat.HasProperty("baseColorTexture"))
					{
						offset = mat.GetTextureOffset("baseColorTexture");
						scale = mat.GetTextureScale("baseColorTexture");
						rotation = mat.HasProperty("baseColorTextureRotation") ? mat.GetFloat("baseColorTextureRotation") : 0;
					}
					else if (mat.HasProperty("_BaseColorTexture"))
					{
						offset = mat.GetTextureOffset("_BaseColorTexture");
						scale = mat.GetTextureScale("_BaseColorTexture");
						rotation = mat.HasProperty("_BaseColorTextureRotation") ? mat.GetFloat("_BaseColorTextureRotation") : 0;
					}
					else if (mat.HasProperty("_BaseColorMap"))
					{
						offset = mat.GetTextureOffset("_BaseColorMap");
						scale = mat.GetTextureScale("_BaseColorMap");
					}
					else if (mat.HasProperty("_BaseMap"))
					{
						offset = mat.GetTextureOffset("_BaseMap");
						scale = mat.GetTextureScale("_BaseMap");
					}
					else if(mat.HasProperty("_MainTex"))
					{
						offset = mat.mainTextureOffset;
						scale = mat.mainTextureScale;
						rotation = 0;
#if UNITY_2021_1_OR_NEWER
						if (mat.HasFloat("_MainTexRotation"))
#else
						if (mat.HasProperty("_MainTexRotation"))
#endif
							rotation = mat.GetFloat("_MainTexRotation");
					}
				}
				else
				{
					offset = Vector2.zero;
					scale = Vector2.one;
				}
			}

			if (_root.ExtensionsUsed == null)
			{
				_root.ExtensionsUsed = new List<string>(
					new[] { ExtTextureTransformExtensionFactory.EXTENSION_NAME }
				);
			}
			else if (!_root.ExtensionsUsed.Contains(ExtTextureTransformExtensionFactory.EXTENSION_NAME))
			{
				_root.ExtensionsUsed.Add(ExtTextureTransformExtensionFactory.EXTENSION_NAME);
			}
			
			if (def.Extensions == null)
				def.Extensions = new Dictionary<string, IExtension>();

			def.Extensions[ExtTextureTransformExtensionFactory.EXTENSION_NAME] = new ExtTextureTransformExtension(
				new GLTF.Math.Vector2(offset.x, 1 - offset.y - scale.y),
				rotation,
				new GLTF.Math.Vector2(scale.x, scale.y),
				 (int)uvChannel
			);

			return true;
		}

		public NormalTextureInfo ExportNormalTextureInfo(
			Texture texture,
			string textureSlot,
			Material material)
		{
			const string normalMapFormatIsXYZ = "_NormalMapFormatXYZ";
			
			var info = new NormalTextureInfo();
			TextureExportSettings exportSettings = default;

#if UNITY_2021_1_OR_NEWER
			if (material.HasFloat(normalMapFormatIsXYZ))
#else
			if (material.HasProperty(normalMapFormatIsXYZ)
#if UNITY_2019_1_OR_NEWER
				&& CheckForPropertyInShader(material.shader, normalMapFormatIsXYZ, ShaderPropertyType.Float)
#endif
			)
#endif			
			if (material.GetFloat(normalMapFormatIsXYZ) >= 1)
			{
				exportSettings.linear = true;
				exportSettings.isValid = true;
				exportSettings.alphaMode = TextureExportSettings.AlphaMode.Never;
				exportSettings.conversion = TextureExportSettings.Conversion.None;
			}
			
			info.Index = ExportTexture(texture, textureSlot, exportSettings);

			if (material.HasProperty("normalScale"))
			{
				info.Scale = material.GetFloat("normalScale");
			}
			else if (material.HasProperty("_NormalScale"))
			{
				info.Scale = material.GetFloat("_NormalScale");
			}
			else if (material.HasProperty("_BumpScale"))
			{
				info.Scale = material.GetFloat("_BumpScale");
			}


			return info;
		}

		private OcclusionTextureInfo ExportOcclusionTextureInfo(
			Texture texture,
			string textureSlot,
			Material material, TextureId sharedTextureId = null)
		{
			var info = new OcclusionTextureInfo();

			if (sharedTextureId != null)
				info.Index = sharedTextureId;
			else
				info.Index = ExportTexture(texture, textureSlot);

			if (material.HasProperty("occlusionStrength"))
			{
				info.Strength = material.GetFloat("occlusionStrength");
			}
			else if (material.HasProperty("_OcclusionStrength"))
			{
				info.Strength = material.GetFloat("_OcclusionStrength");
			}

			return info;
		}

		public PbrMetallicRoughness ExportPBRMetallicRoughness(Material material)
		{
			var pbr = new PbrMetallicRoughness() { MetallicFactor = 0, RoughnessFactor = 1.0f };
			var isGltfPbrMetallicRoughnessShader = material.shader.name.Equals("GLTF/PbrMetallicRoughness", StringComparison.Ordinal);
			var isGlTFastShader = material.shader.name.Equals("glTF/PbrMetallicRoughness", StringComparison.Ordinal);

			if (material.HasProperty("baseColorFactor"))
			{
				pbr.BaseColorFactor = material.GetColor("baseColorFactor").ToNumericsColorLinear();
			}
			else if (material.HasProperty("_BaseColorFactor"))
			{
				pbr.BaseColorFactor = material.GetColor("_BaseColorFactor").ToNumericsColorLinear();
			}
			else if (material.HasProperty("_BaseColor"))
			{
				pbr.BaseColorFactor = material.GetColor("_BaseColor").ToNumericsColorLinear();
			}
			else if (material.HasProperty("_Color"))
			{
				pbr.BaseColorFactor = material.GetColor("_Color").ToNumericsColorLinear();
			}

            if (material.HasProperty("_TintColor")) //particles use _TintColor instead of _Color
            {
                float white = 1;
                if (material.HasProperty("_Color"))
                {
                    var c = material.GetColor("_Color");
                    white = (c.r + c.g + c.b) / 3.0f; //multiply alpha by overall whiteness of TintColor
                }

                pbr.BaseColorFactor = (material.GetColor("_TintColor") * white).ToNumericsColorLinear() ;
            }

            if (material.HasProperty("_MainTex") || material.HasProperty("_BaseMap") || material.HasProperty("_BaseColorTexture") || material.HasProperty("baseColorTexture")) //TODO if additive particle, render black into alpha
			{
				// TODO use private Material.GetFirstPropertyNameIdByAttribute here, supported from 2020.1+
				var mainTexPropertyName = material.HasProperty("_BaseMap") ? "_BaseMap" : material.HasProperty("_MainTex") ? "_MainTex" : material.HasProperty("baseColorTexture") ? "baseColorTexture" : "_BaseColorTexture";
				var mainTex = material.GetTexture(mainTexPropertyName);

				if (mainTex)
				{
					pbr.BaseColorTexture = ExportTextureInfo(mainTex, TextureMapType.BaseColor);
					ExportTextureTransform(pbr.BaseColorTexture, material, mainTexPropertyName);
					pbr.BaseColorTexture.TexCoord = material.HasProperty("baseColorTextureTexCoord") ?
						Mathf.RoundToInt(material.GetFloat("baseColorTextureTexCoord")) :
						material.HasProperty("_BaseColorTextureTexCoord") ?
							Mathf.RoundToInt(material.GetFloat("_BaseColorTextureTexCoord")) : 0;
				}
			}

			// Fallback for custom shaders (e.g. Reallusion RL_* eye/hair/skin/teeth) that are treated
			// as PBR metallic-roughness but name their albedo differently (_CorneaDiffuseMap,
			// _ScleraDiffuseMap, _DiffuseMap, _AlbedoTransparency, ...). Without this, eyes and hair
			// export untextured.
			if (pbr.BaseColorTexture == null && TryFindCustomBaseColorTexture(material, out var customBaseColorProp))
			{
				var customBaseColorTex = material.GetTexture(customBaseColorProp);
				if (customBaseColorTex)
				{
					pbr.BaseColorTexture = ExportTextureInfo(customBaseColorTex, TextureMapType.BaseColor);
					ExportTextureTransform(pbr.BaseColorTexture, material, customBaseColorProp);
				}
			}

            var ignoreMetallicFactor = (material.IsKeywordEnabled("_METALLICGLOSSMAP") || material.IsKeywordEnabled("_METALLICSPECGLOSSMAP")) && !isGltfPbrMetallicRoughnessShader && !isGlTFastShader;
            if (material.HasProperty("metallicFactor") && !ignoreMetallicFactor)
            {
	            pbr.MetallicFactor = material.GetFloat("metallicFactor");
            }
            else if (material.HasProperty("_MetallicFactor") && !ignoreMetallicFactor)
            {
	            pbr.MetallicFactor = material.GetFloat("_MetallicFactor");
            }
            else if (material.HasProperty("_Metallic") && !ignoreMetallicFactor)
			{
				pbr.MetallicFactor = material.GetFloat("_Metallic");
			}

            var needToBakeRoughnessIntoTexture = false;
            float roughnessMultiplier = 1f;
            bool occlusionGetBakedIntoMetallicRoughness = false;

            if (material.HasProperty("roughnessFactor"))
            {
	            float roughness = material.GetFloat("roughnessFactor");
	            pbr.RoughnessFactor = roughness;
            }
            else if (material.HasProperty("_RoughnessFactor"))
            {
	            float roughness = material.GetFloat("_RoughnessFactor");
	            pbr.RoughnessFactor = roughness;
            }
            else if (material.HasProperty("_Roughness"))
			{
				float roughness = material.GetFloat("_Roughness");
				pbr.RoughnessFactor = roughness;
			}
			else if (material.HasProperty("_Glossiness") || material.HasProperty("_Smoothness"))
			{
				var smoothnessPropertyName = material.HasProperty("_Smoothness") ? "_Smoothness" : "_Glossiness";
				var metallicGlossMap = material.HasProperty("_MetallicGlossMap") ? material.GetTexture("_MetallicGlossMap") : null;
				float smoothness = material.GetFloat(smoothnessPropertyName);

				// legacy workaround: the UnityGLTF shaders misuse "_Glossiness" as roughness but don't have a keyword for it.
				if (isGltfPbrMetallicRoughnessShader)
					smoothness = 1 - smoothness;
				if (metallicGlossMap && material.HasProperty("_GlossMapScale") && material.IsKeywordEnabled("_METALLICGLOSSMAP"))
					smoothness = material.GetFloat("_GlossMapScale");

				var hasMetallicRoughnessMap =
					(material.HasProperty("metallicRoughnessTexture") && material.GetTexture("metallicRoughnessTexture")) ||
					(material.HasProperty("_MetallicRoughnessTexture") && material.GetTexture("_MetallicRoughnessTexture")) ||
					(material.HasProperty("_MetallicGlossMap") && material.GetTexture("_MetallicGlossMap"));

				if (!hasMetallicRoughnessMap)
					pbr.RoughnessFactor = 1 - smoothness;
				else
				{
					needToBakeRoughnessIntoTexture = true;
					roughnessMultiplier = 1 - smoothness;
					pbr.RoughnessFactor = 1;
				}
			}

			if (material.HasProperty("metallicRoughnessTexture"))
			{
				var mrTex = material.GetTexture("metallicRoughnessTexture");
				if (mrTex)
				{
					pbr.MetallicRoughnessTexture = ExportTextureInfo(mrTex, TextureMapType.MetallicRoughness);
					ExportTextureTransform(pbr.MetallicRoughnessTexture, material, "metallicRoughnessTexture");
				}
			}
			else if (material.HasProperty("_MetallicRoughnessTexture"))
			{
				var mrTex = material.GetTexture("_MetallicRoughnessTexture");
				if (mrTex)
				{
					pbr.MetallicRoughnessTexture = ExportTextureInfo(mrTex, TextureMapType.MetallicRoughness);
					ExportTextureTransform(pbr.MetallicRoughnessTexture, material, "_MetallicRoughnessTexture");
				}
			}
			else if (material.HasProperty("_MetallicGlossMap"))
			{
				var mrTex = material.GetTexture("_MetallicGlossMap");

				if (mrTex)
				{
					Texture occlusionTex;
					if (material.HasProperty("_OcclusionMap"))
					{
						occlusionTex = material.GetTexture("_OcclusionMap");
						if (occlusionTex != null && occlusionTex == mrTex)
						{
							occlusionGetBakedIntoMetallicRoughness = true;
						}
					}
					
					var conversion = GetExportSettingsForSlot((isGltfPbrMetallicRoughnessShader || isGlTFastShader) ? TextureMapType.Linear : TextureMapType.MetallicGloss);
					if (needToBakeRoughnessIntoTexture)
					{
						conversion = new TextureExportSettings(conversion);
						conversion.smoothnessRangeMax = 1 - roughnessMultiplier;
					}
					
					if (occlusionGetBakedIntoMetallicRoughness)
					{
						conversion.conversion = TextureExportSettings.Conversion.MetalGlossOcclusionChannelSwap;
						_occlusionBakedTextures.Add(material);
					}
					
					pbr.MetallicRoughnessTexture = ExportTextureInfo(mrTex, TextureMapType.MetallicRoughness, conversion);
					// in the Standard shader, _METALLICGLOSSMAP replaces _Metallic and so we need to set the multiplier to 1;
					// that's not true for the gltf shaders though, so we keep the value there.
					if (ignoreMetallicFactor)
						pbr.MetallicFactor = 1.0f;
					ExportTextureTransform(pbr.MetallicRoughnessTexture, material, "_MetallicGlossMap");
				}
			}
			else if(material.HasProperty("_MaskMap"))
			{
				var mrTex = material.GetTexture("_MaskMap");

				if (mrTex)
				{
					// bake remapping into texture during export
					var conversion = GetExportSettingsForSlot(TextureMapType.MetallicGloss);

					conversion.metallicRangeMin = material.GetFloat("_MetallicRemapMin");
					conversion.metallicRangeMax = material.GetFloat("_MetallicRemapMax");
					conversion.smoothnessRangeMin = material.GetFloat("_SmoothnessRemapMin");
					conversion.smoothnessRangeMax = material.GetFloat("_SmoothnessRemapMax");
					conversion.occlusionRangeMin = material.GetFloat("_AORemapMin");
					conversion.occlusionRangeMax = material.GetFloat("_AORemapMax");
					
					conversion.conversion = TextureExportSettings.Conversion.MetalGlossOcclusionChannelSwap;
					_occlusionBakedTextures.Add(material);

					// set factors to 1 because of baked values
					pbr.MetallicFactor = 1f;
					pbr.RoughnessFactor = 1f;

					pbr.MetallicRoughnessTexture = ExportTextureInfo(mrTex, TextureMapType.MetallicRoughness, conversion);

					ExportTextureTransform(pbr.MetallicRoughnessTexture, material, "_MaskMap");
				}
			}

			return pbr;
		}

		public void ExportUnlit(GLTFMaterial def, Material material){

			const string extname = KHR_MaterialsUnlitExtensionFactory.EXTENSION_NAME;
			DeclareExtensionUsage( extname, true );
			def.AddExtension( extname, new KHR_MaterialsUnlitExtension());

			var pbr = new PbrMetallicRoughness();

			if (material.HasProperty("baseColorFactor"))
			{
				pbr.BaseColorFactor = material.GetColor("baseColorFactor").ToNumericsColorLinear();
			}
			else if (material.HasProperty("_BaseColorFactor"))
			{
				pbr.BaseColorFactor = material.GetColor("_BaseColorFactor").ToNumericsColorLinear();
			}
			else if (material.HasProperty("_BaseColor"))
			{
				pbr.BaseColorFactor = material.GetColor("_BaseColor").ToNumericsColorLinear();
			}
			else if (material.HasProperty("_Color"))
			{
				pbr.BaseColorFactor = material.GetColor("_Color").ToNumericsColorLinear();
			}

			if (material.HasProperty("baseColorTexture"))
			{
				var mainTex = material.GetTexture("baseColorTexture");
				if (mainTex)
				{
					pbr.BaseColorTexture = ExportTextureInfo(mainTex, TextureMapType.BaseColor);
					ExportTextureTransform(pbr.BaseColorTexture, material, "baseColorTexture");
				}
			}
			else if (material.HasProperty("_BaseColorTexture"))
			{
				var mainTex = material.GetTexture("_BaseColorTexture");
				if (mainTex)
				{
					pbr.BaseColorTexture = ExportTextureInfo(mainTex, TextureMapType.BaseColor);
					ExportTextureTransform(pbr.BaseColorTexture, material, "_BaseColorTexture");
				}
			}
			else if (material.HasProperty("_BaseMap"))
			{
				var mainTex = material.GetTexture("_BaseMap");
				if (mainTex)
				{
					pbr.BaseColorTexture = ExportTextureInfo(mainTex, TextureMapType.BaseColor);
					ExportTextureTransform(pbr.BaseColorTexture, material, "_BaseMap");
				}
			}
			else if (material.HasProperty("_MainTex"))
			{
				var mainTex = material.GetTexture("_MainTex");
				if (mainTex)
				{
					pbr.BaseColorTexture = ExportTextureInfo(mainTex, TextureMapType.BaseColor);
					ExportTextureTransform(pbr.BaseColorTexture, material, "_MainTex");
				}
			}

			def.PbrMetallicRoughness = pbr;

		}

		private void ExportPBRSpecularGlossiness(GLTFMaterial material, Material materialObj)
		{
			if (_root.ExtensionsUsed == null)
			{
				_root.ExtensionsUsed = new List<string>(new[] { "KHR_materials_pbrSpecularGlossiness" });
			}
			else if (!_root.ExtensionsUsed.Contains("KHR_materials_pbrSpecularGlossiness"))
			{
				_root.ExtensionsUsed.Add("KHR_materials_pbrSpecularGlossiness");
			}
			
			if (material.Extensions == null)
			{
				material.Extensions = new Dictionary<string, IExtension>();
			}

			GLTF.Math.Color diffuseFactor = KHR_materials_pbrSpecularGlossinessExtension.DIFFUSE_FACTOR_DEFAULT;
			TextureInfo diffuseTexture = KHR_materials_pbrSpecularGlossinessExtension.DIFFUSE_TEXTURE_DEFAULT;
			GLTF.Math.Vector3 specularFactor = KHR_materials_pbrSpecularGlossinessExtension.SPEC_FACTOR_DEFAULT;
			double glossinessFactor = KHR_materials_pbrSpecularGlossinessExtension.GLOSS_FACTOR_DEFAULT;
			TextureInfo specularGlossinessTexture = KHR_materials_pbrSpecularGlossinessExtension.SPECULAR_GLOSSINESS_TEXTURE_DEFAULT;

			if (materialObj.HasProperty("_BaseColor"))
			{
				diffuseFactor = materialObj.GetColor("_BaseColor").ToNumericsColorLinear();
			}
			else if (materialObj.HasProperty("_Color"))
			{
				diffuseFactor = materialObj.GetColor("_Color").ToNumericsColorLinear();
			}

			if (materialObj.HasProperty("_BaseMap"))
			{
				var mainTex = materialObj.GetTexture("_BaseMap");

				if (mainTex != null)
				{
					diffuseTexture = ExportTextureInfo(mainTex, TextureMapType.BaseColor);
					ExportTextureTransform(diffuseTexture, materialObj, "_BaseMap");
				}
			}
			else if (materialObj.HasProperty("_MainTex"))
			{
				var mainTex = materialObj.GetTexture("_MainTex");

				if (mainTex != null)
				{
					diffuseTexture = ExportTextureInfo(mainTex, TextureMapType.BaseColor);
					ExportTextureTransform(diffuseTexture, materialObj, "_MainTex");
				}
			}

			if (materialObj.HasProperty("_SpecColor"))
			{
				var specGlossMap = materialObj.GetTexture("_SpecGlossMap");
				if (specGlossMap == null)
				{
					var specColor = materialObj.GetColor("_SpecColor").ToNumericsColorLinear();
					specularFactor = new GLTF.Math.Vector3(specColor.R, specColor.G, specColor.B);
				}
			}

			if (materialObj.HasProperty("_Glossiness"))
			{
				var specGlossMap = materialObj.GetTexture("_SpecGlossMap");
				if (specGlossMap == null)
				{
					glossinessFactor = materialObj.GetFloat("_Glossiness");
				}
			}

			if (materialObj.HasProperty("_SpecGlossMap"))
			{
				var mgTex = materialObj.GetTexture("_SpecGlossMap");

				if (mgTex)
				{
					specularGlossinessTexture = ExportTextureInfo(mgTex, TextureMapType.SpecGloss);
					ExportTextureTransform(specularGlossinessTexture, materialObj, "_SpecGlossMap");
				}
			}

			material.Extensions[KHR_materials_pbrSpecularGlossinessExtensionFactory.EXTENSION_NAME] = new KHR_materials_pbrSpecularGlossinessExtension(
				diffuseFactor,
				diffuseTexture,
				specularFactor,
				glossinessFactor,
				specularGlossinessTexture
			);

			// Fallback for viewers without KHR_materials_pbrSpecularGlossiness support (e.g.
			// current three.js): approximate as a non-metallic material with roughness derived
			// from glossiness. Without this, such viewers use the glTF defaults (metallic = 1,
			// roughness = 1) and the material renders like dark rough metal.
			if (material.PbrMetallicRoughness == null)
				material.PbrMetallicRoughness = new PbrMetallicRoughness();
			material.PbrMetallicRoughness.MetallicFactor = 0;
			material.PbrMetallicRoughness.RoughnessFactor = 1.0 - glossinessFactor;
			material.PbrMetallicRoughness.BaseColorFactor = diffuseFactor;
			material.PbrMetallicRoughness.BaseColorTexture = diffuseTexture;
		}

		private MaterialCommonConstant ExportCommonConstant(Material materialObj)
		{
			if (_root.ExtensionsUsed == null)
			{
				_root.ExtensionsUsed = new List<string>(new[] { "KHR_materials_common" });
			}
			else if (!_root.ExtensionsUsed.Contains("KHR_materials_common"))
			{
				_root.ExtensionsUsed.Add("KHR_materials_common");
			}
			
			var constant = new MaterialCommonConstant();

			if (materialObj.HasProperty("_AmbientFactor"))
			{
				constant.AmbientFactor = materialObj.GetColor("_AmbientFactor").ToNumericsColorRaw();
			}

			if (materialObj.HasProperty("_LightMap"))
			{
				var lmTex = materialObj.GetTexture("_LightMap");

				if (lmTex)
				{
					constant.LightmapTexture = ExportTextureInfo(lmTex, TextureMapType.Linear);
					ExportTextureTransform(constant.LightmapTexture, materialObj, "_LightMap");
				}

			}

			if (materialObj.HasProperty("_LightFactor"))
			{
				constant.LightmapFactor = materialObj.GetColor("_LightFactor").ToNumericsColorRaw();
			}

			return constant;
		}

		// TODO make internal
		public Texture GetSourceTextureForExportedTexture(GLTFTexture exported)
		{
			var textureIndex = _root.Textures.FindIndex(x => x == exported);
			return _textures[textureIndex].Texture;
		}

		// from HDRP's HDUtils.ConvertHDRColorToLDR
		internal static void ConvertHDRColorToLDR(Color hdr, out Color ldr, out float intensity)
		{
			// specifies the max byte value to use when decomposing a float color into bytes with exposure
			// this is the value used by Photoshop
			const float k_MaxByteForOverexposedColor = 191;

			hdr.a = 1.0f;
			ldr = hdr;
			intensity = 1.0f;

			var maxColorComponent = hdr.maxColorComponent;
			if (maxColorComponent != 0f)
			{
				// calibrate exposure to the max float color component
				var scaleFactor = k_MaxByteForOverexposedColor / maxColorComponent;

				ldr.r = Mathf.Min(k_MaxByteForOverexposedColor, scaleFactor * hdr.r) / 255f;
				ldr.g = Mathf.Min(k_MaxByteForOverexposedColor, scaleFactor * hdr.g) / 255f;
				ldr.b = Mathf.Min(k_MaxByteForOverexposedColor, scaleFactor * hdr.b) / 255f;

				intensity = 255f / scaleFactor;
			}
		}
	}
}
