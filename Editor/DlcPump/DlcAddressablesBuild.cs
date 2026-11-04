using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEngine;

/// <summary>
/// Full and content-update Addressables builds for the DLC Publish window. A build covers every Addressables group
/// in the project for the editor's active platform and app version, not one release; the checked releases are only
/// recorded in the receipt and checked against the active profile.
///
/// Receipts go to Logs/dlc-builds/&lt;Platform&gt;/&lt;appVersion&gt;/: &lt;stamp&gt;.manifest.json (snapshot of the checked
/// releases) and &lt;stamp&gt;.build.json (result), plus &lt;stamp&gt;.baseline.bin (the content-state file a content
/// update was built from).
/// </summary>
public static class DlcAddressablesBuild
{
    public static string ReceiptFolder =>
        Path.GetFullPath(Path.Combine("Logs", "dlc-builds", DlcContentPublisher.CurrentPlatform, DlcContentPublisher.CurrentAppVersion));

    public static bool Building { get; private set; }

    /// <summary>The checked releases' Addressables profile, or an error when they don't share one or it isn't active.</summary>
    public static string CheckProfile(IList<DlcRelease> releases, out string profile)
    {
        profile = null;
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (!settings) return "No Addressables settings found in this project.";
        string active = settings.profileSettings.GetProfileName(settings.activeProfileId);
        var profiles = (releases ?? new List<DlcRelease>()).Where(r => r).Select(r => (r.addressablesProfile ?? "").Trim()).Distinct().ToList();
        if (profiles.Count > 1) return "The checked releases use different Addressables profiles: " + string.Join(", ", profiles) + ".";
        profile = profiles.Count == 1 && profiles[0].Length > 0 ? profiles[0] : active;
        if (profile != active) return $"The checked releases use Addressables profile '{profile}', but '{active}' is active. Select it in the Addressables Groups window.";
        return null;
    }

