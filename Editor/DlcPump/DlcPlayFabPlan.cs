using System;
using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>
/// Applies a release-wave schedule to a PlayFab catalog item: a release wave sets StartDate, EndDate and IsHidden
/// (paused = hidden), and every wave the product is in is recorded in DisplayProperties.dlcPump (schedule ID and
/// promotions). DLC Store Export uses it for the exported items; the IAP uploader writes them to PlayFab.
/// </summary>
public static class DlcPlayFabPlan
{
    public static JObject Editable(JObject item)
    {
        var copy = (JObject)item.DeepClone();
        foreach (string key in new[] { "CreationDate", "LastModifiedDate", "Moderation", "Rating", "CreatorEntity", "FriendlyId" }) copy.Remove(key);
        return copy;
    }
    public static JObject Apply(JObject existing, string productId, JObject schedule)
    {
        var item = Editable(existing);
        var waves = (schedule?["waves"] as JArray ?? new JArray()).OfType<JObject>().Where(w => (w["productIds"] as JArray)?.Any(id => (string)id == productId) == true).ToArray();
        var releases = waves.Where(w => (string)w["kind"] == "release").ToArray();
        if (releases.Length > 1) throw new InvalidOperationException("Multiple availability windows for " + productId);
        if (releases.Length == 1)
        {
            item["StartDate"] = releases[0]["startsAtUtc"].DeepClone();
            item["EndDate"] = releases[0]["endsAtUtc"]?.DeepClone() ?? JValue.CreateNull();
            item["IsHidden"] = (bool?)releases[0]["paused"] == true;
        }
        if (waves.Length > 0)
        {
            if (!(item["DisplayProperties"] is JObject)) item["DisplayProperties"] = new JObject();
            var display = (JObject)item["DisplayProperties"];
            if (!(display["dlcPump"] is JObject)) display["dlcPump"] = new JObject();
            var pump = (JObject)display["dlcPump"];
            if (pump["scheduleId"] != null && (string)pump["scheduleId"] != (string)schedule["scheduleId"]) throw new InvalidOperationException(productId + " is managed by a different schedule. Resolve ownership before publishing.");
            pump["scheduleId"] = schedule["scheduleId"].DeepClone();
            pump["promotions"] = new JArray(waves.Where(w => (string)w["kind"] == "promotion").Select(w => new JObject { ["id"] = w["id"].DeepClone(), ["startsAtUtc"] = w["startsAtUtc"].DeepClone(), ["endsAtUtc"] = w["endsAtUtc"]?.DeepClone() ?? JValue.CreateNull(), ["paused"] = (bool?)w["paused"] == true }));
        }
        return item;
    }
}
