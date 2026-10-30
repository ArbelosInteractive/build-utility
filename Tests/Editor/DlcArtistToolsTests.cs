using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class DlcArtistToolsTests
{
    static DlcWaveSchedule Schedule()
    {
        var s = ScriptableObject.CreateInstance<DlcWaveSchedule>();
        s.waves.Add(new DlcWave { name = "Arena week", starts = "2026-11-06 18:00", productIds = new System.Collections.Generic.List<string> { "arena" } }); return s;
    }
    [Test] public void WaveExportIsDeterministicAndOpenEnded()
    {
        var s = Schedule(); try { var json = DlcWaveExport.Build(s, new[] { "arena" }); Assert.AreEqual(json.ToString(), DlcWaveExport.Build(s, new[] { "arena" }).ToString()); Assert.AreEqual(JTokenType.Null, json["waves"][0]["endsAtUtc"].Type); Assert.IsTrue((bool)json["existingOwnersRetainAccess"]); Assert.IsTrue((bool)json["exportOnly"]); } finally { UnityEngine.Object.DestroyImmediate(s); }
    }
    [Test] public void UnknownAndDuplicateProductsBlockExport()
    {
        var s = Schedule(); try { s.waves[0].productIds.Add("arena"); Assert.Throws<InvalidOperationException>(() => DlcWaveExport.Build(s, new[] { "arena" })); s.waves[0].productIds.Clear(); s.waves[0].productIds.Add("missing"); Assert.Throws<InvalidOperationException>(() => DlcWaveExport.Build(s, new[] { "arena" })); } finally { UnityEngine.Object.DestroyImmediate(s); }
    }
    [Test] public void RepeatedReleaseWindowsBlockedButPromotionsAllowed()
    {
        var s = Schedule(); try { var next = DlcWaveExport.Next(s.waves[0], 7); next.productIds.Add("arena"); s.waves.Add(next); Assert.IsNotEmpty(DlcWaveExport.Validate(s, new[] { "arena" })); next.promotion = true; Assert.IsEmpty(DlcWaveExport.Validate(s, new[] { "arena" })); } finally { UnityEngine.Object.DestroyImmediate(s); }
    }
    [Test] public void DateBoundaryAndPauseStates()
    {
        var s = Schedule(); try { var w = s.waves[0]; w.ends = "2026-11-07 18:00"; var start = DlcWaveExport.ToUtc(w.starts, "UTC"); Assert.AreEqual("Scheduled", DlcWaveExport.State(w, "UTC", start.AddSeconds(-1))); Assert.AreEqual("Active", DlcWaveExport.State(w, "UTC", start)); Assert.AreEqual("Ended", DlcWaveExport.State(w, "UTC", start.AddDays(1))); w.paused = true; Assert.AreEqual("Paused", DlcWaveExport.State(w, "UTC", start)); } finally { UnityEngine.Object.DestroyImmediate(s); }
    }
    [Test] public void InvalidDatesAndReverseWindowRejected()
    {
        var s = Schedule(); try { s.waves[0].ends = "2026-11-05 18:00"; Assert.IsNotEmpty(DlcWaveExport.Validate(s, new[] { "arena" })); Assert.Throws<InvalidOperationException>(() => DlcWaveExport.ToUtc("not a date", "UTC")); } finally { UnityEngine.Object.DestroyImmediate(s); }
    }
    [Test] public void DaylightSavingAmbiguityIsRejected()
    {
        string zone = TimeZoneInfo.GetSystemTimeZones().Any(z => z.Id == "Eastern Standard Time") ? "Eastern Standard Time" : "America/New_York";
        Assert.Throws<InvalidOperationException>(() => DlcWaveExport.ToUtc("2026-11-01 01:30", zone));
        Assert.Throws<InvalidOperationException>(() => DlcWaveExport.ToUtc("2026-03-08 02:30", zone));
        Assert.AreEqual(18, DlcWaveExport.ToUtc("2026-11-06 13:00", zone).Hour);
    }
    [Test] public void CadenceCopiesDatesWithFreshIdAndNoProducts()
    {
        var w = new DlcWave { starts = "2026-11-06 18:00", ends = "2026-11-07 18:00" }; w.productIds.Add("arena"); var next = DlcWaveExport.Next(w, 7); Assert.AreNotEqual(w.id, next.id); Assert.AreEqual("2026-11-13 18:00", next.starts); Assert.IsEmpty(next.productIds); Assert.AreEqual(1, w.productIds.Count);
    }
    [Test] public void FillStopsAtBoundaryAndSameColorIsSafe()
    {
        var p = new Color32[32 * 32]; for (int y = 0; y < 32; y++) DlcIconPixels.Set(p, 32, 15, y, Color.black); DlcIconPixels.Fill(p, 32, 0, 0, Color.red); DlcIconPixels.Fill(p, 32, 0, 0, Color.red); Assert.AreEqual((Color32)Color.red, p[0]); Assert.AreEqual((Color32)Color.clear, p[31]); Assert.AreEqual((Color32)Color.black, p[15]);
    }
    [Test] public void MirrorAndLineIncludeEndpoints()
    {
        var p = new Color32[32 * 32]; DlcIconPixels.Line(p, 32, new Vector2Int(0, 0), new Vector2Int(4, 4), Color.red, true); Assert.AreEqual((Color32)Color.red, p[0]); Assert.AreEqual((Color32)Color.red, p[31]); Assert.AreEqual((Color32)Color.red, p[4 * 32 + 4]);
    }
    [Test] public void MoveUsesSnapshotAndClipsAtEdge()
    {
        var p = new Color32[32 * 32]; p[0] = Color.red; p[1] = Color.blue; DlcIconPixels.Move(p, 32, new RectInt(0, 0, 2, 1), new Vector2Int(1, 0)); Assert.AreEqual((Color32)Color.clear, p[0]); Assert.AreEqual((Color32)Color.red, p[1]); Assert.AreEqual((Color32)Color.blue, p[2]); DlcIconPixels.Move(p, 32, new RectInt(1, 0, 2, 1), new Vector2Int(40, 0)); Assert.AreEqual((Color32)Color.clear, p[1]);
    }
    [Test] public void TemplatesAreIndependentAndNonempty()
    {
        foreach (string name in DlcIconPixels.Templates) { var a = DlcIconPixels.Template(name, 64, Color.white); var b = DlcIconPixels.Template(name, 64, Color.white); Assert.IsTrue(a.Any(p => p.a > 0)); a[0] = Color.red; Assert.AreNotEqual(a[0], b[0]); }
    }
    [Test] public void LayersCompositeAlphaAndVisibility()
    {
        var d = ScriptableObject.CreateInstance<DlcIconDocument>(); try { d.Initialize(32); d.layers[0].pixels[0] = Color.blue; d.layers.Add(new DlcIconLayer("Top", 32)); d.layers[1].pixels[0] = new Color32(255, 0, 0, 128); var p = DlcIconPixels.Composite(d)[0]; Assert.That(p.r, Is.InRange(127, 129)); Assert.That(p.b, Is.InRange(126, 128)); Assert.AreEqual(255, p.a); d.layers[1].visible = false; Assert.AreEqual((Color32)Color.blue, DlcIconPixels.Composite(d)[0]); } finally { UnityEngine.Object.DestroyImmediate(d); }
    }
    [Test] public void PngPreservesDimensionsAndTransparentPixels()
    {
        var d = ScriptableObject.CreateInstance<DlcIconDocument>(); Texture2D source = null, decoded = null;
        try { d.Initialize(64); d.layers[0].pixels[0] = Color.red; source = DlcIconPixels.Texture(d); decoded = new Texture2D(2, 2); Assert.IsTrue(ImageConversion.LoadImage(decoded, ImageConversion.EncodeToPNG(source))); Assert.AreEqual(64, decoded.width); Assert.AreEqual((Color32)Color.red, (Color32)decoded.GetPixel(0, 0)); Assert.AreEqual(0, ((Color32)decoded.GetPixel(1, 1)).a); Assert.AreEqual(FilterMode.Point, source.filterMode); } finally { if (source) UnityEngine.Object.DestroyImmediate(source); if (decoded) UnityEngine.Object.DestroyImmediate(decoded); UnityEngine.Object.DestroyImmediate(d); }
    }
    [Test] public void DraftSerializationAndUndoRedoKeepPixelsAndProduct()
    {
        var d = ScriptableObject.CreateInstance<DlcIconDocument>(); var restored = ScriptableObject.CreateInstance<DlcIconDocument>();
        try { d.Initialize(32); d.productId = "arena"; Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.RegisterCompleteObjectUndo(d, "Paint test"); d.layers[0].pixels[0] = Color.red; Undo.FlushUndoRecordObjects(); Undo.IncrementCurrentGroup(); Undo.PerformUndo(); Assert.AreEqual(0, d.layers[0].pixels[0].a); Undo.PerformRedo(); Assert.AreEqual((Color32)Color.red, d.layers[0].pixels[0]); EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(d), restored); Assert.AreEqual("arena", restored.productId); Assert.AreEqual(d.layers[0].pixels[0], restored.layers[0].pixels[0]); Undo.ClearUndo(d); } finally { UnityEngine.Object.DestroyImmediate(d); UnityEngine.Object.DestroyImmediate(restored); }
    }
}
