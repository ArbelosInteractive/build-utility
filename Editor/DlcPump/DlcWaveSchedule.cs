using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

[CreateAssetMenu(menuName = "Build Utility/DLC Wave Schedule")]
public sealed class DlcWaveSchedule : ScriptableObject
{
    public string scheduleId = Guid.NewGuid().ToString("N");
    public string timeZone = "UTC";
    public List<DlcWave> waves = new List<DlcWave>();
}

[Serializable]
public sealed class DlcWave
{
    public string id = Guid.NewGuid().ToString("N");
    public string name = "New wave";
    public string starts = "";
    public string ends = "";
    public bool promotion;
    public bool paused;
    public List<string> productIds = new List<string>();
}

public static class DlcWaveExport
{
    public const string DateFormat = "yyyy-MM-dd HH:mm";
    public static DateTime ToUtc(string local, string zone)
    {
        if (!DateTime.TryParseExact(local, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new InvalidOperationException("Use YYYY-MM-DD HH:mm for dates.");
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        time = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(time) || tz.IsAmbiguousTime(time))
            throw new InvalidOperationException("This local time is skipped or repeated by daylight saving. Choose an unambiguous time.");
        return TimeZoneInfo.ConvertTimeToUtc(time, tz);
    }

    public static List<string> Validate(DlcWaveSchedule schedule, IEnumerable<string> knownProducts)
    {
        var errors = new List<string>();
        if (!schedule) { errors.Add("Select a schedule."); return errors; }
        if (string.IsNullOrWhiteSpace(schedule.scheduleId)) errors.Add("Schedule ID is required.");
        var known = new HashSet<string>(knownProducts ?? Array.Empty<string>(), StringComparer.Ordinal);
        var waveIds = new HashSet<string>(StringComparer.Ordinal);
        var managed = new HashSet<string>(StringComparer.Ordinal);
        if (schedule.waves == null || schedule.waves.Count == 0) { errors.Add("Add a wave."); return errors; }
        foreach (var wave in schedule.waves)
        {
            if (wave == null) { errors.Add("Empty wave."); continue; }
            string label = string.IsNullOrWhiteSpace(wave.name) ? "Unnamed wave" : wave.name;
            if (string.IsNullOrWhiteSpace(wave.name)) errors.Add("Name every wave.");
            if (string.IsNullOrWhiteSpace(wave.id) || !waveIds.Add(wave.id)) errors.Add(label + ": missing/duplicate wave ID.");
            try
            {
                var start = ToUtc(wave.starts, schedule.timeZone);
                if (!string.IsNullOrWhiteSpace(wave.ends) && ToUtc(wave.ends, schedule.timeZone) <= start)
                    errors.Add(label + ": end must be after start.");
            }
            catch (Exception e) { errors.Add(label + ": " + e.Message); }
            if (wave.productIds == null || wave.productIds.Count == 0) { errors.Add(label + ": add products."); continue; }
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in wave.productIds)
            {
                if (string.IsNullOrWhiteSpace(id) || !known.Contains(id)) errors.Add(label + ": unknown product " + id);
                if (!unique.Add(id)) errors.Add(label + ": duplicate product " + id);
                if (!wave.promotion && !managed.Add(id)) errors.Add(id + ": only one release window per product is supported, including paused waves. Use promotion waves for repeat features.");
            }
        }
        return errors;
    }

    public static string State(DlcWave wave, string zone, DateTime utc)
    {
        if (wave.paused) return "Paused";
        var start = ToUtc(wave.starts, zone);
        DateTime? end = string.IsNullOrWhiteSpace(wave.ends) ? (DateTime?)null : ToUtc(wave.ends, zone);
        if (end.HasValue && end.Value <= start) throw new InvalidOperationException("End must be after start.");
        return utc < start ? "Scheduled" : end.HasValue && utc >= end.Value ? "Ended" : "Active";
    }

    public static JObject Build(DlcWaveSchedule schedule, IEnumerable<string> knownProducts)
    {
        var errors = Validate(schedule, knownProducts);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
        return new JObject {
            ["schemaVersion"] = 1, ["scheduleId"] = schedule.scheduleId, ["timeZone"] = schedule.timeZone,
            ["exportOnly"] = true, ["unassignedProducts"] = "unchanged", ["existingOwnersRetainAccess"] = true,
            ["waves"] = new JArray(schedule.waves.OrderBy(w => ToUtc(w.starts, schedule.timeZone)).ThenBy(w => w.id, StringComparer.Ordinal).Select(w => new JObject {
                ["id"] = w.id, ["name"] = w.name, ["kind"] = w.promotion ? "promotion" : "release", ["paused"] = w.paused,
                ["startsAtUtc"] = ToUtc(w.starts, schedule.timeZone).ToString("O", CultureInfo.InvariantCulture),
                ["endsAtUtc"] = string.IsNullOrWhiteSpace(w.ends) ? JValue.CreateNull() : new JValue(ToUtc(w.ends, schedule.timeZone).ToString("O", CultureInfo.InvariantCulture)),
                ["productIds"] = new JArray(w.productIds.OrderBy(id => id, StringComparer.Ordinal)) })) };
    }

    public static DlcWave Next(DlcWave wave, int days)
    {
        if (days < 1 || days > 366) throw new ArgumentOutOfRangeException(nameof(days));
        string Shift(string value) => string.IsNullOrWhiteSpace(value) ? "" : DateTime.ParseExact(value, DateFormat, CultureInfo.InvariantCulture).AddDays(days).ToString(DateFormat, CultureInfo.InvariantCulture);
        return new DlcWave { name = wave.name + " — next", starts = Shift(wave.starts), ends = Shift(wave.ends), promotion = wave.promotion };
    }
}
