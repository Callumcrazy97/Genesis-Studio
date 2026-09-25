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
        var donor = ModelMotionImport.ReadDonor(path);
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
}
