using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

public sealed class DlcPlayFabChange
{
    public string productId;
    public JObject before, after;
    public byte[] png;
    public string imageHash;
    public bool verified;
}

public static class DlcPlayFabPlan
{
    public static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    public static JObject Editable(JObject item)
    {
        var copy = (JObject)item.DeepClone();
        foreach (string key in new[] { "CreationDate", "LastModifiedDate", "Moderation", "Rating", "CreatorEntity", "FriendlyId" }) copy.Remove(key);
        return copy;
    }
    static JToken ComparableImages(JToken value)
    {
        var copy = value?.DeepClone();
        if (copy is JArray images) foreach (var image in images.OfType<JObject>())
        {
            if (Uri.TryCreate((string)image["Url"], UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                System.Text.RegularExpressions.Regex.IsMatch(uri.Host, @"^(pfcatalogcontent\d+\.blob\.core\.windows\.net|content\d+\.prod\.catalog\.playfab\.com)$"))
                image["Url"] = "playfab-content:" + uri.AbsolutePath;
        }
        return copy;
    }
    static bool TryUtc(JToken value, out DateTime utc)
    {
        if (value?.Type == JTokenType.Date) { utc = value.Value<DateTime>().ToUniversalTime(); return true; }
        return DateTime.TryParse((string)value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out utc);
    }
    static JObject Comparable(JObject item)
    {
        var copy = Editable(item); copy.Remove("ETag");
        if (copy["Images"] != null) copy["Images"] = ComparableImages(copy["Images"]);
        foreach (string field in new[] { "StartDate", "EndDate" })
        {
            if (copy[field] == null || copy[field].Type == JTokenType.Null) copy.Remove(field);
            else if (TryUtc(copy[field], out var utc)) copy[field] = utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
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
    public static async Task<DlcPlayFabChange> Read(IDlcPlayFabTransport api, string productId, JObject schedule, byte[] png, bool pixelArt, CancellationToken cancel)
    {
        var key = new JObject { ["AlternateId"] = new JObject { ["Type"] = "FriendlyId", ["Value"] = productId } };
        var published = (await api.Call("GetItem", key, cancel))["Item"] as JObject ?? throw new InvalidOperationException("Missing published item " + productId);
        var draft = (await api.Call("GetDraftItem", new JObject { ["Id"] = published["Id"].DeepClone() }, cancel))["Item"] as JObject ?? throw new InvalidOperationException("Missing draft item " + productId);
        if (string.IsNullOrEmpty((string)draft["ETag"])) throw new InvalidOperationException("Missing draft ETag for " + productId);
        if (!JToken.DeepEquals(Comparable(published), Comparable(draft))) throw new InvalidOperationException(productId + " has unpublished changes in PlayFab. Resolve those changes before reviewing this plan.");
        var after = Apply(draft, productId, schedule); string hash = png == null ? null : Hash(png);
        if (png != null)
        {
            if (!(after["DisplayProperties"] is JObject)) after["DisplayProperties"] = new JObject();
            var display = (JObject)after["DisplayProperties"];
            if (!(display["dlcPump"] is JObject)) display["dlcPump"] = new JObject();
            var pump = (JObject)display["dlcPump"];
            if ((string)pump["thumbnailSha256"] == hash && (bool?)pump["pixelArt"] == pixelArt && (after["Images"] as JArray)?.Any(i => (string)i["Type"] == "Thumbnail" && !string.IsNullOrEmpty((string)i["Url"])) == true) png = null;
            else { pump["thumbnailSha256"] = hash; pump["pixelArt"] = pixelArt; }
        }
        if (System.Text.Encoding.UTF8.GetByteCount(after["DisplayProperties"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "") > 10000) throw new InvalidOperationException(productId + " exceeds the 10 KB display-properties limit. Reduce its promotions or existing metadata.");
        return new DlcPlayFabChange { productId = productId, before = draft, after = after, png = png, imageHash = hash };
    }
    public static JObject Summary(DlcPlayFabChange change)
    {
        var differences = new JObject();
        var before = Comparable(change.before); var after = Comparable(change.after);
        foreach (var property in after.Properties()) if (!JToken.DeepEquals(before[property.Name], property.Value)) differences[property.Name] = new JObject { ["before"] = before[property.Name]?.DeepClone(), ["after"] = property.Value.DeepClone() };
        return new JObject { ["productId"] = change.productId, ["itemId"] = change.before["Id"].DeepClone(), ["changes"] = differences, ["uploadThumbnail"] = change.png != null, ["thumbnailSha256"] = change.imageHash };
    }
    public static async Task Publish(IDlcPlayFabTransport api, DlcPlayFabChange change, CancellationToken cancel)
    {
        change.verified = false;
        var key = new JObject { ["Id"] = change.before["Id"].DeepClone() };
        var current = (await api.Call("GetDraftItem", key, cancel))["Item"] as JObject;
        if (current == null || (string)current["ETag"] != (string)change.before["ETag"]) throw new InvalidOperationException(change.productId + " changed since review. Review again.");
        var updated = (JObject)change.after.DeepClone();
        if (change.png != null)
        {
            var urls = await api.Call("CreateUploadUrls", new JObject { ["Files"] = new JArray(new JObject { ["FileName"] = "icon-" + change.imageHash + ".png" }) }, cancel);
            var media = urls["UploadUrls"]?.FirstOrDefault() as JObject ?? throw new InvalidOperationException("No upload URL was returned.");
            string url = (string)media["Url"], id = (string)media["Id"];
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("No image ID was returned.");
            await api.Upload(url, change.png, cancel);
            var images = updated["Images"] as JArray ?? new JArray();
            updated["Images"] = new JArray(images.Where(i => (string)i["Type"] != "Thumbnail").Select(i => i.DeepClone()));
            ((JArray)updated["Images"]).Add(new JObject { ["Id"] = id, ["Type"] = "Thumbnail", ["Url"] = new Uri(url).GetLeftPart(UriPartial.Path) });
        }
        if (!JToken.DeepEquals(Comparable(change.before), Comparable(updated)))
        {
            // ETag inside CatalogItem protects UpdateDraftItem. Publish also supplies the resulting ETag explicitly.
            var result = (await api.Call("UpdateDraftItem", new JObject { ["Item"] = updated, ["Publish"] = false }, cancel))["Item"] as JObject;
            if (result == null || string.IsNullOrEmpty((string)result["ETag"])) throw new InvalidOperationException("Updated draft did not return an ETag. Inspect PlayFab before retrying.");
            await api.Call("PublishDraftItem", new JObject { ["Id"] = result["Id"].DeepClone(), ["ETag"] = result["ETag"].DeepClone() }, cancel);
            bool complete = false;
            for (int i = 0; i < 20; i++)
            {
                var state = await api.Call("GetItemPublishStatus", key, cancel);
                if ((string)state["Result"] == "Succeeded") { complete = true; break; }
                if ((string)state["Result"] == "Failed") throw new InvalidOperationException("PlayFab publish failed for " + change.productId);
                await Task.Delay(1500, cancel);
            }
            if (!complete) throw new InvalidOperationException("Publish status is still pending. Inspect PlayFab before retrying.");
        }
        var readback = (await api.Call("GetItems", new JObject { ["Ids"] = new JArray(change.before["Id"].DeepClone()) }, cancel))["Items"]?.OfType<JObject>().FirstOrDefault(i => (string)i["Id"] == (string)change.before["Id"]) ?? throw new InvalidOperationException("Published item was not returned.");
        foreach (string field in new[] { "StartDate", "EndDate", "IsHidden", "DisplayProperties", "Images" })
        {
            // Dates returned by JSON.NET may be parsed as dates; compare normalized representations.
            var expected = updated[field]; var actual = readback[field];
            if ((expected == null || expected.Type == JTokenType.Null) && (actual == null || actual.Type == JTokenType.Null)) continue;
            if (field.EndsWith("Date") && TryUtc(expected, out var a) && TryUtc(actual, out var b) && a == b) continue;
            if (field == "Images" && JToken.DeepEquals(ComparableImages(expected), ComparableImages(actual))) continue;
            if (!JToken.DeepEquals(expected, actual)) throw new InvalidOperationException("Read-back mismatch for " + change.productId + " / " + field + ". Review again.");
        }
        change.verified = true;
    }
}
