using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class DlcArtistWindow : EditorWindow
{
    [SerializeField] DlcWaveSchedule schedule;
    [SerializeField] int selectedWave;
    [SerializeField] string search = "";
    [SerializeField] string previewDate = "";
    readonly HashSet<string> selected = new HashSet<string>(StringComparer.Ordinal);
    JObject catalog;
    Label status;
    VisualElement body;
    const string DragKey = "DlcPump.ProductIds";

    [MenuItem("Build Utility/DLC Artist Workspace")]
    public static void Open() => GetWindow<DlcArtistWindow>("DLC Release Waves");
    void OnEnable() { minSize = new Vector2(700, 440); Undo.undoRedoPerformed += Rebuild; }
    void OnDisable() { Undo.undoRedoPerformed -= Rebuild; }
    public void CreateGUI()
    {
        var root = rootVisualElement; root.Clear();
        var toolbar = new Toolbar(); root.Add(toolbar);
        toolbar.Add(new ToolbarButton(() => DlcManagerWindow.Open()) { text = "DLC Pump" });
        toolbar.Add(new Label("Release Waves"));
        toolbar.Add(new ToolbarButton(DlcIconStudioWindow.Open) { text = "Icon Studio" });
        toolbar.Add(new ToolbarButton(() => EditorUtility.DisplayDialog("Release Waves — how to use", "1. Create a schedule outside Resources.\n2. Add a wave and enter dates (YYYY-MM-DD HH:mm). Choose your system time zone.\n3. Drag products from the shelf, select and Add selected, or drop a DLC Release asset.\n4. Use the preview date to inspect active/scheduled/ended states.\n5. Validate, save drafts, and Export JSON.\n\nThe JSON is a DLC Pump schedule, not a PlayFab API request. This phase does not change live availability. Unassigned products and existing ownership remain unchanged.", "OK")) { text = "Help" });
        toolbar.Add(new ToolbarButton(() => { AssetDatabase.SaveAssets(); SetStatus("Drafts saved. No remote services changed."); }) { text = "Save drafts" });
        root.Add(new HelpBox("Local release planning and JSON export. These drafts do not change PlayFab, Apple, Google, or existing ownership. Store readiness is not checked.", HelpBoxMessageType.Info));
        var row = Row(); root.Add(row);
        var asset = new ObjectField("Schedule") { objectType = typeof(DlcWaveSchedule), value = schedule, allowSceneObjects = false }; asset.style.flexGrow = 1;
        asset.RegisterValueChangedCallback(e => { schedule = e.newValue as DlcWaveSchedule; selectedWave = 0; Rebuild(); }); row.Add(asset);
        row.Add(Button("New schedule", () => { var path = DraftPath("Wave schedule"); if (path == null) return; schedule = CreateInstance<DlcWaveSchedule>(); AssetDatabase.CreateAsset(schedule, path); Undo.RegisterCreatedObjectUndo(schedule, "Create wave schedule"); CreateGUI(); }));
        row.Add(Button("Refresh catalog", Rebuild));
        body = new VisualElement(); body.style.flexGrow = 1; root.Add(body);
        status = new Label(); status.style.whiteSpace = WhiteSpace.Normal; status.style.paddingLeft = 8; root.Add(status);
        Rebuild();
    }
    internal static Button Button(string text, Action action) => new Button(action) { text = text };
    internal static VisualElement Row() { var r = new VisualElement(); r.style.flexDirection = FlexDirection.Row; r.style.flexWrap = Wrap.Wrap; return r; }
    internal static string DraftPath(string name)
    {
        var path = EditorUtility.SaveFilePanelInProject("Save " + name, "DLC " + name, "asset", "Save editable drafts outside Resources.");
        if (string.IsNullOrEmpty(path)) return null;
        if (path.Split('/').Contains("Resources")) { EditorUtility.DisplayDialog("Editor draft", "Save outside Resources so editor documents are not included in the game.", "OK"); return null; }
        return path;
    }
    void SetStatus(string value) { if (status != null) status.text = value; }
    IEnumerable<JToken> Products => (catalog?["products"] as JArray ?? new JArray()).Where(p => (int?)p["type"] == 1);
    void Change(string name, Action action, bool rebuild = true)
    {
        Undo.RecordObject(schedule, name); action(); EditorUtility.SetDirty(schedule); if (rebuild) Rebuild();
    }
    void Rebuild()
    {
        if (body == null) return;
        body.Clear();
        try { catalog = DlcReleaseService.Catalog(); }
        catch (Exception e) { body.Add(new HelpBox(e.Message, HelpBoxMessageType.Error)); return; }
        if (!schedule) { body.Add(new HelpBox("Create or choose a schedule, then drag catalog products into a wave.", HelpBoxMessageType.Info)); return; }
        var settings = Row(); body.Add(settings);
        var zone = new TextField("Time zone ID") { value = schedule.timeZone, isDelayed = true }; zone.tooltip = "Use an installed system time zone ID, e.g. UTC. Choose from the picker below.";
        zone.RegisterValueChangedCallback(e => Change("Change wave time zone", () => schedule.timeZone = e.newValue)); settings.Add(zone);
        settings.Add(Button("Choose zone", () => { var menu = new GenericMenu(); foreach (var tz in TimeZoneInfo.GetSystemTimeZones()) { string id = tz.Id; menu.AddItem(new GUIContent(tz.DisplayName), id == schedule.timeZone, () => Change("Change time zone", () => schedule.timeZone = id)); } menu.ShowAsContext(); }));
        settings.Add(Button("New wave", () => Change("Add wave", () => { schedule.waves.Add(new DlcWave()); selectedWave = schedule.waves.Count - 1; })));
        settings.Add(Button("Validate", () => ShowValidation()));
        settings.Add(Button("Export JSON", Export));
        var columns = new TwoPaneSplitView(0, 240, TwoPaneSplitViewOrientation.Horizontal); columns.style.flexGrow = 1; body.Add(columns);
        var shelf = new ScrollView(); shelf.style.minWidth = 170; columns.Add(shelf);
        shelf.Add(new Label("PRODUCT SHELF · drag or select"));
        var filter = new TextField("Search") { value = search, isDelayed = true }; filter.RegisterValueChangedCallback(e => { search = e.newValue; Rebuild(); }); shelf.Add(filter);
        shelf.Add(Button("Add selected to wave", () => AddProducts(selected)));
        foreach (var p in Products.Where(p => ((string)p["id"] + " " + (string)p["defaultDescription"]?["title"]).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            string id = (string)p["id"], title = (string)p["defaultDescription"]?["title"] ?? id;
            var row = Row(); shelf.Add(row);
            var toggle = new Toggle { value = selected.Contains(id) }; toggle.RegisterValueChangedCallback(e => { if (e.newValue) selected.Add(id); else selected.Remove(id); }); row.Add(toggle);
            var iconPath = (DlcProject.Adapter as IDlcIconAuthoring)?.IconAssetPath(id);
            if (!string.IsNullOrEmpty(iconPath)) { var image = new Image { image = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath), scaleMode = ScaleMode.ScaleToFit }; image.style.width = 32; image.style.height = 32; row.Add(image); }
            var label = new Label(title); label.tooltip = id; label.style.flexGrow = 1; label.style.whiteSpace = WhiteSpace.Normal; row.Add(label);
            row.RegisterCallback<MouseMoveEvent>(e => { if ((e.pressedButtons & 1) == 0) return; DragAndDrop.PrepareStartDrag(); DragAndDrop.SetGenericData(DragKey, selected.Contains(id) ? selected.ToArray() : new[] { id }); DragAndDrop.StartDrag("DLC products"); e.StopPropagation(); });
            row.Add(Button("+", () => AddProducts(new[] { id })));
            row.Add(Button("Icon", () => DlcIconStudioWindow.ForProduct(id)));
        }
        var main = new ScrollView(); columns.Add(main);
        if (schedule.waves.Count == 0) { main.Add(new HelpBox("Add your first wave. Unassigned products keep their existing behavior.", HelpBoxMessageType.Info)); return; }
        selectedWave = Mathf.Clamp(selectedWave, 0, schedule.waves.Count - 1);
        for (int i = 0; i < schedule.waves.Count; i++)
        {
            int index = i; var w = schedule.waves[i];
            if (w == null) continue;
            string state; try { state = DlcWaveExport.State(w, schedule.timeZone, PreviewUtc()); } catch { state = "Needs dates"; }
            main.Add(Button((i == selectedWave ? "▶ " : "") + w.name + " · " + state + " · " + w.productIds.Count + " products", () => { selectedWave = index; Rebuild(); }));
        }
        var wave = schedule.waves[selectedWave];
        Text(main, "Wave name", wave.name, v => wave.name = v);
        Text(main, "Starts (YYYY-MM-DD HH:mm)", wave.starts, v => wave.starts = v);
        Text(main, "Ends (blank = stays available)", wave.ends, v => wave.ends = v);
        var promotion = new Toggle("Feature existing products only") { value = wave.promotion }; promotion.RegisterValueChangedCallback(e => Change("Change wave purpose", () => wave.promotion = e.newValue)); main.Add(promotion);
        var paused = new Toggle("Paused in exported plan") { value = wave.paused }; paused.RegisterValueChangedCallback(e => Change("Pause wave", () => wave.paused = e.newValue)); main.Add(paused);
        var drop = new VisualElement(); drop.style.minHeight = 70; drop.style.paddingTop = 10; drop.style.paddingBottom = 10; drop.Add(new Label("DROP PRODUCTS HERE")); main.Add(drop);
        drop.RegisterCallback<DragUpdatedEvent>(e => { DragAndDrop.visualMode = DragAndDrop.GetGenericData(DragKey) is string[] || DragAndDrop.objectReferences.OfType<DlcRelease>().Any() ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected; e.StopPropagation(); });
        drop.RegisterCallback<DragPerformEvent>(e => { var ids = DragAndDrop.GetGenericData(DragKey) as string[] ?? DragAndDrop.objectReferences.OfType<DlcRelease>().SelectMany(r => r.productIds).ToArray(); DragAndDrop.AcceptDrag(); AddProducts(ids); DragAndDrop.SetGenericData(DragKey, null); e.StopPropagation(); });
        foreach (string id in wave.productIds.ToArray()) { var row = Row(); drop.Add(row); row.Add(new Label(id)); row.Add(Button("Remove", () => Change("Remove product", () => wave.productIds.Remove(id)))); }
        var actions = Row(); main.Add(actions);
        actions.Add(Button("Next week (empty wave)", () => Next(7))); actions.Add(Button("Next fortnight", () => Next(14)));
        actions.Add(Button("Delete wave", () => Change("Delete wave", () => schedule.waves.RemoveAt(selectedWave))));
        var preview = new TextField("Preview local date (blank = now)") { value = previewDate, isDelayed = true }; preview.RegisterValueChangedCallback(e => { previewDate = e.newValue; Rebuild(); }); main.Add(preview);
        main.Add(new HelpBox("Preview is local only. One release window per product; repeat promotion waves are allowed. Existing owners retain access. Project drops accept DLC Release assets, not arbitrary artwork.", HelpBoxMessageType.Info));
    }
    DateTime PreviewUtc() => string.IsNullOrWhiteSpace(previewDate) ? DateTime.UtcNow : DlcWaveExport.ToUtc(previewDate, schedule.timeZone);
    void Text(VisualElement parent, string label, string value, Action<string> setter)
    {
        var field = new TextField(label) { value = value, isDelayed = true }; field.RegisterValueChangedCallback(e => Change(label, () => setter(e.newValue))); parent.Add(field);
    }
    void AddProducts(IEnumerable<string> ids)
    {
        if (!schedule || schedule.waves.Count == 0) { SetStatus("Create a wave first."); return; }
        var known = new HashSet<string>(Products.Select(p => (string)p["id"]));
        var incoming = ids.Distinct().ToArray(); if (incoming.Any(id => !known.Contains(id))) { SetStatus("Drop contains unknown or unsupported products."); return; }
        Change("Add wave products", () => { var wave = schedule.waves[Mathf.Clamp(selectedWave, 0, schedule.waves.Count - 1)]; foreach (string id in incoming) if (!wave.productIds.Contains(id)) wave.productIds.Add(id); });
    }
    void Next(int days) { try { var next = DlcWaveExport.Next(schedule.waves[selectedWave], days); Change("Add cadence wave", () => { schedule.waves.Add(next); selectedWave = schedule.waves.Count - 1; }); } catch (Exception e) { SetStatus(e.Message); } }
    void ShowValidation() { var errors = DlcWaveExport.Validate(schedule, Products.Select(p => (string)p["id"])); SetStatus(errors.Count == 0 ? "Valid local plan. Remote state has not been checked." : string.Join("\n", errors)); }
    void Export()
    {
        try
        {
            catalog = DlcReleaseService.Catalog();
            var json = DlcWaveExport.Build(schedule, Products.Select(p => (string)p["id"]));
            string path = EditorUtility.SaveFilePanel("Export DLC Pump schedule", "", "release-waves.json", "json"); if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, json.ToString()); SetStatus("Exported " + path + ". Schedule document only; no server or store changes.");
        }
        catch (Exception e) { SetStatus(e.Message); }
    }
}
