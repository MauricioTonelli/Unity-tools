using System;
using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;
using Object = UnityEngine.Object;

public class Assets_Renamer : EditorWindow
{
    #region Variables

    //Rename Variables
    private Object[] RenameAssets = new Object[0];
    private string _prefix, _name, _suffix;
    private bool addNumbering;
    private bool useAutoPrefix;
    private bool _resetString = false;

    //Lists
    private List<Object> prefabsList = new List<Object>();
    private List<Material> materialsList = new List<Material>();
    private List<Texture2D> texturesList = new List<Texture2D>();
    private List<Sprite> spritesList = new List<Sprite>();
    private List<GameObject> gameobjectsList;

    //Strings
    private string _sAutoPrefix = "Autoprefix Enabled";
    private string _sAutoPrefabPrefix = "P";
    private string _sAutoMaterialPrefix = "M";
    private string _sAutoSpritePrefix = "S";
    private string _sAutoTexturePrefix = "T";

    #endregion

    [MenuItem("Tools/Asset Renamer")]
    public static void LaunchRenamer()
    {
        EditorWindow window = GetWindow(typeof(Assets_Renamer));
        window.titleContent = new GUIContent("Rename Selected Assets");
        window.Show();
    }

    #region Built-in Methods
    private void OnGUI()
    {
        GUILayout.Label("Selected Objects: " + RenameAssets.Length);

        EditorGUILayout.Space();
        if (useAutoPrefix)
        {
            _prefix = EditorGUILayout.TextField(new GUIContent("New Prefix:", "Basic prefix") , _sAutoPrefix);
            _resetString = true;
        }
        else
        {
            if (_resetString)
            {
                _prefix = string.Empty;
                _resetString = false;
            }

            _prefix = EditorGUILayout.TextField(new GUIContent("New Prefix:", "Basic prefix") , _prefix);
        }
        _name = EditorGUILayout.TextField("New Name:", _name);
        _suffix = EditorGUILayout.TextField("New Suffix:", _suffix);
        addNumbering = EditorGUILayout.Toggle(new GUIContent("Add Numbering", "Useful when selecting multiple Assets"), addNumbering);
        useAutoPrefix = EditorGUILayout.Toggle(new GUIContent("Use Auto Prefix", "DISABLE basic prefix input and ENABLE custom prefix inputs"), useAutoPrefix);

        if (useAutoPrefix)
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Space(5);

            GUILayout.Label("Custom prefix inputs", EditorStyles.boldLabel);
            GUILayout.Space(5);

            _sAutoPrefabPrefix = EditorGUILayout.TextField("Custom Prefab Prefix:", _sAutoPrefabPrefix);
            _sAutoMaterialPrefix = EditorGUILayout.TextField("Custom Material Prefix:", _sAutoMaterialPrefix);
            _sAutoTexturePrefix = EditorGUILayout.TextField("Custom Texture Prefix:", _sAutoTexturePrefix);
            _sAutoSpritePrefix = EditorGUILayout.TextField("Custom Sprite Prefix:", _sAutoSpritePrefix);

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Rename Selected"))
        {
            RenamingAssets();
        }

        Repaint();
    }

    #endregion

    #region Custom Methods

    private void RenamingAssets()
    {
        ClearLists();

        //VerifyForWarnings();

        int undoGroup = Undo.GetCurrentGroup();

        AddToLists();

        VerifyForWarnings();

        RenameFromList();

        Undo.CollapseUndoOperations(undoGroup);
    }

    private string ConstructNewName(int index, string autoprefix)
    {
        if (useAutoPrefix)
        {
            _prefix = autoprefix;
        }

        List<string> parts = new List<string>();
        if (!string.IsNullOrEmpty(_prefix)) parts.Add(_prefix);
        if (!string.IsNullOrEmpty(_name)) parts.Add(_name);
        if (!string.IsNullOrEmpty(_suffix)) parts.Add(_suffix);
        if (addNumbering) parts.Add(index.ToString("D3"));

        return string.Join("_", parts);
    }

    private int GetAssetIndex(Object asset)
    {
        if (prefabsList.Contains(asset)) return prefabsList.IndexOf(asset) + 1;
        if (materialsList.Contains(asset as Material)) return materialsList.IndexOf(asset as Material) + 1;
        if (texturesList.Contains(asset as Texture2D)) return texturesList.IndexOf(asset as Texture2D) + 1;
        if (spritesList.Contains(asset as Sprite)) return spritesList.IndexOf(asset as Sprite) + 1;
        if (gameobjectsList.Contains(asset as GameObject)) return gameobjectsList.IndexOf(asset as GameObject) + 1;
        return 0;
    }

