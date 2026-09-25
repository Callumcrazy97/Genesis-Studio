using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Shared context-sensitive inspector host. Editors register one panel per mode and show exactly
/// one at a time, keeping rich tools available without stacking every field into the viewport.
/// </summary>
public sealed class EditorModeHost : Panel
{
    private readonly Dictionary<string, Control> _modes = new(StringComparer.OrdinalIgnoreCase);

    public EditorModeHost()
    {
        BackColor = EditorChrome.Surface;
        Dock = DockStyle.Fill;
    }

    public IReadOnlyList<string> ModeNames => _modes.Keys.ToArray();

    public string ActiveMode { get; private set; } = string.Empty;

    public event EventHandler? ActiveModeChanged;

    public void AddMode(string name, Control content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        if (_modes.ContainsKey(name))
        {
            throw new InvalidOperationException($"An inspector mode named '{name}' is already registered.");
        }

        content.Dock = DockStyle.Fill;
        content.Visible = false;
        _modes.Add(name, content);
        Controls.Add(content);
        if (_modes.Count == 1)
        {
            ShowMode(name);
        }
    }

    public bool ShowMode(string name)
    {
        if (!_modes.TryGetValue(name, out Control? selected))
        {
            return false;
        }

        foreach (Control content in _modes.Values)
        {
            content.Visible = ReferenceEquals(content, selected);
        }

        selected.BringToFront();
        ActiveMode = name;
        ActiveModeChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
