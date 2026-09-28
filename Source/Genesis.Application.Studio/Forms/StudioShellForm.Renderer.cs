using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;

namespace Genesis.Application.Studio.Forms;

public sealed partial class StudioShellForm
{
    private ToolStripDropDownButton CreateRendererSelector()
    {
        ToolStripDropDownButton selector = new(
            RenderingPreferencesBridge.BackendStatusText(_services.Settings.Current.Rendering))
        {
            Name = "RendererSelector",
            AccessibleName = "Rendering backend",
            AccessibleDescription = "Choose the renderer for live editor previews and the next game run.",
            ToolTipText = "Change renderer for editor previews and the next game run",
            ForeColor = ThemeService.Palette.Accent,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
        };
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            ToolStripMenuItem item = new(backend.DisplayName)
            {
                Name = "renderer." + backend.ShortName,
                Tag = backend,
                Checked = RenderBackendSelection.RequestedBackend == backend.Backend,
            };
            item.Click += (_, _) => SelectRenderer(backend);
            selector.DropDownItems.Add(item);
        }
        selector.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in selector.DropDownItems.OfType<ToolStripMenuItem>())
            {
                RenderBackendDescriptor backend = (RenderBackendDescriptor)item.Tag!;
                item.Checked = RenderBackendSelection.RequestedBackend == backend.Backend;
                item.Enabled = backend.IsImplemented && RenderBackendSelection.IsAvailable(backend.Backend);
                item.ToolTipText = item.Enabled
                    ? "Use " + backend.DisplayName
                    : backend.DisplayName + " is unavailable on this machine.";
            }
        };
        return selector;
    }

    private void SelectRenderer(RenderBackendDescriptor backend)
    {
        if (RenderBackendSelection.RequestedBackend == backend.Backend)
            return;
        if (!backend.IsImplemented || !RenderBackendSelection.IsAvailable(backend.Backend))
        {
            SetStatus(backend.DisplayName + " is unavailable on this machine.");
            return;
        }
        string previous = _services.Settings.Current.Rendering.Backend;
        try
        {
            // SettingsChanged applies the same renderer preference as the Preferences dialog;
            // live viewports rebuild through EffectiveBackendChanged without saving editor drafts.
            _services.Settings.Update(settings => settings.Rendering.Backend = backend.SettingsValue);
            SetStatus("Renderer: " + backend.DisplayName + ". The next game run uses this renderer.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _services.Settings.Current.Rendering.Backend = previous;
            ApplyRuntimePreferences();
            SetStatus("Could not save renderer preference: " + error.Message);
        }
    }
}
