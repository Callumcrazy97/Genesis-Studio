using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelEditorControl
{
    /// <summary>
    /// "Generate": seeded trees and rocks as new parts. The generators and their dialog already
    /// existed (the Nature Walk template uses them) but nothing in the Model editor opened them.
    /// </summary>
    private void BuildGeneratorSection(FlowLayoutPanel page)
    {
        CollapsibleSection section = Section(page, "GENERATE", 118);
        Button tree = Tool("Tree…", OpenTreeGenerator);
        tree.Name = "ModelGenerateTree";
        tree.SetBounds(8, 6, 122, 31);
        Button rock = Tool("Rock…", OpenRockGenerator);
        rock.Name = "ModelGenerateRock";
        rock.SetBounds(136, 6, 122, 31);
        section.Content.Controls.Add(tree);
        section.Content.Controls.Add(rock);
        section.Content.Controls.Add(new Label
        {
            Text = "Seeded trees and rocks with their own colours. Change the seed for a new one.",
            Location = new Point(8, 44),
            Size = new Size(250, 44),
            ForeColor = EditorChrome.Muted,
        });
    }

    private void OpenTreeGenerator()
    {
        using ProceduralModelDialog dialog = ProceduralModelDialog.ForTree(new ProceduralTreeOptions());
        if (dialog.ShowDialog(this) == DialogResult.OK) AddGeneratedTree(dialog.TreeOptions);
    }

    private void OpenRockGenerator()
    {
        using ProceduralModelDialog dialog = ProceduralModelDialog.ForRock(new ProceduralRockOptions());
        if (dialog.ShowDialog(this) == DialogResult.OK) AddGeneratedRock(dialog.RockOptions);
    }

    /// <summary>Adds a generated tree as a new part. Undoable.</summary>
    public void AddGeneratedTree(ProceduralTreeOptions options) =>
        AddGenerated(ProceduralModelGenerator.GenerateTree(options), "Tree");

    /// <summary>Adds a generated rock as a new part. Undoable.</summary>
    public void AddGeneratedRock(ProceduralRockOptions options) =>
        AddGenerated(ProceduralModelGenerator.GenerateRock(options), "Rock");

    private void AddGenerated(ProceduralMeshResult result, string kind)
    {
        GModelAsset geometry = new() { Name = kind };
        // The generators write their bark, leaf and stone colours into the vertices; a white,
        // untextured material shows them as they are.
        geometry.Materials.Add(new GModelMaterial { Name = kind, BaseColor = Vector4.One, RoughnessFactor = 0.9f });
        geometry.Meshes.Add(new GModelMesh
        {
            Name = kind,
            MaterialIndex = 0,
            Vertices = result.Vertices,
            Indices = result.Indices,
            IsSkinned = false,
        });
        geometry.RecalculateBounds();
        AddGeometry(geometry, "Generate " + kind.ToLowerInvariant(),
            $"Generated a {kind.ToLowerInvariant()} · move or scale the part, or save and Use in game");
    }
}
