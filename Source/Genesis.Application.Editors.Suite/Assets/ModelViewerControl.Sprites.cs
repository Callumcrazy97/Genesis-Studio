using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public partial class ModelViewerControl
{
    /// <summary>
    /// The "Convert to 2D sprites" panel for the model as it is now, with the viewer's selected
    /// animation and lighting as its starting point. Sprites start orthographic, so every direction
    /// keeps the same size on screen; the panel offers perspective.
    /// </summary>
    public ModelSpriteConversionDialog CreateSpriteConversionDialog()
    {
        GModelAnimationClip? clip = SelectedClip;
        var defaults = new ModelSpriteBakeSettings
        {
            Clip = clip?.Name ?? "",
            FramesPerSecond = clip is null ? 12 : Math.Clamp(Math.Round(clip.Fps), 1, 60),
            AnimationFrames = clip is null ? 8 : Math.Clamp(clip.Frames.Count, 1, 120),
            Lighting = _lighting,
            Ambient = _ambient,
            KeyIntensity = _keyIntensity,
            LightAngleDegrees = _lightAngle,
        };
        return new ModelSpriteConversionDialog(Asset, ProjectRoot, ResourcePath, defaults);
    }

    private void ShowSpriteConversion()
    {
        if (ModelImportInProgress) return;
        if (!Asset.HasRenderableMeshes)
        {
            MessageBox.Show(this, "Import or create model geometry first.", "Convert to 2D sprites", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using ModelSpriteConversionDialog dialog = CreateSpriteConversionDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.CreatedImagePath is { } path) RequestOpenLinkedResource(path);
    }
}
