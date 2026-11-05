using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Runs one of the package's Python tools (Tools~/...) for an editor window, streaming its output.
/// Lines of the form "@@kind {json}" are machine-readable: they go to <c>onMachine</c> and are left out of the
/// returned output. Cancelling, or no output at all for <c>idleTimeout</c>, kills the whole process tree
/// (the IAP uploader starts one Python process per store).
/// </summary>
public static class DlcPythonProcess
{
    public static string PackageTool(params string[] parts)
    {
        var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DlcPythonProcess).Assembly);
        return Path.Combine(new[] { package.resolvedPath, "Tools~" }.Concat(parts).ToArray());
    }

    /// <param name="onLine">Each normal output line, on a background thread.</param>
    /// <param name="onMachine">(kind, json) for each "@@kind {json}" line, on a background thread.</param>
    public static async Task<(int code, string output)> Run(string python, IEnumerable<string> args,
        Action<string> onLine = null, Action<string, string> onMachine = null, CancellationToken cancel = default,
        TimeSpan? idleTimeout = null, IDictionary<string, string> environment = null)
    {
        if (string.IsNullOrWhiteSpace(python)) python = "python";
        var line = new StringBuilder();
        foreach (string a in args) line.Append('"').Append(a.Replace("\"", "\\\"")).Append("\" ");
        var start = new ProcessStartInfo(python, line.ToString()) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Directory.GetCurrentDirectory() };
        start.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        // Azure credentials run az themselves; make sure it's found even if it was installed after Unity started.
        string azDir = DlcAzureCli.Directory();
        if (azDir != null) start.EnvironmentVariables["PATH"] = azDir + Path.PathSeparator + start.EnvironmentVariables["PATH"];
        if (environment != null) foreach (var kv in environment) start.EnvironmentVariables[kv.Key] = kv.Value;
        var idle = idleTimeout ?? TimeSpan.FromMinutes(10);

        Process process;
        try { process = Process.Start(start); }
        catch (Exception e) { return (1, $"Couldn't run '{python}': {e.Message}. Set the Python executable in the settings asset."); }

        using (process)
        {
            var output = new StringBuilder();
            long lastOutputTicks = DateTime.UtcNow.Ticks;

            async Task Pump(StreamReader reader)
            {
                string l;
                while ((l = await reader.ReadLineAsync()) != null)
                {
                    Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
                    if (l.StartsWith("@@", StringComparison.Ordinal))
                    {
                        int space = l.IndexOf(' ');
                        if (space > 2)
                        {
                            try { onMachine?.Invoke(l.Substring(2, space - 2), l.Substring(space + 1)); }
                            catch (Exception e) { UnityEngine.Debug.LogException(e); }
                            continue;
                        }
                    }
                    lock (output) output.Append(l).Append('\n');
                    try { onLine?.Invoke(l); }
                    catch (Exception e) { UnityEngine.Debug.LogException(e); }
                }
            }

            var stdout = Task.Run(() => Pump(process.StandardOutput));
            var stderr = Task.Run(() => Pump(process.StandardError));

            string stopped = await Task.Run(() =>
            {
                while (!process.WaitForExit(500))
                {
                    if (cancel.IsCancellationRequested) return "Cancelled.";
                    if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastOutputTicks), DateTimeKind.Utc) > idle)
                        return $"No output for {idle.TotalMinutes:0} minutes; stopped.";
                }
                return null;
            });
            if (stopped != null)
            {
                KillTree(process);
                process.WaitForExit(5000);
            }
            await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(5000));

            string text;
            lock (output) text = output.ToString();
            return stopped == null ? (process.ExitCode, text) : (1, text + "\n" + stopped);
        }
    }

    static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited) return;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                using (var kill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {process.Id} /T /F") {
                           UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                    kill?.WaitForExit(5000);
            }
            if (!process.HasExited) process.Kill();
        }
        catch (Exception) { /* already gone */ }
    }
}
