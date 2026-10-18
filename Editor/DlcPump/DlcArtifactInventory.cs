using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

// Inventories built files, independently of source-asset size estimates.
public static class DlcArtifactInventory
{
    public static JObject Capture(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = new JArray();
        Walk(root, root, files);
        if (files.Count == 0) throw new InvalidOperationException("The artifact directory is empty.");
        return new JObject { ["schemaVersion"] = 1, ["capturedUtc"] = DateTime.UtcNow.ToString("O"),
            ["totalBytes"] = files.Sum(f => (long)f["bytes"]), ["files"] = files,
            ["scope"] = "All files in the selected output directory. This does not prove which bundles a client will download." };
    }

    static void Walk(string root, string directory, JArray files)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Artifact directories cannot contain symbolic links or junctions: " + directory);
        foreach (var path in Directory.GetFiles(directory).OrderBy(p => p, StringComparer.Ordinal))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Artifact files cannot be symbolic links: " + path);
            files.Add(new JObject { ["path"] = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/'),
                ["bytes"] = new FileInfo(path).Length, ["sha256"] = DlcReleaseService.HashFile(path) });
        }
        foreach (var child in Directory.GetDirectories(directory).OrderBy(p => p, StringComparer.Ordinal)) Walk(root, child, files);
    }

    public static string Verify(JObject inventory, string directory)
    {
        if ((int?)inventory["schemaVersion"] != 1 || !(inventory["files"] is JArray expected))
            throw new InvalidOperationException("Unsupported artifact inventory.");
        var actual = (JArray)Capture(directory)["files"];
        var found = actual.ToDictionary(f => (string)f["path"], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var file in expected)
        {
            var path = (string)file["path"];
            if (string.IsNullOrEmpty(path) || !seen.Add(path)) throw new InvalidOperationException("Invalid or duplicate inventory path.");
            if (!found.TryGetValue(path, out var match)) errors.Add("MISSING " + path);
            else if (!JToken.DeepEquals(file, match)) errors.Add("CHANGED " + path);
        }
        foreach (var path in found.Keys.Where(p => !seen.Contains(p))) errors.Add("UNEXPECTED " + path);
        return errors.Count == 0 ? "PASS: Every artifact matches its recorded size and SHA-256." : string.Join("\n", errors);
    }
}
