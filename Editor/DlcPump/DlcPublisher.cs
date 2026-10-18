using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;

public static class DlcPublisher
{
    public static async Task<string> Run(DlcPublishingSettings settings, string action, string manifest, string buildReceipt, string inventory, string outputDirectory, string receipt)
    {
        if (!settings) throw new InvalidOperationException("Select publishing settings.");
        var package = PackageInfo.FindForAssembly(typeof(DlcPublisher).Assembly);
        string script = Path.Combine(package.resolvedPath, "Tools~/DlcPublisher/publish.mjs");
        var release = JObject.Parse(File.ReadAllText(manifest));
        string prefix = settings.releasePrefix.Trim('/') + "/" + (string)release["releaseId"];
        var request = new JObject { ["action"] = action, ["environment"] = settings.environment,
            ["endpoint"] = settings.azureEndpoint, ["account"] = settings.azureAccount,
            ["container"] = settings.azureContainer, ["releasePrefix"] = prefix,
            ["localEmulator"] = settings.localEmulator, ["publicBlobRead"] = settings.publicBlobRead,
            ["keyEnvironmentVariable"] = settings.keyEnvironmentVariable,
            ["dependenciesDirectory"] = Path.GetFullPath(settings.dependenciesDirectory),
            ["manifestPath"] = Path.GetFullPath(manifest), ["buildReceiptPath"] = Path.GetFullPath(buildReceipt),
            ["inventoryPath"] = Path.GetFullPath(inventory), ["outputDirectory"] = Path.GetFullPath(outputDirectory),
            ["receiptPath"] = Path.GetFullPath(receipt) };
        var start = new ProcessStartInfo(settings.nodeExecutable, "\"" + script.Replace("\"", "\\\"") + "\"") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Directory.GetCurrentDirectory() };
        using (var process = Process.Start(start))
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(request.ToString());
            process.StandardInput.Close();
            bool exited = await Task.Run(() => process.WaitForExit(900000));
            if (!exited) { process.Kill(); throw new TimeoutException("DLC publishing timed out. No completed verification receipt was recorded."); }
            string result = await stdout, error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("DLC publishing failed: " + error);
            return result;
        }
    }
}
