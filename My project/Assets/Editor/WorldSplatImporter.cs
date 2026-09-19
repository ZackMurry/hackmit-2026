using System.IO;
using System.Reflection;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Converts a WorldLabs .spz (kept in the repo-level Assets/Worlds folder, outside
/// the Unity project) into a GaussianSplatAsset and points SampleScene's
/// GaussianSplatRenderer + WorldColliderLoader at it. Drives the package's
/// GaussianSplatAssetCreator window through reflection so the conversion can run
/// without clicking through the UI.
/// </summary>
public static class WorldSplatImporter
{
    const string kOutputFolder = "Assets/GaussianAssets";
    const string kScenePath = "Assets/Scenes/SampleScene.unity";

    const string kModernHouseSpz = "ModernHouse/modern_house_with_lush_landscaping_2m.spz";
    const string kModernHouseCollider = "Worlds/modern_house_with_lush_landscaping_collider.glb";

    // Repo-level Assets/Worlds, i.e. <repo>/Assets/Worlds, not <repo>/My project/Assets.
    static string WorldsDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../Assets/Worlds"));

    [MenuItem("Tools/Worlds/Use Modern House")]
    static void UseModernHouse() => UseWorld(kModernHouseSpz, kModernHouseCollider);

    // Runs the Modern House import once, the first time the editor loads without the
    // converted asset present. No-op afterwards.
    [InitializeOnLoadMethod]
    static void AutoImportModernHouse()
    {
        if (AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(AssetPathFor(kModernHouseSpz)) != null)
            return;
        if (!File.Exists(Path.Combine(WorldsDir, kModernHouseSpz)))
            return;
        EditorApplication.delayCall += UseModernHouse;
    }

    static string AssetPathFor(string spzRelative) =>
        $"{kOutputFolder}/{Path.GetFileNameWithoutExtension(spzRelative)}.asset";

    static void UseWorld(string spzRelative, string colliderFile)
    {
        string spzPath = Path.Combine(WorldsDir, spzRelative);
        if (!File.Exists(spzPath))
        {
            Debug.LogError($"WorldSplatImporter: .spz not found at {spzPath}");
            return;
        }

        var asset = CreateSplatAsset(spzPath);
        if (asset == null)
            return;

        PatchScene(asset, colliderFile);
        Debug.Log($"WorldSplatImporter: {kScenePath} now uses {AssetDatabase.GetAssetPath(asset)} (collider: {colliderFile})");
    }

    static GaussianSplatAsset CreateSplatAsset(string spzPath)
    {
        string assetPath = AssetPathFor(spzPath);
        var existing = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
        if (existing != null)
            return existing;

        const BindingFlags kPrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(GaussianSplatAssetCreator);
        var creator = ScriptableObject.CreateInstance<GaussianSplatAssetCreator>();
        try
        {
            type.GetField("m_InputFile", kPrivate).SetValue(creator, spzPath);
            type.GetField("m_ImportCameras", kPrivate).SetValue(creator, false);
            type.GetField("m_OutputFolder", kPrivate).SetValue(creator, kOutputFolder);

            // Awake() pulls the last quality picked in the window from EditorPrefs;
            // pin Medium so every world is converted the same way as RusticKitchen.
            var qualityField = type.GetField("m_Quality", kPrivate);
            qualityField.SetValue(creator, System.Enum.Parse(qualityField.FieldType, "Medium"));
            type.GetMethod("ApplyQualityLevel", kPrivate).Invoke(creator, null);

            type.GetMethod("CreateAsset", kPrivate).Invoke(creator, null);

            var error = (string)type.GetField("m_ErrorMessage", kPrivate).GetValue(creator);
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError($"WorldSplatImporter: {error}");
                return null;
            }
        }
        finally
        {
            Object.DestroyImmediate(creator);
        }

        var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
        if (asset == null)
            Debug.LogError($"WorldSplatImporter: expected asset at {assetPath} after conversion");
        return asset;
    }

    static void PatchScene(GaussianSplatAsset asset, string colliderFile)
    {
        var scene = SceneManager.GetSceneByPath(kScenePath);
        bool wasOpen = scene.isLoaded;
        if (!wasOpen)
            scene = EditorSceneManager.OpenScene(kScenePath, OpenSceneMode.Additive);

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var renderer in root.GetComponentsInChildren<GaussianSplatRenderer>(true))
            {
                Undo.RecordObject(renderer, "Set world splat");
                renderer.m_Asset = asset;
                EditorUtility.SetDirty(renderer);
            }
            foreach (var loader in root.GetComponentsInChildren<WorldColliderLoader>(true))
            {
                Undo.RecordObject(loader, "Set world collider");
                loader.colliderFile = colliderFile;
                EditorUtility.SetDirty(loader);
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        if (!wasOpen)
            EditorSceneManager.CloseScene(scene, true);
    }
}
