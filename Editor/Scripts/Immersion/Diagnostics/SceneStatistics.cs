using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class SceneStatistics : EditorWindow
{
    public class SceneData
    {
        public List<Material> Materials = new List<Material>();
        
        public void Clear()
        {
            Materials.Clear();
        }
    }
    
    private SceneData sceneData = new SceneData();
    private Vector2 scrollPosition;
    
    
    [MenuItem("Revolution/Scene statistics")]
    public static void Open()
    {
        var window = GetWindow<SceneStatistics>("Scene statistics");
        window.minSize = new Vector2(320, 180);
        window.Show();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Scene statistics", EditorStyles.boldLabel);

        EditorGUILayout.Space(8);

        if (GUILayout.Button("Gather stats"))
        {
            sceneData.Clear();
            var gameObjects = GameObject.FindObjectsOfType<GameObject>();
            foreach (var gameObject in gameObjects)
            {
                var renderer = gameObject.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var materials = renderer.sharedMaterials;
                    if (materials != null)
                    {
                        foreach (var material in materials)
                        {
                            if (material != null)
                            {
                                if (!sceneData.Materials.Contains(material))
                                {
                                    sceneData.Materials.Add(material);
                                }
                            }
                        }
                    }
                }
            }
        }
        GUILayout.Space(8);
        GUILayout.Label($"Materials: {sceneData.Materials.Count}");
        scrollPosition = GUILayout.BeginScrollView(scrollPosition);
        GUILayout.BeginVertical();
        foreach (var material in sceneData.Materials)
        {
            string materialName = material.name;
            string materialShaderName = material.shader.name;
            
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(materialName, GUILayout.Width(200));
            EditorGUILayout.LabelField(materialShaderName, GUILayout.Width(300));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Select"))
            {
                Selection.activeObject = material;
            }
            if (GUILayout.Button("Convert"))
            {
                ConvertToNewShader(material);
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
        GUILayout.EndScrollView();
    }

    private void ConvertToNewShader(Material material)
    {
        material.shader = Shader.Find("Immersion/Gi/SimpleLitGi");
        var oldColorTexture = material.GetTexture("_BaseMap");
        material.SetTexture("_BaseMapPbr", oldColorTexture);
    }
}