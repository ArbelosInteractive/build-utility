using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

/// <summary>One "@@progress" update from publish_dlc.py --progress.</summary>
public sealed class DlcPublishProgress
{
    public int Step, Steps, FilesDone, FilesTotal;
    public long Done, Total;
    public string Phase = "", Unit = "", Item = "";

    public float Fraction => Total > 0 ? Math.Min(1f, (float)Done / Total) : 0f;

    public string Describe()
    {
        string amount = Unit == "bytes" ? $"{Mb(Done)} / {Mb(Total)} MB" : $"{Done} / {Total}";
        string files = Unit == "bytes" && FilesTotal > 0 ? $"  ·  file {Math.Min(FilesDone + 1, FilesTotal)} of {FilesTotal}" : "";
        return $"Step {Step}/{Steps}  {Phase}:  {amount}{files}";
    }

    static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0");

    public static DlcPublishProgress Parse(string json)
    {
        try
        {
            var o = JObject.Parse(json);
            return new DlcPublishProgress {
                Step = (int?)o["step"] ?? 0, Steps = (int?)o["steps"] ?? 0, Phase = (string)o["phase"] ?? "",
                Done = (long?)o["done"] ?? 0, Total = (long?)o["total"] ?? 0, Unit = (string)o["unit"] ?? "",
                Item = (string)o["item"] ?? "", FilesDone = (int?)o["filesDone"] ?? 0, FilesTotal = (int?)o["filesTotal"] ?? 0 };
        }
        catch (Exception) { return null; }
    }
}

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
    /// <param name="onLine">Each output line as it arrives, on a background thread.</param>
    /// <param name="onProgress">Each progress update, on a background thread.</param>
    /// <param name="cancel">Stops the script. Safe at any point: publish again to continue.</param>
    public static Task<(int code, string output)> Publish(DlcPublishingSettings settings, string exportFolder, IEnumerable<string> products,
        bool dryRun, bool allowNewCatalog = false, Action<string> onLine = null, Action<DlcPublishProgress> onProgress = null,
        CancellationToken cancel = default)
    {
        var args = new List<string> { "publish", "--staging", exportFolder, "--progress" };
        var only = products?.ToList();
        if (only != null && only.Count > 0) { args.Add("--product"); args.AddRange(only); }
        if (allowNewCatalog) args.Add("--allow-new-catalog");
        return Run(settings, args, dryRun, onLine, onProgress, cancel);
    }

    /// <summary>Signs in to the container as the Azure CLI user and reads its properties. Writes nothing.</summary>
    public static Task<(int code, string output)> Check(DlcPublishingSettings settings) =>
        Run(settings, new List<string> { "check" }, false);

    /// <summary>Where Download baseline saves the stored content-state file for the current platform and app version.</summary>
    public static string BaselineDownloadPath =>
        Path.GetFullPath(Path.Combine("Library", "DlcBaselines", CurrentPlatform, CurrentAppVersion, "addressables_content_state.bin"));

    /// <summary>Downloads _build-state/&lt;Platform&gt;/&lt;appVersion&gt;/addressables_content_state.bin to <see cref="BaselineDownloadPath"/>.</summary>
    public static Task<(int code, string output)> DownloadBaseline(DlcPublishingSettings settings) =>
        Run(settings, new List<string> { "baseline", "--platform", CurrentPlatform, "--app-version", CurrentAppVersion,
            "--out", BaselineDownloadPath }, false);

    /// <summary>What's published for the current platform and app version, for the build suggestion.</summary>
    public sealed class RemoteVersionState
    {
        public List<string> catalogs = new List<string>();
        public bool baseline;
        public int products;
        public bool HasCatalog => catalogs.Count > 0;
    }

    /// <summary>Reads the remote folder (read-only). Returns null state and an error when it can't be read.</summary>
    public static async Task<(RemoteVersionState state, string error)> RemoteState(DlcPublishingSettings settings)
    {
        var (code, output) = await Run(settings, new List<string> { "status", "--platform", CurrentPlatform,
            "--app-version", CurrentAppVersion, "--state-json" }, false);
        string line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("@@state ", StringComparison.Ordinal));
        if (code != 0 || line == null)
            return (null, output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("error:"))?.Substring(6).Trim() ?? output.Trim());
        try
        {
            var o = JObject.Parse(line.Substring("@@state ".Length));
            return (new RemoteVersionState {
                catalogs = o["catalogs"]?.Select(t => (string)t).ToList() ?? new List<string>(),
                baseline = (bool?)o["baseline"] ?? false, products = (int?)o["products"] ?? 0 }, null);
        }
        catch (Exception e) { return (null, "Couldn't read the remote state: " + e.Message); }
    }

    public static Task<(int code, string output)> Status(DlcPublishingSettings settings) =>
        Run(settings, new List<string> { "status", "--platform", CurrentPlatform, "--app-version", CurrentAppVersion }, false);

    public static Task<(int code, string output)> Rollback(DlcPublishingSettings settings, string productId, string version, bool drop, bool dryRun) =>
        Run(settings, new List<string> { "rollback", "--platform", CurrentPlatform, "--app-version", CurrentAppVersion,
            "--product", productId, drop ? "--drop" : "--to", version }, dryRun);

    const string ProgressPrefix = "@@progress ";
    static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    static async Task<(int code, string output)> Run(DlcPublishingSettings settings, List<string> args, bool dryRun,
        Action<string> onLine = null, Action<DlcPublishProgress> onProgress = null, CancellationToken cancel = default)
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
                var output = new StringBuilder();
                long lastOutputTicks = DateTime.UtcNow.Ticks;

                async Task Pump(StreamReader reader)
                {
                    string l;
                    while ((l = await reader.ReadLineAsync()) != null)
                    {
                        Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
                        if (l.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                        {
                            var progress = DlcPublishProgress.Parse(l.Substring(ProgressPrefix.Length));
                            if (progress != null) onProgress?.Invoke(progress);
                            continue;
                        }
                        lock (output) output.Append(l).Append('\n');
                        onLine?.Invoke(l);
                    }
                }

                var stdout = Task.Run(() => Pump(process.StandardOutput));
                var stderr = Task.Run(() => Pump(process.StandardError));

                // No overall limit: big uploads take as long as they take. Stop only when cancelled, or when the
                // script has printed nothing (not even progress) for IdleTimeout.
                string stopped = await Task.Run(() =>
                {
                    while (!process.WaitForExit(500))
                    {
                        if (cancel.IsCancellationRequested) return "Cancelled.";
                        if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastOutputTicks), DateTimeKind.Utc) > IdleTimeout)
                            return $"No output for {IdleTimeout.TotalMinutes:0} minutes; stopped.";
                    }
                    return null;
                });
                if (stopped != null)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { /* already exited */ }
                    process.WaitForExit(5000);
                }
                await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(5000));

                string text;
                lock (output) text = output.ToString();
                if (stopped == null) return (process.ExitCode, text);
                return (1, text + "\n" + stopped + " Anything uploaded so far stays; publish again to continue " +
                           "(identical files are skipped). Check Logs/dlc-publish for details.");
            }
        }
        catch (Exception e)
        {
            return (1, $"Couldn't run '{settings.pythonExecutable}': {e.Message}. Set pythonExecutable in {settings.name}.");
        }
    }
}
