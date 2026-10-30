using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class DlcArtistWindowTests
{
    [Test] public void PlayFabWindowRequiresExplicitReviewAndPublish()
    {
        var window = ScriptableObject.CreateInstance<DlcPlayFabWindow>();
        try
        {
            window.CreateGUI();
            Assert.IsTrue(window.rootVisualElement.Query<Button>().ToList().Any(b => b.text == "Read & review PlayFab changes"));
            Assert.IsTrue(window.rootVisualElement.Query<Button>().ToList().Any(b => b.text == "Publish reviewed changes…"));
            Assert.IsTrue(window.rootVisualElement.Query<TextField>().ToList().Single(f => f.label == "Reviewed changes").isReadOnly);
        }
        finally { UnityEngine.Object.DestroyImmediate(window); }
    }
    sealed class Adapter : IDlcProjectAdapter
    {
        public string CatalogPath { get; set; }
        public string DisplayName => "Test";
        public string BackupDirectory => Path.GetTempPath();
        public UnityEngine.Object UnlockableCatalog => null;
        public List<DlcReleaseContent> Resolve(string id, List<string> diagnostics) => new List<DlcReleaseContent>();
        public string Populate(DlcRelease release) => "";
    }
    [Test] public void WaveWindowRendersAssignedProductsAndDateControls()
    {
        var previous = DlcProject.Adapter;
        var path = Path.Combine(Path.GetTempPath(), "dlc-ui-" + Guid.NewGuid().ToString("N") + ".json");
        DlcArtistWindow window = null; DlcWaveSchedule schedule = null;
        try
        {
            File.WriteAllText(path, "{\"products\":[{\"id\":\"arena\",\"type\":1,\"defaultDescription\":{\"title\":\"Arena\"}}]}");
            DlcProject.Adapter = new Adapter { CatalogPath = path };
            schedule = ScriptableObject.CreateInstance<DlcWaveSchedule>(); schedule.waves.Add(new DlcWave { starts = "2026-11-06 18:00" }); schedule.waves[0].productIds.Add("arena");
            window = ScriptableObject.CreateInstance<DlcArtistWindow>();
            typeof(DlcArtistWindow).GetField("schedule", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, schedule);
            window.CreateGUI();
            Assert.IsTrue(window.rootVisualElement.Query<TextField>().ToList().Any(f => f.label.StartsWith("Starts")));
            Assert.IsTrue(window.rootVisualElement.Query<Label>().ToList().Any(l => l.text == "arena"));
            Assert.IsTrue(window.rootVisualElement.Query<Button>().ToList().Any(b => b.text == "Export JSON"));
        }
        finally { if (window) UnityEngine.Object.DestroyImmediate(window); if (schedule) UnityEngine.Object.DestroyImmediate(schedule); DlcProject.Adapter = previous; File.Delete(path); }
    }
    [Test] public void IconWindowRendersLayersToolsAndAssignment()
    {
        DlcIconStudioWindow window = null; var doc = ScriptableObject.CreateInstance<DlcIconDocument>();
        try
        {
            doc.Initialize(32); window = ScriptableObject.CreateInstance<DlcIconStudioWindow>();
            typeof(DlcIconStudioWindow).GetField("document", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, doc);
            window.CreateGUI();
            Assert.IsNotNull(window.rootVisualElement.Q<IMGUIContainer>());
            Assert.IsTrue(window.rootVisualElement.Query<Button>().ToList().Any(b => b.text == "Assign to product"));
            Assert.IsTrue(window.rootVisualElement.Query<Button>().ToList().Any(b => b.text == "Arena"));
        }
        finally { if (window) UnityEngine.Object.DestroyImmediate(window); UnityEngine.Object.DestroyImmediate(doc); }
    }
}
