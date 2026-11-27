using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

public static class DlcCatalogEditing
{
    // App Store Connect limits for an in-app purchase's display name and description. The game and every store
    // use the catalog's one title and description, so the catalog keeps to the strictest limits.
    public const int TitleMax = 30, DescriptionMax = 45;

    /// <summary>What's wrong with a listing's text, or an empty list when it fits.</summary>
    public static List<string> ListingProblems(string title, string description)
    {
        var problems = new List<string>();
        title = title?.Trim() ?? ""; description = description?.Trim() ?? "";
        if (title.Length == 0) problems.Add("Title is required.");
        else if (title.Length > TitleMax) problems.Add("Title is " + title.Length + " characters; the maximum is " + TitleMax + ".");
        if (description.Length == 0) problems.Add("Description is required.");
        else if (description.Length > DescriptionMax) problems.Add("Description is " + description.Length + " characters; the maximum is " + DescriptionMax + ".");
        return problems;
    }

    /// <summary>Throws when a listing's text doesn't fit the store limits.</summary>
    public static void RequireListing(string id, string title, string description)
    {
        var problems = ListingProblems(title, description);
        if (problems.Count > 0) throw new ArgumentException(id + ": " + string.Join(" ", problems));
    }

    // Patch only marketing fields. Never reconstruct the catalog through a lossy DTO.
    public static JObject Patch(JObject catalog, string id, string title, string description)
    {
        RequireListing(id, title, description);
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
