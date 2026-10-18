using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

public static class DlcStoreReadiness
{
    // A credential-free handoff schema, not an API request or proof of registration.
    public static JObject Requirements(JObject manifest)
    {
        return new JObject { ["schemaVersion"] = 1, ["releaseId"] = manifest["releaseId"],
            ["minimumClientVersion"] = manifest["minimumClientVersion"],
            ["products"] = new JArray(((JArray)manifest["products"]).Select(p => new JObject {
                ["id"] = p["id"], ["type"] = "NonConsumable", ["title"] = p["defaultDescription"]?["title"],
                ["description"] = p["defaultDescription"]?["description"], ["storeIDs"] = p["storeIDs"]?.DeepClone(),
                ["playFabFriendlyId"] = p["id"], ["contentType"] = p["payouts"]?.FirstOrDefault()?["st"] })) };
    }

    public static string Compare(JObject required, JObject observed)
    {
        if ((int?)observed["schemaVersion"] != 1 || !(observed["products"] is JArray products))
            throw new InvalidOperationException("Expected a DLC Pump normalized store snapshot (schemaVersion 1, products array).");
        string environment = (string)observed["environment"];
        string source = (string)observed["source"];
        if (string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(source) ||
            !DateTimeOffset.TryParse((string)observed["capturedUtc"], out var captured))
            throw new InvalidOperationException("Store snapshot must identify environment, source and capturedUtc.");
        var output = new List<string> { "Snapshot source: " + source + "; environment: " + environment + "; captured: " + captured.ToString("O"),
            "This compares supplied data. It does not perform live verification or purchase tests." };
        if (DateTimeOffset.UtcNow - captured > TimeSpan.FromDays(1)) output.Add("WARNING: Snapshot is older than 24 hours.");
        foreach (var item in (JArray)required["products"])
        {
            string id = (string)item["id"];
            var found = products.Where(p => (string)p["id"] == id).ToArray();
            if (found.Length != 1) { output.Add("ERROR: Missing or duplicated product " + id); continue; }
            foreach (string field in new[] { "type", "playFabFriendlyId", "contentType" })
                if ((string)found[0][field] != (string)item[field]) output.Add("ERROR: " + id + " mismatched " + field);
            if ((bool?)found[0]["available"] != true) output.Add("ERROR: " + id + " is not confirmed available in this snapshot.");
        }
        if (!output.Any(s => s.StartsWith("ERROR:"))) output.Add("PASS supplied snapshot matches required IDs, grant types and availability. Live purchase/download verification remains separate.");
        return string.Join("\n", output);
    }
}
