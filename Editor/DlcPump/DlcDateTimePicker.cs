using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

/// <summary>
/// A small calendar popup for picking a local date and time ("yyyy-MM-dd HH:mm", the format release waves use).
/// Open it with <c>PopupWindow.Show(buttonRect, new DlcDateTimePicker(...))</c>.
/// </summary>
public sealed class DlcDateTimePicker : PopupWindowContent
{
    static readonly string[] Hours = BuildList(24, 1);
    static readonly string[] Minutes = BuildList(60, 5);
    static readonly string[] WeekDays = { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };

    readonly Action<string> onPick;
    readonly string caption;
    readonly bool allowClear;
    DateTime month;     // first day of the month on screen
    DateTime? day;      // the picked day, if any
    int hour, minute;

    /// <param name="value">The current value ("yyyy-MM-dd HH:mm" or blank).</param>
    /// <param name="caption">Shown under the title, e.g. the schedule's time zone.</param>
    /// <param name="allowClear">Show "Clear", which picks a blank value (e.g. "no end date").</param>
    /// <param name="defaultTime">Time used when the value is blank, as "HH:mm".</param>
    public DlcDateTimePicker(string value, string caption, bool allowClear, Action<string> onPick, string defaultTime = "18:00")
    {
        this.onPick = onPick;
        this.caption = caption;
        this.allowClear = allowClear;
        if (DateTime.TryParseExact((value ?? "").Trim(), DlcWaveExport.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            day = parsed.Date;
            hour = parsed.Hour;
            minute = parsed.Minute;
        }
        else if (DateTime.TryParseExact(defaultTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            hour = time.Hour;
            minute = time.Minute;
        }
        var shown = day ?? DateTime.Today;
        month = new DateTime(shown.Year, shown.Month, 1);
    }

    static string[] BuildList(int count, int step)
    {
        var list = new string[count / step];
        for (int i = 0; i < list.Length; i++) list[i] = (i * step).ToString("00", CultureInfo.InvariantCulture);
        return list;
    }

    public override Vector2 GetWindowSize() => new Vector2(260, 300);

    public override void OnGUI(Rect rect)
    {
        GUILayout.BeginArea(new Rect(rect.x + 6, rect.y + 4, rect.width - 12, rect.height - 8));

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("◀", EditorStyles.miniButtonLeft, GUILayout.Width(28))) month = month.AddMonths(-1);
            GUILayout.FlexibleSpace();
            GUILayout.Label(month.ToString("MMMM yyyy", CultureInfo.InvariantCulture), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("▶", EditorStyles.miniButtonRight, GUILayout.Width(28))) month = month.AddMonths(1);
        }
        if (!string.IsNullOrEmpty(caption)) GUILayout.Label(caption, EditorStyles.centeredGreyMiniLabel);

        float cell = (rect.width - 12) / 7f;
        using (new EditorGUILayout.HorizontalScope())
            foreach (string d in WeekDays) GUILayout.Label(d, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(cell - 3));

        // Six weeks starting on the Monday on or before the 1st.
        int offset = ((int)month.DayOfWeek + 6) % 7;
        var first = month.AddDays(-offset);
        var normal = new GUIStyle(EditorStyles.miniButton) { fixedHeight = 22 };
        var dim = new GUIStyle(normal);
        dim.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
        var today = new GUIStyle(normal) { fontStyle = FontStyle.Bold };
        for (int week = 0; week < 6; week++)
            using (new EditorGUILayout.HorizontalScope())
                for (int d = 0; d < 7; d++)
                {
                    var date = first.AddDays(week * 7 + d);
                    var old = GUI.backgroundColor;
                    if (day.HasValue && date == day.Value) GUI.backgroundColor = new Color(0.35f, 0.65f, 1f);
                    var style = date.Month != month.Month ? dim : date == DateTime.Today ? today : normal;
                    if (GUILayout.Button(date.Day.ToString(CultureInfo.InvariantCulture), style, GUILayout.Width(cell - 3)))
                    {
                        day = date;
                        if (date.Month != month.Month) month = new DateTime(date.Year, date.Month, 1);
                    }
                    GUI.backgroundColor = old;
                }

        GUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Time", GUILayout.Width(36));
            hour = EditorGUILayout.Popup(Mathf.Clamp(hour, 0, 23), Hours, GUILayout.Width(48));
            GUILayout.Label(":", GUILayout.Width(8));
            // Minutes in 5-minute steps; an exact minute from the text field is kept until changed.
            int index = minute % 5 == 0 ? minute / 5 : -1;
            var labels = index >= 0 ? Minutes : Prepend(Minutes, minute.ToString("00", CultureInfo.InvariantCulture));
            int picked = EditorGUILayout.Popup(index >= 0 ? index : 0, labels, GUILayout.Width(48));
            minute = index >= 0 ? picked * 5 : picked == 0 ? minute : (picked - 1) * 5;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("00:00", EditorStyles.miniButtonLeft)) { hour = 0; minute = 0; }
            if (GUILayout.Button("12:00", EditorStyles.miniButtonMid)) { hour = 12; minute = 0; }
            if (GUILayout.Button("18:00", EditorStyles.miniButtonRight)) { hour = 18; minute = 0; }
        }

        GUILayout.Label(day.HasValue ? Format() : "Pick a day", EditorStyles.centeredGreyMiniLabel);
        GUILayout.FlexibleSpace();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Today")) { day = DateTime.Today; month = new DateTime(day.Value.Year, day.Value.Month, 1); }
            if (allowClear && GUILayout.Button("Clear")) { onPick?.Invoke(""); editorWindow.Close(); }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel")) editorWindow.Close();
            using (new EditorGUI.DisabledScope(!day.HasValue))
                if (GUILayout.Button("OK", GUILayout.Width(50))) { onPick?.Invoke(Format()); editorWindow.Close(); }
        }
        GUILayout.EndArea();
    }

    string Format() => day.Value.AddHours(hour).AddMinutes(minute).ToString(DlcWaveExport.DateFormat, CultureInfo.InvariantCulture);

    static string[] Prepend(string[] list, string first)
    {
        var copy = new string[list.Length + 1];
        copy[0] = first;
        Array.Copy(list, 0, copy, 1, list.Length);
        return copy;
    }
}