    /// <summary>One manifest for the build: the platform and profile, plus each checked release's snapshot.</summary>
    public static JObject Snapshot(IList<DlcRelease> releases, string profile) => new JObject {
        ["schemaVersion"] = 1, ["target"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
        ["appVersion"] = PlayerSettings.bundleVersion, ["profile"] = profile, ["unityVersion"] = Application.unityVersion,
        ["releases"] = new JArray(releases.Where(r => r).OrderBy(r => r.releaseId, StringComparer.Ordinal).Select(r => (JToken)DlcReleaseService.Snapshot(r))) };

    /// <summary>
    /// Checks the editor state and queues the build for the next editor tick. Returns an error message, or null when
    /// queued; <paramref name="finished"/> then gets the report once the build ends.
    /// </summary>
    public static string Queue(IList<DlcRelease> releases, string baselinePath, Action<bool, string> finished)
    {
        if (Building) return "A build is already running.";
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return "Stop Play mode and wait for scripts to compile first.";
        if (!DlcReleaseService.IsStorePlatform(EditorUserBuildSettings.activeBuildTarget))
            return $"The active build target is {EditorUserBuildSettings.activeBuildTarget}. Switch to Android or iOS first; every release builds for both.";
        releases = (releases ?? new List<DlcRelease>()).Where(r => r).ToList();
        if (releases.Count == 0) return "Check the releases this build is for.";
        string profileError = CheckProfile(releases, out string profile);
        if (profileError != null) return profileError;

        bool update = baselinePath != null;
        if (update && (string.IsNullOrWhiteSpace(baselinePath) || !File.Exists(baselinePath)))
            return "Choose the content-state baseline (the .bin from the full build players have). A content update never falls back to a full build.";

        AssetDatabase.SaveAssets();
        var snapshot = Snapshot(releases, profile);
        Directory.CreateDirectory(ReceiptFolder);
        string receipt = Path.Combine(ReceiptFolder, (update ? "update-" : "full-") + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        DlcReleaseService.WriteNew(receipt + ".manifest.json", snapshot.ToString());
        string baseline = update ? Path.GetFullPath(baselinePath) : null;
        string baselineHash = update ? DlcReleaseService.Hash(File.ReadAllBytes(baseline)) : null;
        Building = true;
        EditorApplication.delayCall += () => Build(releases, receipt, snapshot, baseline, baselineHash, finished);
        return null;
    }

    static void Build(IList<DlcRelease> releases, string receipt, JObject snapshot, string baseline, string baselineHash, Action<bool, string> finished)
    {
        var result = new JObject {
            ["releaseIds"] = new JArray(releases.Where(r => r).Select(r => r.releaseId)),
            ["manifestSha256"] = DlcReleaseService.Hash(Encoding.UTF8.GetBytes(snapshot.ToString())),
            ["startedUtc"] = DateTime.UtcNow.ToString("O"), ["published"] = false,
            ["buildKind"] = baseline == null ? "full" : "content-update", ["baselineSha256"] = baselineHash };
        string report;
        bool ok = false;
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("Build cancelled: Editor state changed.");
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (!settings || EditorUserBuildSettings.activeBuildTarget.ToString() != (string)snapshot["target"] ||
                PlayerSettings.bundleVersion != (string)snapshot["appVersion"] ||
                settings.profileSettings.GetProfileName(settings.activeProfileId) != (string)snapshot["profile"])
                throw new InvalidOperationException("Build cancelled: target, app version or profile changed.");
            if (releases.Any(r => !r) || !JToken.DeepEquals(snapshot["releases"], Snapshot(releases, (string)snapshot["profile"])["releases"]))
                throw new InvalidOperationException("Build cancelled: a release or its content changed after the snapshot.");

            if (baseline != null)
            {
                if (!File.Exists(baseline) || DlcReleaseService.Hash(File.ReadAllBytes(baseline)) != baselineHash)
                    throw new InvalidOperationException("Content-state baseline changed since queueing.");
                // Build from a copy kept with the receipt, so an output cleanup can't erase the shipped build's state.
                string archivedBaseline = receipt + ".baseline.bin";
                using (var input = File.OpenRead(baseline))
                using (var output = new FileStream(archivedBaseline, FileMode.CreateNew)) input.CopyTo(output);
                var updated = ContentUpdateScript.BuildContentUpdate(settings, archivedBaseline);
                if (updated == null || !string.IsNullOrEmpty(updated.Error))
                    throw new InvalidOperationException(updated?.Error ?? "No content-update result.");
                result["outputPath"] = updated.OutputPath;
                result["buildProvider"] = "Unity content update with explicit archived baseline";
            }
            else
            {
                // Reuse the shared package's checked build primitive.
                var provider = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Arbelos.BuildUtility.Editor.CustomAddressableTools"))
                    .FirstOrDefault(t => t != null);
                var build = provider?.GetMethod("BuildPlayerContentWithResult", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                if (build == null) throw new InvalidOperationException("The shared Arbelos build package needs BuildPlayerContentWithResult support. Update com.arbelos.buildutility.");
                try
                {
                    var built = (AddressablesPlayerBuildResult)build.Invoke(null, null);
                    if (built != null && !string.IsNullOrEmpty(built.Error)) throw new InvalidOperationException(built.Error);
                    result["outputPath"] = built?.OutputPath;
                }
                catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
                result["buildProvider"] = "com.arbelos.buildutility / BuildPlayerContentWithResult";
            }
            result["success"] = ok = true;
            report = $"{(baseline == null ? "Full build" : "Content update")} for {snapshot["target"]} {snapshot["appVersion"]} succeeded. " +
                     "Nothing is uploaded yet: export, then publish.\nReceipt: " + receipt + ".build.json";
        }
        catch (Exception error) { result["success"] = false; result["error"] = error.ToString(); report = "BUILD FAILED: " + error.Message; }
        finally
        {
            result["finishedUtc"] = DateTime.UtcNow.ToString("O");
            Building = false;
        }
        try { DlcReleaseService.WriteNew(receipt + ".build.json", result.ToString()); }
        catch (Exception error) { Debug.LogException(error); report += "\nReceipt could not be saved: " + error.Message; }
        if (ok) Debug.Log("[DLC Build] " + report); else Debug.LogError("[DLC Build] " + report);
        finished?.Invoke(ok, report);
    }
}
