using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public class Assets_Renamer : EditorWindow
{
    #region Variables

    //Rename Variables
    private Object[] RenameAssets = new Object[0];
    [SerializeField] private string _name = "";

    //Lists
    private readonly List<Object> prefabsList = new List<Object>();
    private readonly List<Material> materialsList = new List<Material>();
    private readonly List<Texture2D> texturesList = new List<Texture2D>();
    private readonly List<Sprite> spritesList = new List<Sprite>();
    private readonly List<Object> otherAssetsList = new List<Object>();
    private readonly List<GameObject> gameobjectsList = new List<GameObject>();

    //Preview
    private List<PlannedRename> preview = new List<PlannedRename>();
    private bool previewDirty = true;
    private Vector2 previewScroll;
    private static GUIStyle errorStyle;

    //Asset renames aren't covered by Unity's Undo, so the last batch is kept here to be reverted
    [SerializeField] private List<RenameRecord> lastRename = new List<RenameRecord>();

    private SpriteDataProviderFactories spriteDataFactories;

    #endregion

    #region Data Types

    //A rename that was applied, with enough data to find the asset again and restore its old name
    [Serializable]
    private class RenameRecord
    {
        public string guid;
        public string spriteID; //Empty when the asset file itself was renamed
        public string oldName;
        public string newName;
    }

    //A rename waiting to be applied
    private class PlannedRename
    {
        public Object target; //For the preview icon
        public GameObject gameObject; //Set for hierarchy objects
        public string assetPath;
        public string spriteID; //Set for sprites inside a sprite sheet
        public string oldName;
        public string newName;
        public string problem; //Why this rename can't be applied

        public bool IsFile => gameObject == null && string.IsNullOrEmpty(spriteID);
        public bool IsSheetSprite => !string.IsNullOrEmpty(spriteID);
        public bool Unchanged => newName == oldName;
    }

    #endregion

    [MenuItem("Tools/Asset Renamer")]
    public static void LaunchRenamer()
    {
        EditorWindow window = GetWindow(typeof(Assets_Renamer));
        window.titleContent = new GUIContent("Rename Selected Assets");
        window.Show();
    }

    #region Built-in Methods

    private void OnEnable()
    {
        RenameAssets = Selection.objects;
        previewDirty = true;
    }

    private void OnGUI()
    {
        if (previewDirty) RefreshPreview();

        EditorGUI.BeginChangeCheck();

        _name = EditorGUILayout.TextField(new GUIContent("New Name:",
            "When several assets of the same kind are selected, _001, _002... is added after the name"), _name);

        if (EditorGUI.EndChangeCheck()) previewDirty = true;

        EditorGUILayout.Space();
        DrawPreview();
        EditorGUILayout.Space();

        int changeCount = preview.Count(p => !p.Unchanged);
        bool hasProblems = preview.Any(p => p.problem != null);
        using (new EditorGUI.DisabledScope(changeCount == 0 || hasProblems))
        {
            if (GUILayout.Button(changeCount > 0 ? $"Rename {changeCount} Selected" : "Rename Selected"))
            {
                RenamingAssets();
                GUIUtility.ExitGUI();
            }
        }

        using (new EditorGUI.DisabledScope(lastRename.Count == 0))
        {
            if (GUILayout.Button(new GUIContent("Revert Last Asset Rename", "Hierarchy renames can be undone with Ctrl+Z, asset renames only with this button")))
            {
                RevertLastRename();
                GUIUtility.ExitGUI();
            }
        }
    }

    //Updates at every selection change
    private void OnSelectionChange()
    {
        RenameAssets = Selection.objects;
        previewDirty = true;
        Repaint();
    }

    //Names can change outside the tool (Ctrl+Z, manual renames), so keep the preview current
    private void OnHierarchyChange()
    {
        previewDirty = true;
        Repaint();
    }

    private void OnProjectChange()
    {
        previewDirty = true;
        Repaint();
    }

    #endregion

    #region Custom Methods

    private void RenamingAssets()
    {
        //Make sure the plan matches the current names before applying it
        RefreshPreview();

        List<string> problems = preview.Where(p => p.problem != null).Select(p => $"{p.oldName}: {p.problem}").ToList();
        if (problems.Count > 0)
        {
            ShowProblems("Nothing was renamed:", problems);
            return;
        }

        RenameFromPlan(preview.Where(p => !p.Unchanged).ToList());
        previewDirty = true;
    }

    //Numbers are only added when more than one asset of the same kind gets the name
    private string ConstructNewName(string currentName, int index, int count)
    {
        if (string.IsNullOrWhiteSpace(_name)) return currentName;

        string newName = _name.Trim();
        return count > 1 ? newName + "_" + index.ToString("D3") : newName;
    }

    private static bool IsPrefabAsset(Object asset)
    {
        PrefabAssetType type = PrefabUtility.GetPrefabAssetType(asset);
        return type == PrefabAssetType.Regular || type == PrefabAssetType.Variant;
    }

    //Add the selected assets to their respective list for the numbering
    private void AddToLists()
    {
        HashSet<Object> added = new HashSet<Object>();

        foreach (Object obj in RenameAssets)
        {
            if (obj == null) continue;

            //Hierarchy objects, prefab instances included
            if (!EditorUtility.IsPersistent(obj))
            {
                if (obj is GameObject go && added.Add(go)) gameobjectsList.Add(go);
                continue;
            }

            string assetPath = AssetDatabase.GetAssetPath(obj);
            if (AssetDatabase.IsValidFolder(assetPath) || obj is MonoScript) continue;

            if (obj is Sprite sprite)
            {
                if (added.Add(sprite)) spritesList.Add(sprite);
                continue;
            }

            //Sub-assets other than sprites (meshes inside a model, etc.) share their file with the main asset
            if (!AssetDatabase.IsMainAsset(obj)) continue;

            if (obj is Texture2D texture)
            {
                Sprite[] sprites = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath).OfType<Sprite>().ToArray();
                if (sprites.Length == 0)
                {
                    if (added.Add(texture)) texturesList.Add(texture);
                }
                else
                {
                    foreach (Sprite subSprite in sprites)
                    {
                        if (added.Add(subSprite)) spritesList.Add(subSprite);
                    }
                }
            }
            else if (!added.Add(obj))
            {
                continue;
            }
            else if (obj is GameObject && IsPrefabAsset(obj))
            {
                prefabsList.Add(obj);
            }
            else if (obj is Material material)
            {
                materialsList.Add(material);
            }
            else
            {
                otherAssetsList.Add(obj);
            }
        }

        //Sort so the numbering is predictable instead of following the selection order
        prefabsList.Sort(CompareAssetPaths);
        materialsList.Sort(CompareAssetPaths);
        texturesList.Sort(CompareAssetPaths);
        otherAssetsList.Sort(CompareAssetPaths);
        spritesList.Sort((a, b) =>
        {
            int pathCompare = CompareAssetPaths(a, b);
            return pathCompare != 0 ? pathCompare : EditorUtility.NaturalCompare(a.name, b.name);
        });
        gameobjectsList.Sort(CompareHierarchyOrder);
    }

    private static int CompareAssetPaths(Object a, Object b)
    {
        return EditorUtility.NaturalCompare(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b));
    }

    private static int CompareHierarchyOrder(GameObject a, GameObject b)
    {
        int sceneCompare = GetSceneIndex(a.scene).CompareTo(GetSceneIndex(b.scene));
        if (sceneCompare != 0) return sceneCompare;

        List<int> pathA = GetSiblingPath(a.transform);
        List<int> pathB = GetSiblingPath(b.transform);
        for (int i = 0; i < Mathf.Min(pathA.Count, pathB.Count); i++)
        {
            if (pathA[i] != pathB[i]) return pathA[i].CompareTo(pathB[i]);
        }
        return pathA.Count.CompareTo(pathB.Count);
    }

    private static int GetSceneIndex(Scene scene)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            if (SceneManager.GetSceneAt(i) == scene) return i;
        }
        return int.MaxValue;
    }

    private static List<int> GetSiblingPath(Transform transform)
    {
        List<int> path = new List<int>();
        for (; transform != null; transform = transform.parent)
        {
            path.Insert(0, transform.GetSiblingIndex());
        }
        return path;
    }

    #region Preview
    private void RefreshPreview()
    {
        previewDirty = false;
        ClearLists();
        AddToLists();
        preview = BuildRenamePlan();
    }

    private void DrawPreview()
    {
        GUILayout.Label($"Preview ({RenameAssets.Length} selected)", EditorStyles.boldLabel);

        if (preview.Count == 0)
        {
            EditorGUILayout.HelpBox(RenameAssets.Length == 0
                ? "Select assets in the Project window or objects in the Hierarchy."
                : "None of the selected objects can be renamed (folders and scripts are skipped).", MessageType.Info);
            return;
        }

        if (errorStyle == null)
        {
            errorStyle = new GUIStyle(EditorStyles.label);
            errorStyle.normal.textColor = new Color(0.9f, 0.3f, 0.3f);
        }

        float nameWidth = (position.width - 60) / 2;
        previewScroll = EditorGUILayout.BeginScrollView(previewScroll);
        foreach (PlannedRename rename in preview)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(AssetPreview.GetMiniThumbnail(rename.target), GUILayout.Width(18), GUILayout.Height(18));
            GUILayout.Label(rename.oldName, GUILayout.Width(nameWidth));
            GUILayout.Label("→", GUILayout.Width(16));
            if (rename.problem != null)
            {
                GUILayout.Label(rename.newName, errorStyle);
            }
            else if (rename.Unchanged)
            {
                GUILayout.Label("(unchanged)", EditorStyles.centeredGreyMiniLabel, GUILayout.ExpandWidth(false));
            }
            else
            {
                GUILayout.Label(rename.newName);
            }
            EditorGUILayout.EndHorizontal();

            if (rename.problem != null)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(22);
                GUILayout.Label(rename.problem, errorStyle);
                EditorGUILayout.EndHorizontal();
            }
        }
        EditorGUILayout.EndScrollView();
    }
    #endregion

    #region Rename Plan
    //Work out every new name first, so nothing is renamed if any of them is invalid
    private List<PlannedRename> BuildRenamePlan()
    {
        List<PlannedRename> plan = new List<PlannedRename>();

        for (int i = 0; i < gameobjectsList.Count; i++)
        {
            GameObject go = gameobjectsList[i];
            plan.Add(new PlannedRename { target = go, gameObject = go, oldName = go.name, newName = ConstructNewName(go.name, i + 1, gameobjectsList.Count) });
        }

        AddFilesToPlan(plan, prefabsList);
        AddFilesToPlan(plan, materialsList);
        AddFilesToPlan(plan, texturesList);
        AddFilesToPlan(plan, otherAssetsList);
        AddSpritesToPlan(plan);

        CheckForProblems(plan);
        return plan;
    }

    private void AddFilesToPlan<T>(List<PlannedRename> plan, List<T> assets) where T : Object
    {
        for (int i = 0; i < assets.Count; i++)
        {
            string assetPath = AssetDatabase.GetAssetPath(assets[i]);
            string oldName = Path.GetFileNameWithoutExtension(assetPath);
            plan.Add(new PlannedRename
            {
                target = assets[i],
                assetPath = assetPath,
                oldName = oldName,
                newName = ConstructNewName(oldName, i + 1, assets.Count)
            });
        }
    }

    private void AddSpritesToPlan(List<PlannedRename> plan)
    {
        Dictionary<string, SpriteRect[]> sheets = new Dictionary<string, SpriteRect[]>();

        for (int i = 0; i < spritesList.Count; i++)
        {
            Sprite sprite = spritesList[i];
            string assetPath = AssetDatabase.GetAssetPath(sprite);

            //A single sprite is named after its texture file, so the file is renamed
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null || importer.spriteImportMode != SpriteImportMode.Multiple)
            {
                string fileName = Path.GetFileNameWithoutExtension(assetPath);
                plan.Add(new PlannedRename { target = sprite, assetPath = assetPath, oldName = fileName, newName = ConstructNewName(fileName, i + 1, spritesList.Count) });
                continue;
            }

            if (!sheets.TryGetValue(assetPath, out SpriteRect[] rects))
            {
                rects = GetSpriteDataProvider(assetPath)?.GetSpriteRects() ?? new SpriteRect[0];
                sheets[assetPath] = rects;
            }

            PlannedRename rename = new PlannedRename { target = sprite, assetPath = assetPath, oldName = sprite.name, newName = ConstructNewName(sprite.name, i + 1, spritesList.Count) };
            SpriteRect spriteRect = rects.FirstOrDefault(r => r.name == sprite.name && r.rect == sprite.rect);
            if (spriteRect == null)
            {
                rename.problem = $"Couldn't find it in the sprite sheet {Path.GetFileName(assetPath)}";
            }
            else
            {
                rename.spriteID = spriteRect.spriteID.ToString();
            }
            plan.Add(rename);
        }

        //Sprite names must stay unique inside each sheet
        foreach (KeyValuePair<string, SpriteRect[]> sheet in sheets)
        {
            List<PlannedRename> sheetRenames = plan.Where(p => p.IsSheetSprite && p.assetPath == sheet.Key).ToList();
            Dictionary<string, string> plannedNames = sheetRenames.ToDictionary(p => p.spriteID, p => p.newName);

            HashSet<string> duplicates = new HashSet<string>(sheet.Value
                .Select(r => plannedNames.TryGetValue(r.spriteID.ToString(), out string n) ? n : r.name)
                .GroupBy(n => n)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key));

            foreach (PlannedRename rename in sheetRenames.Where(p => !p.Unchanged && p.problem == null && duplicates.Contains(p.newName)))
            {
                rename.problem = "Another sprite in this sheet already has this name";
            }
        }
    }

    private static string GetNewAssetPath(PlannedRename rename)
    {
        return Path.Combine(Path.GetDirectoryName(rename.assetPath), rename.newName + Path.GetExtension(rename.assetPath)).Replace('\\', '/');
    }

    private void CheckForProblems(List<PlannedRename> plan)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();

        foreach (PlannedRename rename in plan.Where(p => p.problem == null && !p.Unchanged))
        {
            if (string.IsNullOrWhiteSpace(rename.newName))
            {
                rename.problem = "The new name would be empty";
            }
            else if (rename.IsFile && rename.newName.IndexOfAny(invalidChars) >= 0)
            {
                rename.problem = "Has characters that aren't allowed in file names";
            }
        }

        IEnumerable<PlannedRename> fileRenames = plan.Where(p => p.IsFile && p.problem == null && !p.Unchanged);
        foreach (IGrouping<string, PlannedRename> target in fileRenames.GroupBy(GetNewAssetPath, StringComparer.OrdinalIgnoreCase))
        {
            if (target.Count() > 1)
            {
                foreach (PlannedRename rename in target)
                {
                    rename.problem = "Another selected asset gets the same name";
                }
                continue;
            }

            PlannedRename single = target.First();
            bool caseOnlyChange = string.Equals(target.Key, single.assetPath, StringComparison.OrdinalIgnoreCase);
            if (!caseOnlyChange && (File.Exists(target.Key) || Directory.Exists(target.Key)))
            {
                single.problem = "A file with this name already exists";
            }
        }
    }
    #endregion

    private void RenameFromPlan(List<PlannedRename> plan)
    {
        RenameHierarchyObjects(plan.Where(p => p.gameObject != null).ToList());

        List<RenameRecord> records = new List<RenameRecord>();
        List<string> failures = new List<string>();

        //Batch the file renames so the AssetDatabase only processes them once
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (PlannedRename rename in plan.Where(p => p.IsFile))
            {
                string guid = AssetDatabase.AssetPathToGUID(rename.assetPath);
                string error = AssetDatabase.RenameAsset(rename.assetPath, rename.newName);
                if (string.IsNullOrEmpty(error))
                {
                    records.Add(new RenameRecord { guid = guid, oldName = rename.oldName, newName = rename.newName });
                }
                else
                {
                    failures.Add($"{rename.assetPath}: {error}");
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }

        foreach (IGrouping<string, PlannedRename> sheet in plan.Where(p => p.IsSheetSprite).GroupBy(p => p.assetPath))
        {
            if (RenameSprites(sheet.Key, sheet.Select(p => (p.spriteID, p.oldName, p.newName))))
            {
                string guid = AssetDatabase.AssetPathToGUID(sheet.Key);
                records.AddRange(sheet.Select(p => new RenameRecord { guid = guid, spriteID = p.spriteID, oldName = p.oldName, newName = p.newName }));
            }
            else
            {
                failures.Add($"{sheet.Key}: couldn't rename its sprites");
            }
        }

        AssetDatabase.SaveAssets();

        if (records.Count > 0) lastRename = records;
        if (failures.Count > 0) ShowProblems("Some assets couldn't be renamed:", failures);
    }

    #region Hierarchy - Rename and Undo Method
    //Rename the selected Hierarchy gameobjects as a single Undo step
    private void RenameHierarchyObjects(List<PlannedRename> renames)
    {
        if (renames.Count == 0) return;

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Rename Objects");
        int undoGroup = Undo.GetCurrentGroup();

        foreach (PlannedRename rename in renames)
        {
            Undo.RecordObject(rename.gameObject, "Rename Objects");
            rename.gameObject.name = rename.newName;
        }

        Undo.CollapseUndoOperations(undoGroup);
    }
    #endregion

    #region Sprites - Rename Method
    private ISpriteEditorDataProvider GetSpriteDataProvider(string texturePath)
    {
        if (spriteDataFactories == null)
        {
            spriteDataFactories = new SpriteDataProviderFactories();
            spriteDataFactories.Init();
        }

        ISpriteEditorDataProvider dataProvider = spriteDataFactories.GetSpriteEditorDataProviderFromObject(AssetImporter.GetAtPath(texturePath));
        dataProvider?.InitSpriteEditorDataProvider();
        return dataProvider;
    }

    //Rename sprites inside a sprite sheet, keeping their IDs so references to them aren't lost.
    //A sprite is only renamed while it still has the "from" name.
    private bool RenameSprites(string texturePath, IEnumerable<(string spriteID, string from, string to)> renames)
    {
        ISpriteEditorDataProvider dataProvider = GetSpriteDataProvider(texturePath);
        if (dataProvider == null) return false;

        Dictionary<string, (string from, string to)> renamesByID = renames.ToDictionary(r => r.spriteID, r => (r.from, r.to));
        SpriteRect[] spriteRects = dataProvider.GetSpriteRects();
        foreach (SpriteRect spriteRect in spriteRects)
        {
            if (renamesByID.TryGetValue(spriteRect.spriteID.ToString(), out (string from, string to) rename) && spriteRect.name == rename.from)
            {
                spriteRect.name = rename.to;
            }
        }
        dataProvider.SetSpriteRects(spriteRects);

        //Keep the name to file ID table in sync with the new names
        ISpriteNameFileIdDataProvider nameFileIdProvider = dataProvider.GetDataProvider<ISpriteNameFileIdDataProvider>();
        if (nameFileIdProvider != null)
        {
            List<SpriteNameFileIdPair> pairs = nameFileIdProvider.GetNameFileIdPairs()
                .Where(pair => spriteRects.All(r => r.spriteID != pair.GetFileGUID()))
                .ToList();
            pairs.AddRange(spriteRects.Select(r => new SpriteNameFileIdPair(r.name, r.spriteID)));
            nameFileIdProvider.SetNameFileIdPairs(pairs);
        }

        dataProvider.Apply();
        AssetImporter.GetAtPath(texturePath).SaveAndReimport();
        return true;
    }
    #endregion

    private void RevertLastRename()
    {
        List<string> failures = new List<string>();

        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (RenameRecord record in lastRename.Where(r => string.IsNullOrEmpty(r.spriteID)))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(record.guid);
                //Skip assets that were deleted or renamed again since
                if (string.IsNullOrEmpty(assetPath) || Path.GetFileNameWithoutExtension(assetPath) != record.newName) continue;

                string error = AssetDatabase.RenameAsset(assetPath, record.oldName);
                if (!string.IsNullOrEmpty(error)) failures.Add($"{assetPath}: {error}");
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }

        foreach (IGrouping<string, RenameRecord> sheet in lastRename.Where(r => !string.IsNullOrEmpty(r.spriteID)).GroupBy(r => r.guid))
        {
            string texturePath = AssetDatabase.GUIDToAssetPath(sheet.Key);
            if (string.IsNullOrEmpty(texturePath)) continue;

            if (!RenameSprites(texturePath, sheet.Select(r => (r.spriteID, r.newName, r.oldName))))
            {
                failures.Add($"{texturePath}: couldn't restore its sprite names");
            }
        }

        AssetDatabase.SaveAssets();
        lastRename.Clear();
        previewDirty = true;

        if (failures.Count > 0) ShowProblems("Some assets couldn't be reverted:", failures);
    }

    //Clear Lists
    private void ClearLists()
    {
        prefabsList.Clear();
        materialsList.Clear();
        texturesList.Clear();
        spritesList.Clear();
        otherAssetsList.Clear();
        gameobjectsList.Clear();
    }

    private static void ShowProblems(string title, List<string> problems)
    {
        const int maxShown = 10;
        string message = title + "\n\n" + string.Join("\n", problems.Take(maxShown));
        if (problems.Count > maxShown) message += $"\n...and {problems.Count - maxShown} more (see the Console)";

        foreach (string problem in problems) Debug.LogWarning("Asset Renamer: " + problem);
        EditorUtility.DisplayDialog("Warning", message, "OK");
    }

    #endregion
}
