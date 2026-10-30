using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class DlcIconStudioWindow : EditorWindow
{
    [SerializeField] DlcIconDocument document;
    [SerializeField] string product = "";
    [SerializeField] int layer;
    [SerializeField] int newSize = 64;
    [SerializeField] Color color = new Color(0.9f, 0.45f, 0.2f, 1);
    [SerializeField] string tool = "Pencil";
    [SerializeField] bool mirror, grid = true;
    [SerializeField] float zoom = 6;
    Texture2D preview;
    Label status;
    ColorField colorField;
    Vector2Int anchor, last;
    RectInt selection;
    bool stroke;
    int undoGroup;
    static readonly string[] Tools = { "Pencil", "Eraser", "Fill", "Eyedropper", "Line", "Rectangle", "Select", "Move" };
    [MenuItem("Build Utility/DLC Icon Studio")]
    public static void Open() => GetWindow<DlcIconStudioWindow>("DLC Icon Studio");
    public static void ForProduct(string id)
    {
        var w = GetWindow<DlcIconStudioWindow>("DLC Icon Studio"); w.product = id;
        w.document = AssetDatabase.FindAssets("t:DlcIconDocument").Select(g => AssetDatabase.LoadAssetAtPath<DlcIconDocument>(AssetDatabase.GUIDToAssetPath(g))).FirstOrDefault(d => d && d.productId == id);
        w.layer = 0; w.selection = new RectInt(); w.RefreshTexture(); w.CreateGUI();
    }
    void OnEnable() { minSize = new Vector2(720, 460); Undo.undoRedoPerformed += Reload; }
    void OnDisable() { EndStroke(); Undo.undoRedoPerformed -= Reload; if (preview) DestroyImmediate(preview); }
    void OnLostFocus() { EndStroke(); }
    void Reload() { EndStroke(); RefreshTexture(); CreateGUI(); }
    void Say(string text) { if (status != null) status.text = text; }
    void Edit(string name, Action edit, bool rebuild = true)
    {
        if (!document) return;
        Undo.RegisterCompleteObjectUndo(document, name); edit(); EditorUtility.SetDirty(document); RefreshTexture(); if (rebuild) CreateGUI();
    }
    void RefreshTexture()
    {
        if (preview) DestroyImmediate(preview);
        preview = document && document.layers.Count > 0 ? DlcIconPixels.Texture(document) : null; Repaint();
    }
    public void CreateGUI()
    {
        EndStroke();
        var root = rootVisualElement; root.Clear();
        var toolbar = new Toolbar(); root.Add(toolbar);
        toolbar.Add(new ToolbarButton(DlcArtistWindow.Open) { text = "Release Waves" });
        toolbar.Add(new Label("Icon Studio"));
        toolbar.Add(new ToolbarButton(() => EditorUtility.DisplayDialog("Icon Studio — how to use", "1. Open a product's Icon button in Release Waves, or create a document and set its product ID.\n2. Choose a canvas size before New. Use 64 for pixel art or 256/512 for a headshot.\n3. Add a template layer or import artwork. Enable Smooth headshot before importing photographic art.\n4. Select a layer, paint, and preview at 64/128 pixels. Select then Move relocates pixels; Undo/Redo is supported.\n5. Save draft preserves layers. Export PNG saves a standalone image. Assign to product writes the game's icon and keeps a backup when replacing an existing PNG.\n\nAssign updates the local fallback. Use Review PlayFab upload to review and publish the thumbnail separately. The updated game downloads remote artwork with a local fallback.", "OK")) { text = "Help" });
        toolbar.Add(new ToolbarButton(() => { AssetDatabase.SaveAssets(); Say("Editable draft saved."); }) { text = "Save draft" });
        toolbar.Add(new ToolbarButton(Undo.PerformUndo) { text = "Undo" }); toolbar.Add(new ToolbarButton(Undo.PerformRedo) { text = "Redo" });
        root.Add(new HelpBox("Paint icons, assign a local fallback, or review a PlayFab thumbnail upload. Larger detail-image rendering is separate; existing PlayFab Screenshot artwork is preserved.", HelpBoxMessageType.Info));
        var top = DlcArtistWindow.Row(); root.Add(top);
        var asset = new ObjectField("Icon document") { objectType = typeof(DlcIconDocument), allowSceneObjects = false, value = document }; asset.style.flexGrow = 1;
        asset.RegisterValueChangedCallback(e => { document = e.newValue as DlcIconDocument; layer = 0; selection = new RectInt(); RefreshTexture(); CreateGUI(); }); top.Add(asset);
        var sizes = new PopupField<int>(new System.Collections.Generic.List<int> { 32, 64, 128, 256, 512 }, newSize); sizes.tooltip = "New canvas size. Use 256/512 for smooth imported headshots."; sizes.RegisterValueChangedCallback(e => newSize = e.newValue); top.Add(sizes);
        top.Add(DlcArtistWindow.Button("New", NewDocument));
        status = new Label(); status.style.whiteSpace = WhiteSpace.Normal;
        if (!document) { root.Add(new Label("Choose a product, then create an editable icon document or select one.")); root.Add(status); return; }
        if (document.layers.Count == 0) { root.Add(DlcArtistWindow.Button("Initialize canvas", () => Edit("Initialize icon", () => document.Initialize(newSize)))); root.Add(status); return; }
        layer = Mathf.Clamp(layer, 0, document.layers.Count - 1);
        var productRow = DlcArtistWindow.Row(); root.Add(productRow);
        var productField = new TextField("Product ID") { value = document.productId, isDelayed = true }; productField.style.flexGrow = 1; productField.RegisterValueChangedCallback(e => Edit("Assign product ID", () => document.productId = e.newValue)); productRow.Add(productField);
        productRow.Add(DlcArtistWindow.Button("Use selected product", () => { if (!string.IsNullOrEmpty(product)) Edit("Assign product ID", () => document.productId = product); }));
        var split = new TwoPaneSplitView(0, 210, TwoPaneSplitViewOrientation.Horizontal); split.style.flexGrow = 1; root.Add(split);
        var library = new ScrollView(); split.Add(library);
        library.Add(new Label("DEFAULT LIBRARY · adds a layer"));
        foreach (var template in DlcIconPixels.Templates)
        {
            string name = template;
            library.Add(DlcArtistWindow.Button(name, () => Edit("Add template", () => { document.layers.Add(new DlcIconLayer(name, document.size) { pixels = DlcIconPixels.Template(name, document.size, color) }); layer = document.layers.Count - 1; })));
        }
        library.Add(DlcArtistWindow.Button("Import PNG/JPEG…", Import));
        library.Add(new Label("LAYERS · bottom to top"));
        for (int i = 0; i < document.layers.Count; i++)
        {
            int index = i; var row = DlcArtistWindow.Row(); library.Add(row);
            var visible = new Toggle { value = document.layers[i].visible }; visible.RegisterValueChangedCallback(e => Edit("Layer visibility", () => document.layers[index].visible = e.newValue)); row.Add(visible);
            row.Add(DlcArtistWindow.Button((index == layer ? "▶ " : "") + document.layers[i].name, () => { layer = index; selection = new RectInt(); CreateGUI(); }));
        }
        library.Add(DlcArtistWindow.Button("Add empty layer", () => Edit("Add layer", () => { document.layers.Add(new DlcIconLayer("Layer " + (document.layers.Count + 1), document.size)); layer = document.layers.Count - 1; })));
        var rename = new TextField("Layer name") { value = document.layers[layer].name, isDelayed = true }; rename.RegisterValueChangedCallback(e => Edit("Rename layer", () => document.layers[layer].name = e.newValue)); library.Add(rename);
        library.Add(DlcArtistWindow.Button("Move layer up", () => Swap(1))); library.Add(DlcArtistWindow.Button("Move layer down", () => Swap(-1)));
        library.Add(DlcArtistWindow.Button("Delete layer", () => { if (document.layers.Count > 1) Edit("Delete layer", () => { document.layers.RemoveAt(layer); layer = Math.Max(0, layer - 1); }); }));
        var pane = new ScrollView(ScrollViewMode.VerticalAndHorizontal); split.Add(pane);
        var controls = DlcArtistWindow.Row(); pane.Add(controls);
        var tools = new PopupField<string>(Tools.ToList(), tool); tools.RegisterValueChangedCallback(e => { EndStroke(); tool = e.newValue; }); controls.Add(tools);
        colorField = new ColorField("Paint") { value = color }; colorField.RegisterValueChangedCallback(e => color = e.newValue); controls.Add(colorField);
        var mirroring = new Toggle("Mirror") { value = mirror }; mirroring.RegisterValueChangedCallback(e => mirror = e.newValue); controls.Add(mirroring);
        var showGrid = new Toggle("Grid") { value = grid }; showGrid.RegisterValueChangedCallback(e => { grid = e.newValue; Repaint(); }); controls.Add(showGrid);
        var smooth = new Toggle("Smooth headshot") { value = !document.pixelArt }; smooth.RegisterValueChangedCallback(e => Edit("Sampling mode", () => document.pixelArt = !e.newValue)); controls.Add(smooth);
        var zoomer = new Slider("Zoom", 1, 12) { value = zoom }; zoomer.RegisterValueChangedCallback(e => { zoom = e.newValue; Repaint(); }); pane.Add(zoomer);
        var palette = DlcArtistWindow.Row(); pane.Add(palette);
        foreach (var c in new[] { Color.white, Color.black, Color.red, Color.yellow, Color.green, Color.cyan, Color.blue, Color.magenta }) { Color chosen = c; var swatch = DlcArtistWindow.Button("■", () => { color = chosen; colorField.SetValueWithoutNotify(color); }); swatch.style.color = chosen; palette.Add(swatch); }
        pane.Add(new IMGUIContainer(DrawCanvas));
        var actions = DlcArtistWindow.Row(); root.Add(actions);
        actions.Add(DlcArtistWindow.Button("Export PNG…", Export)); actions.Add(DlcArtistWindow.Button("Assign to product", Assign));
        actions.Add(DlcArtistWindow.Button("Review PlayFab upload…", () => DlcPlayFabWindow.ForIcon(document)));
        root.Add(status);
        if (!preview) RefreshTexture();
    }
    void NewDocument()
    {
        string path = DlcArtistWindow.DraftPath("Icon"); if (path == null) return;
        document = CreateInstance<DlcIconDocument>(); document.Initialize(newSize); document.productId = product;
        AssetDatabase.CreateAsset(document, path); Undo.RegisterCreatedObjectUndo(document, "Create icon document"); layer = 0; selection = new RectInt(); RefreshTexture(); CreateGUI();
    }
    void Swap(int delta)
    {
        int other = layer + delta; if (other < 0 || other >= document.layers.Count) return;
        Edit("Reorder layer", () => { var temp = document.layers[layer]; document.layers[layer] = document.layers[other]; document.layers[other] = temp; layer = other; });
    }
    void Import()
    {
        var path = EditorUtility.OpenFilePanel("Import headshot or artwork (centered fit)", "", "png,jpg,jpeg"); if (string.IsNullOrEmpty(path)) return;
        Texture2D input = null;
        try
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidOperationException("Choose an image smaller than 32 MB.");
            input = new Texture2D(2, 2); if (!ImageConversion.LoadImage(input, File.ReadAllBytes(path))) throw new InvalidOperationException("Image could not be decoded.");
            var texture = input;
            Edit("Import icon artwork", () => {
                var added = new DlcIconLayer(Path.GetFileNameWithoutExtension(path), document.size);
                float scale = Mathf.Min((float)document.size / texture.width, (float)document.size / texture.height);
                int width = Math.Max(1, Mathf.RoundToInt(texture.width * scale)), height = Math.Max(1, Mathf.RoundToInt(texture.height * scale));
                int left = (document.size - width) / 2, bottom = (document.size - height) / 2;
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    added.pixels[(bottom + y) * document.size + left + x] = document.pixelArt ? texture.GetPixel(Math.Min(texture.width - 1, x * texture.width / width), Math.Min(texture.height - 1, y * texture.height / height)) : texture.GetPixelBilinear((x + .5f) / width, (y + .5f) / height);
                document.layers.Add(added); layer = document.layers.Count - 1;
            });
        }
        catch (Exception e) { Say(e.Message); }
        finally { if (input) DestroyImmediate(input); }
    }
    byte[] Png() { var tex = DlcIconPixels.Texture(document); try { return ImageConversion.EncodeToPNG(tex); } finally { DestroyImmediate(tex); } }
    void Export()
    {
        try { string path = EditorUtility.SaveFilePanel("Export store icon", "", "store-icon.png", "png"); if (!string.IsNullOrEmpty(path)) { File.WriteAllBytes(path, Png()); Say("PNG exported. Editable layers remain in the icon document."); } }
        catch (Exception e) { Say(e.Message); }
    }
    void Assign()
    {
        try
        {
            var products = DlcReleaseService.Catalog()["products"];
            if (products.Count(p => (string)p["id"] == document.productId && (int?)p["type"] == 1) != 1) throw new InvalidOperationException("Choose one known non-consumable product ID.");
            var adapter = DlcProject.Required as IDlcIconAuthoring;
            if (adapter == null) throw new InvalidOperationException("This project adapter must implement IDlcIconAuthoring before assigning icons.");
            string path = adapter.IconAssetPath(document.productId).Replace('\\', '/');
            string full = Path.GetFullPath(path), assets = Path.GetFullPath("Assets") + Path.DirectorySeparatorChar;
            if (!full.StartsWith(assets, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Icon destination must be a PNG inside Assets.");
            if (File.Exists(path) && !EditorUtility.DisplayDialog("Replace product icon?", "Replace " + path + "? A timestamped PNG backup will be kept under the project's DLC backup directory. Pixel Undo does not restore exported files.", "Replace", "Cancel")) return;
            var bytes = Png();
            if (File.Exists(path)) { var backup = DlcProject.Required.BackupDirectory; Directory.CreateDirectory(backup); File.Copy(path, Path.Combine(backup, "icon-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".png")); }
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer) { importer.textureType = TextureImporterType.Default; importer.alphaIsTransparency = true; importer.mipmapEnabled = false; importer.textureCompression = TextureImporterCompression.Uncompressed; importer.filterMode = document.pixelArt ? FilterMode.Point : FilterMode.Bilinear; importer.npotScale = TextureImporterNPOTScale.None; importer.SaveAndReimport(); }
            Undo.RecordObject(document, "Record icon assignment"); document.assignedPath = path; EditorUtility.SetDirty(document); AssetDatabase.SaveAssets();
            Say("Assigned " + path + ". Restart Play mode to refresh the game's cached icons. Not uploaded to PlayFab.");
        }
        catch (Exception e) { Say(e.Message); }
    }
    void EndStroke()
    {
        if (!stroke) return;
        stroke = false; Undo.CollapseUndoOperations(undoGroup); if (document) EditorUtility.SetDirty(document);
    }
    void DrawCanvas()
    {
        if (!document || !preview || document.layers.Count == 0) return;
        float width = document.size * zoom;
        var r = GUILayoutUtility.GetRect(width, width, GUILayout.Width(width), GUILayout.Height(width));
        EditorGUI.DrawTextureTransparent(r, preview, ScaleMode.StretchToFill);
        if (grid && zoom >= 5)
        {
            Handles.BeginGUI(); Handles.color = new Color(.5f, .5f, .5f, .35f);
            for (int i = 0; i <= document.size; i++) { Handles.DrawLine(new Vector3(r.x + i * zoom, r.y), new Vector3(r.x + i * zoom, r.yMax)); Handles.DrawLine(new Vector3(r.x, r.y + i * zoom), new Vector3(r.xMax, r.y + i * zoom)); } Handles.EndGUI();
        }
        if (selection.width > 0 && selection.height > 0) GUI.Box(new Rect(r.x + selection.x * zoom, r.y + (document.size - selection.yMax) * zoom, selection.width * zoom, selection.height * zoom), "");
        var e = Event.current;
        Vector2Int Position() => new Vector2Int(Mathf.Clamp((int)((e.mousePosition.x - r.x) / zoom), 0, document.size - 1), Mathf.Clamp(document.size - 1 - (int)((e.mousePosition.y - r.y) / zoom), 0, document.size - 1));
        if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
        {
            if (!document.layers[layer].visible) { Say("Show the selected layer before painting."); return; }
            anchor = last = Position();
            if (tool == "Eyedropper") { color = DlcIconPixels.Composite(document)[anchor.y * document.size + anchor.x]; colorField.SetValueWithoutNotify(color); e.Use(); return; }
            Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Icon " + tool); Undo.RegisterCompleteObjectUndo(document, "Icon " + tool); stroke = true;
            if (tool == "Fill") { DlcIconPixels.Fill(document.layers[layer].pixels, document.size, anchor.x, anchor.y, color); EndStroke(); }
            else if (tool == "Pencil" || tool == "Eraser") Paint(anchor, anchor);
            RefreshTexture(); e.Use();
        }
        if (stroke && e.type == EventType.MouseDrag && e.button == 0)
        {
            var pos = Position(); if (tool == "Pencil" || tool == "Eraser") Paint(last, pos);
            if (tool == "Select") selection = SelectionRect(anchor, pos);
            last = pos; RefreshTexture(); e.Use();
        }
        if (stroke && (e.rawType == EventType.MouseUp || e.type == EventType.Ignore))
        {
            var pos = Position(); var pixels = document.layers[layer].pixels;
            if (tool == "Line") DlcIconPixels.Line(pixels, document.size, anchor, pos, color, mirror);
            if (tool == "Rectangle") { DlcIconPixels.Line(pixels, document.size, anchor, new Vector2Int(pos.x, anchor.y), color, mirror); DlcIconPixels.Line(pixels, document.size, new Vector2Int(pos.x, anchor.y), pos, color, mirror); DlcIconPixels.Line(pixels, document.size, pos, new Vector2Int(anchor.x, pos.y), color, mirror); DlcIconPixels.Line(pixels, document.size, new Vector2Int(anchor.x, pos.y), anchor, color, mirror); }
            if (tool == "Select") selection = SelectionRect(anchor, pos);
            if (tool == "Move") { DlcIconPixels.Move(pixels, document.size, selection, pos - anchor); selection = new RectInt(); }
            EndStroke(); RefreshTexture(); e.Use();
        }
        GUILayout.Label("Store preview · 64 px / 128 px · " + document.size + " × " + document.size + " source");
        GUILayout.BeginHorizontal(); GUILayout.Label(preview, GUILayout.Width(64), GUILayout.Height(64)); GUILayout.Label(preview, GUILayout.Width(128), GUILayout.Height(128)); GUILayout.EndHorizontal();
    }
    static RectInt SelectionRect(Vector2Int a, Vector2Int b) => new RectInt(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Abs(a.x - b.x) + 1, Math.Abs(a.y - b.y) + 1);
    void Paint(Vector2Int a, Vector2Int b) => DlcIconPixels.Line(document.layers[layer].pixels, document.size, a, b, tool == "Eraser" ? (Color32)Color.clear : (Color32)color, mirror);
}
