using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// Azure CLI sign-in for the DLC Publish window, the same way the IAP Uploader does it. publish_dlc.py authenticates
/// with AzureCliCredential, so whoever is signed in here is who uploads. No keys are stored.
/// </summary>
public static class DlcAzureCli
{
    const string NotInstalled = "Azure CLI not installed (winget install Microsoft.AzureCLI, then restart Unity)";

    /// <summary>
    /// Full path to az, or null. Also checks the default install folders, because Unity's PATH is fixed when it
    /// starts and misses a CLI installed afterwards.
    /// </summary>
    public static string Find()
    {
        bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        var names = windows ? new[] { "az.cmd", "az.exe" } : new[] { "az" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0).ToList();
        if (windows)
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (!string.IsNullOrEmpty(root)) dirs.Add(Path.Combine(root, "Microsoft SDKs", "Azure", "CLI2", "wbin"));
        }
        else dirs.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" });

        foreach (var dir in dirs)
            foreach (var name in names)
            {
                try
                {
                    string path = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    /// <summary>Folder containing az, for putting on the PATH of child processes (publish_dlc.py).</summary>
    public static string Directory() => Find() is string az ? Path.GetDirectoryName(az) : null;

    /// <summary>(signed-in user, null) or (null, reason).</summary>
    public static async Task<(string user, string reason)> Account()
    {
        if (Find() == null) return (null, NotInstalled);
        var r = await Run(new[] { "account", "show", "--query", "user.name", "-o", "tsv" }, 60);
        string user = r.stdout.Trim();
        return r.code == 0 && user.Length > 0 ? (user, null) : (null, r.code == -1 ? r.stderr : "not signed in");
    }

    static Process loginProcess;

    /// <summary>True while a sign-in started by <see cref="Login"/> is still waiting for the browser.</summary>
    public static bool SigningIn => loginProcess != null;

    /// <summary>
    /// Opens the browser sign-in. Returns (ok, message).
    /// The Windows account broker ("Sign in with work account" popup) is turned off for this call: started
    /// from Unity, az has no window for the popup, so the popup closes and az waits forever. The interactive
    /// subscription picker is turned off too, because there is no console to answer it.
    /// </summary>
    public static async Task<(bool ok, string message)> Login(string tenant)
    {
        if (Find() == null) return (false, NotInstalled);
        var args = new List<string> { "login", "--allow-no-subscriptions" };
        if (!string.IsNullOrWhiteSpace(tenant)) { args.Add("--tenant"); args.Add(tenant.Trim()); }
        var env = new Dictionary<string, string> {
            ["AZURE_CORE_ENABLE_BROKER_ON_WINDOWS"] = "false",
            ["AZURE_CORE_LOGIN_EXPERIENCE_V2"] = "off" };
        var r = await Run(args, 300, env, p => loginProcess = p);
        loginProcess = null;
        string detail = (r.stderr.Trim().Length > 0 ? r.stderr : r.stdout).Trim();
        if (detail.Length > 600) detail = detail.Substring(detail.Length - 600);
        return (r.code == 0, r.code == -2 ? "Sign-in timed out after 5 minutes."
            : r.code == -3 ? "Sign-in cancelled." : detail);
    }

    /// <summary>Stops a sign-in that is waiting for the browser.</summary>
    public static void CancelLogin()
    {
        var p = loginProcess;
        if (p == null) return;
        cancelled.Add(p.Id);
        KillTree(p);
    }

    static readonly HashSet<int> cancelled = new HashSet<int>();

    static void KillTree(Process p)
    {
        try
        {
            if (p.HasExited) return;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // az.cmd -> python.exe: kill the whole tree, not just cmd.exe.
                using (var k = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { UseShellExecute = false, CreateNoWindow = true }))
                    k?.WaitForExit(10000);
            }
            if (!p.HasExited) p.Kill();
        }
        catch (Exception) { }
    }

    public static Task Logout() => Find() == null ? Task.CompletedTask : Run(new[] { "logout" }, 60);

    /// <summary>Reads one Key Vault secret as the signed-in user. The value is returned, never logged.</summary>
    public static async Task<string> ReadSecret(string vault, string name)
    {
        if (Find() == null) throw new InvalidOperationException(NotInstalled);
        if (!System.Text.RegularExpressions.Regex.IsMatch(vault ?? "", "^[A-Za-z0-9-]{3,24}$")) throw new InvalidOperationException("Enter the Key Vault name.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "^[A-Za-z0-9-]{1,127}$")) throw new InvalidOperationException("Enter the Key Vault secret name.");
        var r = await Run(new[] { "keyvault", "secret", "show", "--vault-name", vault, "--name", name, "--query", "value", "-o", "tsv" }, 90);
        string value = r.stdout.Trim();
        if (r.code != 0 || value.Length == 0)
        {
            string reason = (r.stderr ?? "").Trim().Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? $"exit {r.code}";
            throw new InvalidOperationException($"Couldn't read secret '{name}' from Key Vault '{vault}': {reason}. Sign in to Azure, and check you have the 'Key Vault Secrets User' role.");
        }
        return value;
    }

    /// <summary>code -1: couldn't start, -2: timed out, -3: cancelled.</summary>
    static async Task<(int code, string stdout, string stderr)> Run(IEnumerable<string> args, int timeoutSeconds,
        Dictionary<string, string> env = null, Action<Process> started = null)
    {
        string az = Find();
        string line = string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""));
        bool cmd = az.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(cmd ? "cmd.exe" : az, cmd ? "/d /s /c \"\"" + az + "\" " + line + "\"" : line) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true };
        start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        if (env != null) foreach (var kv in env) start.EnvironmentVariables[kv.Key] = kv.Value;
        try
        {
            using (var process = Process.Start(start))
            {
                process.StandardInput.Close(); // any prompt gets end-of-input and fails instead of hanging
                started?.Invoke(process);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                bool exited = await Task.Run(() => process.WaitForExit(timeoutSeconds * 1000));
                if (!exited) { KillTree(process); return (-2, "", "timed out"); }
                if (cancelled.Remove(process.Id)) return (-3, "", "cancelled");
                return (process.ExitCode, await stdout, await stderr);
            }
        }
        catch (Exception e) { return (-1, "", "couldn't run the Azure CLI: " + e.Message); }
    }
}
