using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Releases folder picker plus a searchable, checkable list of the DlcRelease assets in it (subfolders included).
/// Shared by the DLC Publish and DLC Store Export windows; the folder is remembered per project.
/// </summary>
[Serializable]
public sealed class DlcReleaseListView
{
    [SerializeField] string folder = "";
    [SerializeField] string search = "";
    [SerializeField] List<string> checkedGuids = new List<string>();

    readonly List<(string guid, DlcRelease release)> releases = new List<(string, DlcRelease)>();
    Vector2 scroll;

    static string FolderPref => "Arbelos.DlcReleases." + PlayerSettings.productGUID + ".folder";

    public string Folder => folder;
    public IReadOnlyList<(string guid, DlcRelease release)> All => releases;
    public List<DlcRelease> Checked => releases.Where(r => checkedGuids.Contains(r.guid)).Select(r => r.release).ToList();

    /// <summary>Product IDs of the checked releases, trimmed and de-duplicated, in release order.</summary>
    public List<string> CheckedProductIds => Checked.SelectMany(r => r.productIds ?? new List<string>())
        .Select(id => (id ?? "").Trim()).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    public void Load(string legacyFolderPref = null)
    {
        if (string.IsNullOrEmpty(folder)) folder = EditorPrefs.GetString(FolderPref, "");
        if (string.IsNullOrEmpty(folder) && legacyFolderPref != null) folder = EditorPrefs.GetString(legacyFolderPref, "");
        Refresh();
    }

    public void Refresh()
    {
        releases.Clear();
        if (AssetDatabase.IsValidFolder(folder))
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(DlcRelease), new[] { folder }))
            {
                var r = AssetDatabase.LoadAssetAtPath<DlcRelease>(AssetDatabase.GUIDToAssetPath(guid));
                if (r) releases.Add((guid, r));
            }
        releases.Sort((a, b) => string.Compare(a.release.releaseId, b.release.releaseId, StringComparison.OrdinalIgnoreCase));
        checkedGuids.RemoveAll(g => releases.All(r => r.guid != g));
    }

    /// <param name="greyOtherPlatforms">Grey out releases whose target isn't the active build target.</param>
    /// <returns>An error message for the caller's report, or null.</returns>
    public string Draw(bool greyOtherPlatforms)
    {
        string message = null;
        EditorGUILayout.LabelField("Releases", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Releases folder", string.IsNullOrEmpty(folder) ? "(none)" : folder);
            if (GUILayout.Button("Browse", GUILayout.Width(65))) message = Browse();
            if (GUILayout.Button("Refresh", GUILayout.Width(65))) Refresh();
        }
        if (!AssetDatabase.IsValidFolder(folder))
        {
            EditorGUILayout.HelpBox("Choose the project folder that contains your DlcRelease assets (subfolders are included).", MessageType.Info);
            return message;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            search = EditorGUILayout.TextField("Search", search);
            if (GUILayout.Button("All", GUILayout.Width(40))) foreach (var r in Visible()) if (!checkedGuids.Contains(r.guid)) checkedGuids.Add(r.guid);
            if (GUILayout.Button("None", GUILayout.Width(45))) foreach (var r in Visible()) checkedGuids.Remove(r.guid);
        }

        var visible = Visible().ToList();
        var active = EditorUserBuildSettings.activeBuildTarget;
        EditorGUILayout.LabelField($"{releases.Count} releases, {visible.Count} shown, {checkedGuids.Count} checked", EditorStyles.miniLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll, EditorStyles.helpBox, GUILayout.MinHeight(180), GUILayout.MaxHeight(320));
        foreach (var (guid, release) in visible)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool on = checkedGuids.Contains(guid);
                bool now = EditorGUILayout.Toggle(on, GUILayout.Width(18));
                if (now != on) { if (now) checkedGuids.Add(guid); else checkedGuids.Remove(guid); }
                bool other = greyOtherPlatforms && release.target != active;
                using (new EditorGUI.DisabledScope(other))
                {
                    EditorGUILayout.LabelField(new GUIContent(release.releaseId, AssetDatabase.GUIDToAssetPath(guid)), EditorStyles.boldLabel, GUILayout.MinWidth(140));
                    EditorGUILayout.LabelField(release.target + (other ? " (other platform)" : ""), GUILayout.Width(150));
                    EditorGUILayout.LabelField($"{release.productIds?.Count ?? 0} products, {release.content?.Count ?? 0} entries", GUILayout.Width(150));
                }
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    Selection.activeObject = release;
                    EditorGUIUtility.PingObject(release);
                }
            }
        }
        EditorGUILayout.EndScrollView();
        return message;
    }

    IEnumerable<(string guid, DlcRelease release)> Visible() => releases.Where(r => string.IsNullOrWhiteSpace(search)
        || r.release.releaseId.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
        || r.release.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
        || (r.release.productIds ?? new List<string>()).Any(id => id != null && id.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));

    string Browse()
    {
        string start = AssetDatabase.IsValidFolder(folder) ? Path.GetFullPath(folder) : Application.dataPath;
        string picked = EditorUtility.OpenFolderPanel("Releases folder", start, "");
        if (string.IsNullOrEmpty(picked)) return null;
        string project = Path.GetFullPath(".").Replace('\\', '/').TrimEnd('/') + "/";
        string full = Path.GetFullPath(picked).Replace('\\', '/');
        string relative = full.StartsWith(project, StringComparison.OrdinalIgnoreCase) ? full.Substring(project.Length) : null;
        if (relative == null || !AssetDatabase.IsValidFolder(relative)) return "Choose a folder inside this project's Assets.";
        folder = relative;
        EditorPrefs.SetString(FolderPref, folder);
        Refresh();
        return null;
    }
}
