using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

public partial class ModelViewerControl
{
    private readonly ToolStripDropDownButton _moreOptions = new("More Options") { Name = "ModelMoreOptions", Padding = new Padding(6, 4, 6, 4), Overflow = ToolStripItemOverflow.Never };
    private DateTime _motionFeedbackUntil;
    public string LastMotionImportMessage { get; private set; } = "";

    private void BuildMotionImportMenu()
    {
        _moreOptions.DropDownItems.Add(new ToolStripMenuItem("Import Rig From Model", null, (_, _) => ChooseMotionImport(false)) { Name = "ImportRigFromModel" });
        _moreOptions.DropDownItems.Add(new ToolStripMenuItem("Import Model as animation", null, (_, _) => ChooseMotionImport(true)) { Name = "ImportModelAsAnimation" });
        Commands.Items.Add(_moreOptions);
    }

    private async void ChooseMotionImport(bool animation)
    {
        if (_importing) return;
        using var picker = new OpenFileDialog { Title = animation ? "Import Model as animation" : "Import Rig From Model", Filter = ModelMotionImport.FileFilter, CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            await ImportMotionAsync(picker.FileName, animation);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show(this, ex.Message, animation ? "Animation import failed" : "Rig import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public Task<IReadOnlyList<string>> ImportModelAsAnimationAsync(string source) => ImportMotionAsync(source, animation: true);

    private async Task<IReadOnlyList<string>> ImportMotionAsync(string source, bool animation)
    {
        if (_importing) throw new InvalidOperationException("A model import is already running.");
        var before = ModelPoseWorkflow.Copy(Asset); string oldClip = ActiveClip;
        SetPlaying(false); _importing = true; Enabled = false; UseWaitCursor = true;
        Status.Text = animation ? "Importing animation clips…" : "Importing and binding rig…";
        try
        {
            var result = await Task.Run(() => PrepareMotionImport(before, source, animation)).ConfigureAwait(false);
            if (!IsDisposed) await InvokeAsync(() => { if (!IsDisposed) CommitMotionImport(result, before, oldClip, animation); });
            return result.Clips;
        }
        finally { if (!IsDisposed) await InvokeAsync(() => { _importing = false; Enabled = true; UseWaitCursor = false; }); }
    }

    public void ImportRigFromModel(string source) => ImportMotion(source, animation: false);
    public IReadOnlyList<string> ImportModelAsAnimation(string source) => ImportMotion(source, animation: true);

    private IReadOnlyList<string> ImportMotion(string source, bool animation)
    {
        if (_importing) throw new InvalidOperationException("A model import is already running.");
        var before = ModelPoseWorkflow.Copy(Asset); string oldClip = ActiveClip;
        var result = PrepareMotionImport(before, source, animation);
        CommitMotionImport(result, before, oldClip, animation); return result.Clips;
    }

    private ModelMotionImportResult PrepareMotionImport(GModelAsset before, string path, bool animation)
    {
        // Clips from a glTF file are mirrored the same way the model receiving them was.
        var donor = ModelMotionImport.ReadDonor(path, before.ImportSettings?.ConvertRightHanded == true);
        string file = Path.GetFileName(path);
        string name = ResourceDisplayName.Format(file);
        var result = animation ? ModelMotionImport.Animations(before, donor, name) : ModelMotionImport.Rig(before, donor, name);
        PersistModelChanges(result.Asset);
        return result;
    }

    private void CommitMotionImport(ModelMotionImportResult result, GModelAsset before, string oldClip, bool animation)
    {
        string clip = result.Clips.FirstOrDefault() ?? oldClip;
        void Restore(GModelAsset asset, string selected)
        {
            SetPlaying(false); Asset = ModelPoseWorkflow.Copy(asset); RefreshAssetPresentation(); OnMotionImported(); SelectClip(selected);
        }
        Restore(result.Asset, clip);
        PushEdit(animation ? "Import animations" : "Import rig", () => Restore(result.Asset, clip), () => Restore(before, oldClip));
        AcceptSave(); LastMotionImportMessage = result.Message; _motionFeedbackUntil = DateTime.UtcNow.AddSeconds(15);
        Status.Text = result.Message;
    }

    protected virtual void OnMotionImported() { }

    protected void PersistModelChanges(GModelAsset asset)
    {
        string original = File.ReadAllText(ResourcePath);
        var document = JsonNode.Parse(original)!.AsObject();
        document["schemaVersion"] = 5; document["parts"] = new JsonArray(); document.Remove("primitive");
        document["rig"] = asset.Rig.IsValid ? "Canonical" : null;
        document["animations"] = new JsonArray(asset.Animations.Select(c => (JsonNode?)JsonValue.Create(c.Name)).ToArray());
        WriteMaterialShaders(document, asset);
        WriteMaterialUnlit(document, asset);
        WriteMaterialUvScroll(document, asset);
        string canonical = StudioModelResourceLoader.CanonicalPath(ResourcePath);
        string staged = Path.Combine(ResourceAssociates.GetModelDataDirectory(ResourcePath), ".motion-import-" + Guid.NewGuid().ToString("N") + ".gmodel");
        string backup = staged + ".previous"; bool replaced = false, rollbackSucceeded = true;
        try
        {
            RuntimeModelStore.Save(staged, asset);
            if (File.Exists(canonical)) File.Copy(canonical, backup);
            ResourceBackupService.BackupBeforeOverwrite(canonical);
            ProjectAssetWriteRegistry.MarkLocalWrite(canonical);
            File.Move(staged, canonical, overwrite: true); replaced = true;
            WriteResourceText(document.ToJsonString(new() { WriteIndented = true }));
        }
        catch
        {
            if (replaced)
            {
                rollbackSucceeded = false;
                if (File.Exists(backup)) File.Copy(backup, canonical, overwrite: true); else File.Delete(canonical);
                if (File.ReadAllText(ResourcePath) != original) File.WriteAllText(ResourcePath, original);
                rollbackSucceeded = true;
            }
            throw;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
            if (rollbackSucceeded && File.Exists(backup)) File.Delete(backup);
        }
    }

    /// <summary>
    /// Keeps which materials are unlit in the descriptor's <c>materialUnlit</c>, so a re-import of
    /// the source (which may say otherwise) cannot undo the choice. Written once any material is
    /// unlit or the entry exists; every material is listed, lit ones as false.
    /// </summary>
    public static void WriteMaterialUnlit(JsonObject document, GModelAsset asset)
    {
        string property = document.Select(entry => entry.Key)
            .FirstOrDefault(key => string.Equals(key, "materialUnlit", StringComparison.OrdinalIgnoreCase)) ?? "materialUnlit";
        if (document[property] is null && !asset.Materials.Any(material => material?.Unlit == true)) return;
        var map = new JsonObject();
        foreach (GModelMaterial material in asset.Materials)
            if (!string.IsNullOrWhiteSpace(material?.Name)) map[material.Name] = material.Unlit;
        document.Remove(property);
        document[property] = map;
    }

    /// <summary>
    /// Keeps how fast each material's picture slides in the descriptor's <c>materialUvScroll</c>
    /// ([u, v] UV units a second), so a re-import of the source keeps it. Only moving materials are
    /// listed; the entry goes once none moves.
    /// </summary>
    public static void WriteMaterialUvScroll(JsonObject document, GModelAsset asset)
    {
        string property = document.Select(entry => entry.Key)
            .FirstOrDefault(key => string.Equals(key, "materialUvScroll", StringComparison.OrdinalIgnoreCase)) ?? "materialUvScroll";
        var map = new JsonObject();
        foreach (GModelMaterial material in asset.Materials)
            if (!string.IsNullOrWhiteSpace(material?.Name) && (material.UvScrollU != 0f || material.UvScrollV != 0f))
                map[material.Name] = new JsonArray(material.UvScrollU, material.UvScrollV);
        document.Remove(property);
        if (map.Count > 0) document[property] = map;
    }

    /// <summary>
    /// Keeps each material's own mesh shader in the descriptor's <c>materialShaders</c>, where a
    /// re-import of the model's source cannot lose it. Entries for materials the model does not
    /// have (any more) are left as they are.
    /// </summary>
    public static void WriteMaterialShaders(JsonObject document, GModelAsset asset)
    {
        // The descriptor is read without regard to case, so an existing "MaterialShaders" is the same entry.
        string property = document.Select(entry => entry.Key)
            .FirstOrDefault(key => string.Equals(key, "materialShaders", StringComparison.OrdinalIgnoreCase)) ?? "materialShaders";
        JsonObject? existing = document[property] as JsonObject;
        JsonObject map = existing ?? new JsonObject();
        foreach (GModelMaterial material in asset.Materials)
        {
            if (string.IsNullOrWhiteSpace(material?.Name)) continue;
            foreach (string key in map.Select(entry => entry.Key)
                         .Where(key => string.Equals(key, material.Name, StringComparison.OrdinalIgnoreCase)).ToArray())
                map.Remove(key);
            if (!string.IsNullOrWhiteSpace(material.Shader)) map[material.Name] = material.Shader.Trim();
        }
        if (existing is not null)
        {
            if (map.Count == 0) document.Remove(property);
        }
        else if (map.Count > 0)
        {
            document.Remove(property);
            document[property] = map;
        }
    }
}
