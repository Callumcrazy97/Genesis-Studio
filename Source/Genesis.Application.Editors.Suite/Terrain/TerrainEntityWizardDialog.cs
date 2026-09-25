using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Modal wrapper around <see cref="TerrainEntityWizardPanel"/> for legacy flows and headless tests.
/// </summary>
public sealed class TerrainEntityWizardDialog : DpiAwareForm
{
    private readonly TerrainEntityWizardPanel _panel;

    public TerrainEntityWizardDialog(string resourcePath, string projectRoot, TerrainEntityType? presetType)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = EditorChrome.Canvas;
        ClientSize = new Size(920, 640);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Terrain Entity";

        _panel = new TerrainEntityWizardPanel(resourcePath, projectRoot, presetType)
        {
            Dock = DockStyle.Fill,
        };
        _panel.Saved += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        _panel.Cancelled += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.Add(_panel);
    }

    public string ResourcePath => _panel.ResourcePath;

    public string EntityName => _panel.EntityName;

    public TerrainEntityType EntityType => _panel.EntityType;

    public IReadOnlyList<TerrainEntityComponent> Components => _panel.Components;

    public Control ContentHost => _panel.ContentHost;

    public void SetName(string name) => _panel.SetName(name);

    public void SetType(TerrainEntityType type) => _panel.SetType(type);

    public void SaveAndCloseForTest() => _panel.SaveAndCloseForTest();

    public void GoToPage(int page) => _panel.GoToPage(page);

    public void AddComponent(string kind) => _panel.AddComponent(kind);

    public void SetComponentProperty(int index, string key, string value) =>
        _panel.SetComponentProperty(index, key, value);
}