    private bool HasSpriteSubAsset(string assetPath)
    {
        Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);
        return subAssets.Any(subAsset => subAsset is Sprite);
    }

    //Add the selected assets to their respective list for the numbering
    private void AddToLists()
    {
        foreach (Object asset in RenameAssets)
        {
            string assetPath = AssetDatabase.GetAssetPath(asset);

            if (PrefabUtility.GetPrefabAssetType(asset) != PrefabAssetType.NotAPrefab)
            {
                prefabsList.Add(asset);
            }
            else if (asset is Material)
            {
                materialsList.Add(asset as Material);
            }
            else if (asset is Texture2D)
            {
                if (HasSpriteSubAsset(assetPath))
                {
                    Texture2D texture = asset as Texture2D;
                    Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);
                    foreach (Object subAsset in subAssets)
                    {
                        if (subAsset is Sprite sprite)
                        {
                            spritesList.Add(sprite);
                        }
                    }
                }
                else
                {
                    texturesList.Add(asset as Texture2D);
                }
            }
            else if (asset is GameObject)
            {
                gameobjectsList.Add(asset as GameObject);
            }
        }
    }

    //Verify the type of selected assets and rename them
    private void RenameFromList()
    {
        foreach (Object asset in prefabsList)
        {
            string assetPath = AssetDatabase.GetAssetPath(asset);
            string newName = ConstructNewName(GetAssetIndex(asset), _sAutoPrefabPrefix);
            AssetsRename(assetPath, asset, newName);
        }
        foreach (Material material in materialsList)
        {
            string assetPath = AssetDatabase.GetAssetPath(material);
            string newName = ConstructNewName(GetAssetIndex(material), _sAutoMaterialPrefix);
            AssetsRename(assetPath, material, newName);
        }
        foreach (Texture2D tex2D in texturesList)
        {
            string assetPath = AssetDatabase.GetAssetPath(tex2D);
            string newName = ConstructNewName(GetAssetIndex(tex2D), _sAutoTexturePrefix);
            AssetsRename(assetPath, tex2D, newName);
        }
        foreach (Sprite sprite in spritesList)
        {
            string assetPath = AssetDatabase.GetAssetPath(sprite);
            string newName = ConstructNewName(GetAssetIndex(sprite), _sAutoSpritePrefix);
            AssetsRename(assetPath, sprite, newName);
        }
        foreach (GameObject go in gameobjectsList)
        {
            if (!useAutoPrefix)
            {
                string assetPath = AssetDatabase.GetAssetPath(go);
                string newName = ConstructNewName(GetAssetIndex(go), "");
                HierarchyRename(go, newName);
            }
        }
    }

    #region Hierarchy - Rename and Undo Method
    //Rename the selected Hierarchy gameobject and add it to the Undo
    private void HierarchyRename(Object asset, string newName)
    {
        Undo.RecordObject(asset, "Returning to previous name");
        asset.name = newName;
    }
    #endregion

    #region Assets - Rename and Undo Method
    private void AssetsRename(string assetPath, Object asset, string newName)
    {
        string directory = System.IO.Path.GetDirectoryName(assetPath);
        string extension = System.IO.Path.GetExtension(assetPath);
        string newAssetPath = System.IO.Path.Combine(directory, newName + extension);

        string oldAssetPath = assetPath;
        string oldName = asset.name;

        Object mainAsset = AssetDatabase.LoadMainAssetAtPath(oldAssetPath);
        EditorUtility.SetDirty(mainAsset);
        Undo.RegisterCompleteObjectUndo(mainAsset, "Rename Asset");

        AssetDatabase.MoveAsset(assetPath, newAssetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Undo.undoRedoPerformed += () =>
        {
            string currentPath = AssetDatabase.GetAssetPath(asset);

            if (currentPath != newAssetPath)
            {
                AssetDatabase.MoveAsset(newAssetPath, oldAssetPath);
            }
            else
            {
                AssetDatabase.MoveAsset(oldAssetPath, newAssetPath);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        };

        Object[] rootInstances = Object.FindObjectsOfType<Object>()
            .Where(go => PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.NotAPrefab && PrefabUtility.GetCorrespondingObjectFromSource(go) == asset ||
            go is Material || go is Texture2D || go is Sprite)
            .ToArray();

        foreach (Object instance in rootInstances)
        {
            instance.name = newName;
            EditorUtility.SetDirty(instance);
        }
    }
    #endregion

    //Updates at every selection change
    private void OnSelectionChange()
    {
        RenameAssets = Selection.objects;
        //ClearLists();
        Repaint();
    }

    //Clear Lists
    private void ClearLists()
    {
        prefabsList.Clear();
        materialsList.Clear();
        texturesList.Clear();
        spritesList.Clear();
        gameobjectsList.Clear();
    }

    private void VerifyForWarnings()
    {
        if (RenameAssets == null || RenameAssets.Length == 0)
        {
            EditorUtility.DisplayDialog("Warning", "No Assets selected", "Ok");
            return;
        }

        if (gameobjectsList.Count > 0 && useAutoPrefix)
        {
            EditorUtility.DisplayDialog("Warning", "Auto Prefixe is on, make sure to turn it off before renaming hierarchy objects", "OK");
            return;
        }
    }

    #endregion
}