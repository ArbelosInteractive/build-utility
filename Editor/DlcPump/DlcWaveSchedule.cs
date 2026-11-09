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
        // Release windows per product. A product may be in several release waves (e.g. a limited run now and a
        // re-release later) as long as the windows don't overlap; paused waves count too.
        var windows = new Dictionary<string, List<(DlcWave wave, DateTime start, DateTime? end)>>(StringComparer.Ordinal);
        if (schedule.waves == null || schedule.waves.Count == 0) { errors.Add("Add a wave."); return errors; }
        foreach (var wave in schedule.waves)
        {
            if (wave == null) { errors.Add("Empty wave."); continue; }
            string label = Label(wave);
            if (string.IsNullOrWhiteSpace(wave.name)) errors.Add("Name every wave.");
            if (string.IsNullOrWhiteSpace(wave.id) || !waveIds.Add(wave.id)) errors.Add(label + ": missing/duplicate wave ID.");
            bool datesOk = TryWindow(wave, schedule.timeZone, out var start, out var end, out string dateError);
            if (!datesOk) errors.Add(label + ": " + dateError);
            if (wave.productIds == null || wave.productIds.Count == 0) { errors.Add(label + ": add products."); continue; }
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in wave.productIds)
            {
                if (string.IsNullOrWhiteSpace(id) || !known.Contains(id)) errors.Add(label + ": unknown product " + id);
                if (!unique.Add(id)) { errors.Add(label + ": duplicate product " + id); continue; }
                if (!wave.promotion && datesOk && !string.IsNullOrWhiteSpace(id))
                {
                    if (!windows.TryGetValue(id, out var list)) windows[id] = list = new List<(DlcWave, DateTime, DateTime?)>();
                    list.Add((wave, start, end));
                }
            }
        }
        foreach (var kv in windows.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var list = kv.Value.OrderBy(w => w.start).ToList();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    if (Overlaps(list[i].start, list[i].end, list[j].start, list[j].end))
                        errors.Add($"{kv.Key}: release waves '{Label(list[i].wave)}' ({Describe(list[i].wave)}) and '{Label(list[j].wave)}' ({Describe(list[j].wave)}) overlap. " +
                                   "A product can be in several release waves only if their dates don't overlap (a blank end means it never ends).");
        }
        return errors;
    }

    /// <summary>
    /// Checks the waves chosen for one export. A PlayFab item has a single start/end at a time, so a product may be
    /// in only one selected release wave; and every product in a selected wave must be part of the export.
    /// </summary>
    public static List<string> ValidateSelection(DlcWaveSchedule schedule, ICollection<string> waveIds, ICollection<string> exportProducts = null)
    {
        var errors = new List<string>();
        if (!schedule) { errors.Add("Select a wave schedule."); return errors; }
        if (waveIds == null || waveIds.Count == 0) { errors.Add("Select at least one wave."); return errors; }
        var byId = (schedule.waves ?? new List<DlcWave>()).Where(w => w != null && !string.IsNullOrEmpty(w.id))
            .GroupBy(w => w.id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (string id in waveIds.Where(id => !byId.ContainsKey(id))) errors.Add($"Selected wave {id} isn't in {schedule.name}.");
        var selected = waveIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        foreach (var group in selected.Where(w => !w.promotion).SelectMany(w => (w.productIds ?? new List<string>()).Distinct().Select(p => (p, w)))
                     .GroupBy(x => x.p, StringComparer.Ordinal).Where(g => g.Count() > 1).OrderBy(g => g.Key, StringComparer.Ordinal))
            errors.Add($"{group.Key} is in several selected release waves ({string.Join(", ", group.Select(x => "'" + Label(x.w) + "'"))}). " +
                       "A PlayFab item has one start and end date at a time: export one of these waves now and the other one later.");
        if (exportProducts != null)
            foreach (var w in selected)
                foreach (string p in (w.productIds ?? new List<string>()).Where(p => !exportProducts.Contains(p)).Distinct())
                    errors.Add($"'{Label(w)}': {p} isn't in this store export, so its dates can't be set. Add it to the store export settings, or deselect the wave.");
        return errors;
    }

    static string Label(DlcWave wave) => string.IsNullOrWhiteSpace(wave?.name) ? "Unnamed wave" : wave.name;

    /// <summary>"start → end" as entered (local to the schedule's time zone).</summary>
    public static string Describe(DlcWave wave) =>
        $"{(string.IsNullOrWhiteSpace(wave.starts) ? "no start" : wave.starts)} → {(string.IsNullOrWhiteSpace(wave.ends) ? "no end" : wave.ends)}";

    public static bool TryWindow(DlcWave wave, string zone, out DateTime start, out DateTime? end, out string error)
    {
        start = default; end = null; error = null;
        try
        {
            start = ToUtc(wave.starts, zone);
            end = string.IsNullOrWhiteSpace(wave.ends) ? (DateTime?)null : ToUtc(wave.ends, zone);
            if (end.HasValue && end.Value <= start) { error = "end must be after start."; return false; }
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    /// <summary>Half-open windows [start, end); a null end never ends.</summary>
    public static bool Overlaps(DateTime aStart, DateTime? aEnd, DateTime bStart, DateTime? bEnd) =>
        aStart < (bEnd ?? DateTime.MaxValue) && bStart < (aEnd ?? DateTime.MaxValue);

    public static string State(DlcWave wave, string zone, DateTime utc)
    {
        if (wave.paused) return "Paused";
        var start = ToUtc(wave.starts, zone);
        DateTime? end = string.IsNullOrWhiteSpace(wave.ends) ? (DateTime?)null : ToUtc(wave.ends, zone);
        if (end.HasValue && end.Value <= start) throw new InvalidOperationException("End must be after start.");
        return utc < start ? "Scheduled" : end.HasValue && utc >= end.Value ? "Ended" : "Active";
    }

    /// <summary>
    /// The schedule document consumed by <see cref="DlcPlayFabPlan.Apply"/>. Each product ends up in at most one
    /// release wave, because a PlayFab item has one start/end at a time:
    /// <list type="bullet">
    /// <item>With <paramref name="waveIds"/> (a store export): only those waves; a product in two of them is an error.</item>
    /// <item>Without (Release Waves' Export JSON): every wave; a product with several (non-overlapping) release windows keeps the
    /// one active at <paramref name="nowUtc"/>, else the next upcoming one, else the one that ended last.</item>
    /// </list>
    /// </summary>
    public static JObject Build(DlcWaveSchedule schedule, IEnumerable<string> knownProducts, ICollection<string> waveIds = null, DateTime? nowUtc = null)
    {
        var errors = Validate(schedule, knownProducts);
        if (waveIds != null) errors.AddRange(ValidateSelection(schedule, waveIds));
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
        var now = nowUtc ?? DateTime.UtcNow;
        var waves = schedule.waves.Where(w => waveIds == null || waveIds.Contains(w.id)).ToList();

        // Product -> the release wave whose window is applied.
        var chosen = new Dictionary<string, DlcWave>(StringComparer.Ordinal);
        foreach (var group in waves.Where(w => !w.promotion).SelectMany(w => w.productIds.Select(p => (p, w))).GroupBy(x => x.p, StringComparer.Ordinal))
        {
            var candidates = group.Select(x => x.w).Distinct().Select(w => (w, start: ToUtc(w.starts, schedule.timeZone),
                end: string.IsNullOrWhiteSpace(w.ends) ? (DateTime?)null : ToUtc(w.ends, schedule.timeZone))).ToList();
            var pick = candidates.Where(c => c.start <= now && (!c.end.HasValue || now < c.end.Value)).Select(c => c.w).FirstOrDefault()
                       ?? candidates.Where(c => c.start > now).OrderBy(c => c.start).Select(c => c.w).FirstOrDefault()
                       ?? candidates.OrderByDescending(c => c.end ?? DateTime.MaxValue).Select(c => c.w).First();
            chosen[group.Key] = pick;
        }

        return new JObject {
            ["schemaVersion"] = 1, ["scheduleId"] = schedule.scheduleId, ["timeZone"] = schedule.timeZone,
            ["exportOnly"] = true, ["unassignedProducts"] = "unchanged", ["existingOwnersRetainAccess"] = true,
            ["selectedWaves"] = waveIds == null ? (JToken)"all" : new JArray(waves.Select(w => w.id)),
            ["waves"] = new JArray(waves.OrderBy(w => ToUtc(w.starts, schedule.timeZone)).ThenBy(w => w.id, StringComparer.Ordinal).Select(w => new JObject {
                ["id"] = w.id, ["name"] = w.name, ["kind"] = w.promotion ? "promotion" : "release", ["paused"] = w.paused,
                ["startsAtUtc"] = ToUtc(w.starts, schedule.timeZone).ToString("O", CultureInfo.InvariantCulture),
                ["endsAtUtc"] = string.IsNullOrWhiteSpace(w.ends) ? JValue.CreateNull() : new JValue(ToUtc(w.ends, schedule.timeZone).ToString("O", CultureInfo.InvariantCulture)),
                ["productIds"] = new JArray(w.productIds.Where(id => w.promotion || chosen[id] == w).Distinct().OrderBy(id => id, StringComparer.Ordinal)) })) };
    }

    public static DlcWave Next(DlcWave wave, int days)
    {
        if (days < 1 || days > 366) throw new ArgumentOutOfRangeException(nameof(days));
        string Shift(string value) => string.IsNullOrWhiteSpace(value) ? "" : DateTime.ParseExact(value, DateFormat, CultureInfo.InvariantCulture).AddDays(days).ToString(DateFormat, CultureInfo.InvariantCulture);
        return new DlcWave { name = wave.name + " — next", starts = Shift(wave.starts), ends = Shift(wave.ends), promotion = wave.promotion };
    }
}
