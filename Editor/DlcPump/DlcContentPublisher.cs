using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;

/// <summary>
/// Runs Tools~/DlcPublisher/publish_dlc.py. The script signs in as the current Azure CLI user;
/// Unity passes only the account URL, container and paths.
/// </summary>
public static class DlcContentPublisher
{
    public static string StagingRoot(DlcPublishingSettings settings) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(settings.stagingDirectory) ? "DlcContentStaging" : settings.stagingDirectory);

    public static string CurrentPlatform => EditorUserBuildSettings.activeBuildTarget.ToString();
    public static string CurrentAppVersion => PlayerSettings.bundleVersion;

    /// <summary>Export folders for the active platform and app version, newest first.</summary>
    public static List<string> Exports(DlcPublishingSettings settings)
    {
        string root = Path.Combine(StagingRoot(settings), CurrentPlatform, CurrentAppVersion);
        return Directory.Exists(root)
            ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "export.json")))
                .OrderByDescending(d => d, StringComparer.Ordinal).ToList()
            : new List<string>();
    }

    public static string Destination(DlcPublishingSettings settings) =>
        $"{settings.AccountUrl}/{settings.container}/{CurrentPlatform}/{CurrentAppVersion}/";

    /// <param name="allowNewCatalog">Publish even if the app-version folder already has a catalog with a different
    /// name. Only for an app version that hasn't shipped: installed players keep loading the old catalog.</param>
    public static Task<(int code, string output)> Publish(DlcPublishingSettings settings, string exportFolder, IEnumerable<string> products,
        bool dryRun, bool allowNewCatalog = false)
    {
        var args = new List<string> { "publish", "--staging", exportFolder };
        var only = products?.ToList();
        if (only != null && only.Count > 0) { args.Add("--product"); args.AddRange(only); }
        if (allowNewCatalog) args.Add("--allow-new-catalog");
        return Run(settings, args, dryRun);
    }

    /// <summary>Signs in to the container as the Azure CLI user and reads its properties. Writes nothing.</summary>
    public static Task<(int code, string output)> Check(DlcPublishingSettings settings) =>
        Run(settings, new List<string> { "check" }, false);

    public static Task<(int code, string output)> Status(DlcPublishingSettings settings) =>
        Run(settings, new List<string> { "status", "--platform", CurrentPlatform, "--app-version", CurrentAppVersion }, false);

    public static Task<(int code, string output)> Rollback(DlcPublishingSettings settings, string productId, string version, bool drop, bool dryRun) =>
        Run(settings, new List<string> { "rollback", "--platform", CurrentPlatform, "--app-version", CurrentAppVersion,
            "--product", productId, drop ? "--drop" : "--to", version }, dryRun);

    static async Task<(int code, string output)> Run(DlcPublishingSettings settings, List<string> args, bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(settings.accountName)) return (1, $"accountName is empty in {settings.name}.");
        var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DlcContentPublisher).Assembly);
        string script = Path.Combine(package.resolvedPath, "Tools~", "DlcPublisher", "publish_dlc.py");
        args.InsertRange(0, new[] { script });
        args.AddRange(new[] { "--account-url", settings.AccountUrl, "--container", settings.container,
            "--log-dir", Path.GetFullPath(Path.Combine("Logs", "dlc-publish")) });
        if (dryRun) args.Add("--dry-run");

        var line = new StringBuilder();
        foreach (string a in args) line.Append('"').Append(a.Replace("\"", "\\\"")).Append("\" ");
        var start = new ProcessStartInfo(settings.pythonExecutable, line.ToString()) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Directory.GetCurrentDirectory() };
        start.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        // AzureCliCredential runs az itself; make sure it's found even if it was installed after Unity started.
        string azDir = DlcAzureCli.Directory();
        if (azDir != null) start.EnvironmentVariables["PATH"] = azDir + Path.PathSeparator + start.EnvironmentVariables["PATH"];
        try
        {
            using (var process = Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                bool exited = await Task.Run(() => process.WaitForExit(30 * 60 * 1000));
                if (!exited) { process.Kill(); return (1, "Timed out after 30 minutes. Check Logs/dlc-publish and products.json before retrying."); }
                string error = await stderr;
                return (process.ExitCode, await stdout + (string.IsNullOrWhiteSpace(error) ? "" : "\n" + error));
            }
        }
        catch (Exception e)
        {
            return (1, $"Couldn't run '{settings.pythonExecutable}': {e.Message}. Set pythonExecutable in {settings.name}.");
        }
    }
}
