using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

public static class DlcCatalogEditing
{
    // Patch only marketing fields. Never reconstruct the catalog through a lossy DTO.
    public static JObject Patch(JObject catalog, string id, string title, string description)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Title and description are required.");
        var copy = (JObject)catalog.DeepClone();
        var matches = ((JArray)copy["products"]).Where(p => (string)p["id"] == id).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Product ID must resolve uniquely: " + id);
        var product = matches[0];
        if (!(product["defaultDescription"] is JObject)) product["defaultDescription"] = new JObject();
        product["defaultDescription"]["title"] = title.Trim();
        product["defaultDescription"]["description"] = description.Trim();
        return copy;
    }

    public static string Save(string expectedHash, JObject updated)
    {
        var path = DlcReleaseService.CatalogPath;
        if (DlcReleaseService.Hash(File.ReadAllBytes(path)) != expectedHash) throw new InvalidOperationException("Catalog changed since loading. Reload before saving.");
        string directory = DlcProject.Required.BackupDirectory;
        Directory.CreateDirectory(directory);
        string backup = Path.GetFullPath(Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N") + ".json"));
        string temporary = path + ".dlcpump.tmp";
        DlcReleaseService.WriteNew(temporary, updated.ToString());
        try
        {
            if (DlcReleaseService.Hash(File.ReadAllBytes(path)) != expectedHash) throw new InvalidOperationException("Catalog changed while preparing save.");
            File.Replace(temporary, path, backup);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        AssetDatabase.ImportAsset(path);
        return backup;
    }
}
