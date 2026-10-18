using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
#if ARBELOS_DLC_LAYOUT_V2
using UnityEditor.AddressableAssets.Build.Layout;
#endif

public static class DlcBuildLayoutReport
{
    public static JObject Analyze(string path, JObject manifest)
    {
#if ARBELOS_DLC_LAYOUT_V2
        var layout = BuildLayout.Open(path, false);
        if (layout == null) throw new InvalidOperationException("Cannot open Addressables build layout.");
        try
        {
            if (!layout.ReadFull()) throw new InvalidOperationException("Invalid or unsupported build layout.");
            var bundles = layout.Groups.SelectMany(g => g.Bundles).Concat(layout.BuiltInBundles).Distinct().ToArray();
            var products = new JArray();
            foreach (var product in (JArray)manifest["products"])
            {
                string id = (string)product["id"];
                var paths = new HashSet<string>(((JArray)manifest["content"]).Where(c => (string)c["productId"] == id).Select(c => (string)c["path"]));
                var selected = new HashSet<BuildLayout.Bundle>();
                void Include(BuildLayout.Bundle bundle)
                {
                    if (bundle == null || !selected.Add(bundle)) return;
                    foreach (var dependency in bundle.Dependencies ?? new List<BuildLayout.Bundle>()) Include(dependency);
                }
                foreach (var bundle in bundles)
                    if (bundle.Files.Any(f => f.Assets.Any(a => paths.Contains(a.AssetPath)) || f.OtherAssets.Any(a => paths.Contains(a.AssetPath)))) Include(bundle);
                products.Add(new JObject { ["productId"] = id,
                    ["bundleBytesIncludingDependencies"] = selected.Aggregate(0UL, (sum, b) => checked(sum + b.FileSize)),
                    ["bundles"] = new JArray(selected.OrderBy(b => b.Name).Select(b => new JObject { ["name"] = b.Name, ["bytes"] = b.FileSize })),
                    ["status"] = selected.Count == 0 ? "No matching bundle assets; content may be client-included or absent from this build." : "Mapped to bundle files." });
            }
            return new JObject { ["schemaVersion"] = 1, ["releaseId"] = manifest["releaseId"],
                ["layoutSha256"] = DlcReleaseService.HashFile(path), ["products"] = products,
                ["duplicatedAssetGuids"] = new JArray(layout.DuplicatedAssets.Select(d => d.AssetGuid)),
                ["scope"] = "Bundle file sizes including dependencies, assuming an empty cache. Shared bundles are counted once per product. Local bundles, cached content and catalog metadata affect actual network bytes. Use a layout from this release's build." };
        }
        finally { layout.Close(); }
#else
        throw new NotSupportedException("Build-layout analysis requires Addressables 2.x or newer. Other DLC Pump features support the package's existing Addressables dependency.");
#endif
    }
}
