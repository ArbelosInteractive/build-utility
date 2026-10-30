using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class DlcPlayFabWindow : EditorWindow
{
    [SerializeField] DlcPlayFabConnection connection;
    [SerializeField] DlcWaveSchedule schedule;
    [SerializeField] DlcIconDocument icon;
    DlcPlayFabClient client;
    CancellationTokenSource cancellation;
    List<DlcPlayFabChange> changes;
    string fingerprint;
    TextField review;
    Label status;
    VisualElement controls;
    bool busy;
    [MenuItem("Build Utility/DLC PlayFab Review")]
    public static void Open() => GetWindow<DlcPlayFabWindow>("DLC PlayFab Review");
    public static void ForSchedule(DlcWaveSchedule value) { var w = GetWindow<DlcPlayFabWindow>("DLC PlayFab Review"); w.schedule = value; w.changes = null; w.CreateGUI(); }
    public static void ForIcon(DlcIconDocument value) { var w = GetWindow<DlcPlayFabWindow>("DLC PlayFab Review"); w.icon = value; w.changes = null; w.CreateGUI(); }
    void OnDisable() { cancellation?.Cancel(); client?.Dispose(); client = null; }
    public void CreateGUI()
    {
        var root = rootVisualElement; root.Clear(); minSize = new Vector2(680, 450);
        root.Add(new HelpBox("Review is read-only. Publish updates existing PlayFab items and can upload the selected icon. It does not activate Apple/Google purchases. Catalog availability controls shop discovery; marketplace purchase authorization remains separate.", HelpBoxMessageType.Info));
        controls = new VisualElement(); root.Add(controls);
        ObjectField Field(string title, Type type, UnityEngine.Object value, Action<UnityEngine.Object> setter)
        {
            var f = new ObjectField(title) { objectType = type, allowSceneObjects = false, value = value }; f.RegisterValueChangedCallback(e => { setter(e.newValue); changes = null; CreateGUI(); }); controls.Add(f); return f;
        }
        Field("Connection", typeof(DlcPlayFabConnection), connection, o => connection = o as DlcPlayFabConnection);
        controls.Add(DlcArtistWindow.Button("New connection", () => { string path = DlcArtistWindow.DraftPath("PlayFab Connection"); if (path == null) return; connection = CreateInstance<DlcPlayFabConnection>(); AssetDatabase.CreateAsset(connection, path); Undo.RegisterCreatedObjectUndo(connection, "Create PlayFab connection"); CreateGUI(); }));
        if (connection)
        {
            var serialized = new SerializedObject(connection);
            foreach (string name in new[] { "environmentName", "titleId", "secretEnvironmentVariable", "ownershipSafeClientDeployed" }) controls.Add(new PropertyField(serialized.FindProperty(name)));
            controls.Bind(serialized);
        }
        Field("Schedule (optional)", typeof(DlcWaveSchedule), schedule, o => schedule = o as DlcWaveSchedule);
        Field("Icon (optional)", typeof(DlcIconDocument), icon, o => icon = o as DlcIconDocument);
        var actions = DlcArtistWindow.Row(); controls.Add(actions);
        actions.Add(DlcArtistWindow.Button("Read & review PlayFab changes", () => Run(Prepare)));
        actions.Add(DlcArtistWindow.Button("Publish reviewed changes…", () => Run(Publish)));
        root.Add(DlcArtistWindow.Button("Cancel network work", () => cancellation?.Cancel()));
        review = new TextField("Reviewed changes") { multiline = true, isReadOnly = true }; review.style.flexGrow = 1; review.style.minHeight = 180; root.Add(review);
        status = new Label("No live actions run automatically. Set the secret environment variable outside Unity and restart the Editor before connecting."); status.style.whiteSpace = WhiteSpace.Normal; root.Add(status);
        controls.SetEnabled(!busy);
        if (changes != null) review.value = Describe();
    }
    JObject ScheduleJson()
    {
        if (!schedule) return null;
        var products = (JArray)DlcReleaseService.Catalog()["products"];
        return DlcWaveExport.Build(schedule, products.Where(p => (int?)p["type"] == 1).Select(p => (string)p["id"]));
    }
    byte[] IconBytes()
    {
        if (!icon) return null;
        var tex = DlcIconPixels.Texture(icon); try { return ImageConversion.EncodeToPNG(tex); } finally { DestroyImmediate(tex); }
    }
    string Fingerprint(JObject json, byte[] bytes) => DlcPlayFabPlan.Hash(Encoding.UTF8.GetBytes((connection ? EditorJsonUtility.ToJson(connection) : "") + (json?.ToString() ?? "") + (icon ? icon.productId + ":" + icon.pixelArt : "") + (bytes == null ? "" : DlcPlayFabPlan.Hash(bytes))));
    async void Run(Func<Task> operation)
    {
        if (busy) return; busy = true; controls.SetEnabled(false); cancellation?.Dispose(); cancellation = new CancellationTokenSource();
        try { await operation(); }
        catch (OperationCanceledException) { if (this) status.text = "Cancelled. A request already accepted by PlayFab may still finish. Review remote state before retrying."; changes = null; }
        catch (Exception e) { if (this) status.text = e.Message; changes = null; }
        finally { busy = false; if (this && controls != null) controls.SetEnabled(true); }
    }
    async Task Prepare()
    {
        changes = null;
        if (!connection || string.IsNullOrWhiteSpace(connection.environmentName)) throw new InvalidOperationException("Choose a named connection first.");
        if (!schedule && !icon) throw new InvalidOperationException("Choose a schedule, an icon, or both.");
        var json = ScheduleJson(); var bytes = IconBytes();
        var ids = new HashSet<string>((json?["waves"] as JArray ?? new JArray()).SelectMany(w => (JArray)w["productIds"]).Select(t => (string)t), StringComparer.Ordinal);
        if (icon)
        {
            if (!((JArray)DlcReleaseService.Catalog()["products"]).Any(p => (string)p["id"] == icon.productId && (int?)p["type"] == 1)) throw new InvalidOperationException("The icon must have a known non-consumable product ID.");
            ids.Add(icon.productId);
        }
        string captured = Fingerprint(json, bytes);
        client?.Dispose(); client = new DlcPlayFabClient(connection.titleId);
        status.text = "Connecting to " + connection.environmentName + " / " + connection.titleId + " (read-only review)…";
        await client.Authenticate(connection.secretEnvironmentVariable, cancellation.Token);
        var proposed = new List<DlcPlayFabChange>();
        foreach (string id in ids.OrderBy(id => id, StringComparer.Ordinal))
        {
            status.text = "Reading " + id;
            proposed.Add(await DlcPlayFabPlan.Read(client, id, json, icon && icon.productId == id ? bytes : null, icon && icon.pixelArt, cancellation.Token));
        }
        if (captured != Fingerprint(ScheduleJson(), IconBytes())) throw new InvalidOperationException("Local settings changed while reading. Review again.");
        changes = proposed; fingerprint = captured; review.value = Describe();
        status.text = "Reviewed " + changes.Count + " products. No catalog changes made. Apple/Google readiness is not checked.";
    }
    string Describe() => new JObject { ["environment"] = connection.environmentName, ["titleId"] = connection.titleId, ["changes"] = new JArray(changes.Select(DlcPlayFabPlan.Summary)), ["note"] = "Products removed from the local schedule are not automatically reverted remotely. Review dates/visibility explicitly before removal." }.ToString();
    async Task Publish()
    {
        if (changes == null || client == null || fingerprint != Fingerprint(ScheduleJson(), IconBytes())) throw new InvalidOperationException("Read and review the current changes first.");
        if (schedule && !connection.ownershipSafeClientDeployed) throw new InvalidOperationException("Deploy the ownership-safe client before publishing availability waves, then confirm it in this connection.");
        if (!EditorUtility.DisplayDialog("Publish to " + connection.environmentName + " / " + connection.titleId + "?", "Apply the reviewed PlayFab changes for " + changes.Count + " products? Availability dates may hide items immediately. Existing owners must retain access through the updated client. Apple/Google availability is not changed. A receipt records per-item results; publication is not atomic.", "Publish reviewed changes", "Cancel")) return;
        string receipt = EditorUtility.SaveFilePanel("Save publish receipt", "", "dlc-playfab-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + ".json", "json"); if (string.IsNullOrEmpty(receipt)) return;
        var entries = new JArray();
        var report = new JObject { ["titleId"] = connection.titleId, ["environment"] = connection.environmentName, ["startedUtc"] = DateTime.UtcNow.ToString("O"), ["results"] = entries };
        File.WriteAllText(receipt, report.ToString());
        foreach (var change in changes)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var entry = new JObject { ["productId"] = change.productId, ["itemId"] = change.before["Id"].DeepClone(), ["status"] = "in-progress" }; entries.Add(entry); File.WriteAllText(receipt, report.ToString());
            try { status.text = "Publishing " + change.productId; await DlcPlayFabPlan.Publish(client, change, cancellation.Token); entry["status"] = "verified"; }
            catch { entry["status"] = "unverified-inspect-remote-before-retry"; File.WriteAllText(receipt, report.ToString()); throw; }
            File.WriteAllText(receipt, report.ToString());
        }
        status.text = "Verified " + changes.Count + " published items. Receipt: " + receipt; changes = null; client.Dispose(); client = null;
    }
}
