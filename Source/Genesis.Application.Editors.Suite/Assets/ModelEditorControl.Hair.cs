using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Assets;

public partial class ModelEditorControl
{
    private void EditHairStyles()
    {
        using var dialog = new ModelHairDialog(Asset);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ChangeAsset("Edit hair styles", () => dialog.Result.Save(Asset));
    }
}
