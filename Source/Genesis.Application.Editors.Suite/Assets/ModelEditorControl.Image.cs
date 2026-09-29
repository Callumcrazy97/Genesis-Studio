using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelEditorControl
{
    public ModelImageDialog CreateImageDialog(string? imageReference = null) => new(ProjectRoot, imageReference);

    private void OpenImageCreation()
    {
        using ModelImageDialog dialog = CreateImageDialog();
        if (dialog.ShowDialog(this) == System.Windows.Forms.DialogResult.OK && dialog.Result is not null)
            AddImageGeometry(dialog.Result);
    }

    public ModelTexturePaintDialog CreateTexturePaintDialog()
    {
        FinishStroke();
        int mesh = _groups.SelectedIndex;
        if (mesh < 0 || mesh >= Asset.Meshes.Count) throw new InvalidOperationException("Select the mesh you want to paint first.");
        return new(ProjectRoot, Asset, Asset.Meshes[mesh].MaterialIndex);
    }

    private void OpenTexturePainting()
    {
        try { using ModelTexturePaintDialog dialog = CreateTexturePaintDialog(); dialog.ShowDialog(this); RefreshMaterialInspector(); }
        catch (Exception ex) { System.Windows.Forms.MessageBox.Show(this, ex.Message, "Paint Model Image"); }
    }

    public void AddImageGeometry(GModelAsset geometry)
    {
        if (!geometry.HasRenderableMeshes) throw new ArgumentException("The Image has no geometry.", nameof(geometry));
        GModelAsset copy = ModelPoseWorkflow.Copy(geometry);
        ChangeAsset("Create from Image", () =>
        {
            int materialOffset = Asset.Materials.Count;
            Asset.Materials.AddRange(copy.Materials);
            foreach (GModelMesh mesh in copy.Meshes)
            {
                mesh.MaterialIndex += materialOffset;
                mesh.TriangleMaterialIndices = mesh.TriangleMaterialIndices.Select(index => index + materialOffset).ToArray();
                Asset.Meshes.Add(mesh);
            }
        });
        SelectPart(Asset.Meshes.Count - 1);
        FrameModel();
        Status.Text = "Created from Image · edit the part, or save and Use in game · colours stay linked to the Image";
    }
}
