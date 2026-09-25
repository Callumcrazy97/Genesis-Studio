using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private SplitContainer _entitiesSplit = null!;
    private Panel _entityWizardHost = null!;
    private TerrainEntityWizardPanel? _entityWizard;
    private string? _pendingEntityPath;
    private Form? _entityWizardWindow;

    private void WireEntitiesMode(string projectRoot)
    {
        _entitiesSplit = new SplitContainer
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 240,
        };

        _entityListPanel = new TerrainEntityListPanel(projectRoot, () =>
            _settings.Entities.Select(ResolveEntityFullPath)
                .Concat(Directory.Exists(ResourcePath + ".parts")
                    ? Directory.EnumerateFiles(ResourcePath + ".parts", "*.terrainpart.json") : []))
        {
            Dock = DockStyle.Fill,
        };
        _entityListPanel.CreateRequested += OnEntityCreateRequested;
        _entityListPanel.EditRequested += OnEntityEditRequested;

        _entityWizardHost = new Panel
        {
            BackColor = EditorChrome.Canvas,
            Dock = DockStyle.Fill,
            Visible = false,
        };
        Label wizardHint = new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Text = "Select an entity to edit, or use + on a group header to create one in-panel.",
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _entityWizardHost.Controls.Add(wizardHint);

        _entitiesSplit.Panel1.Controls.Add(_entityListPanel);
        _entitiesSplit.Panel2.Controls.Add(_entityWizardHost);
    }

    private void ShowEntityWizard(string resourcePath, TerrainEntityType? presetType, bool isNew)
    {
        if (resourcePath.EndsWith(".object.json", StringComparison.OrdinalIgnoreCase))
        {
            var editor = new Objects.ObjectEditorControl(resourcePath, ProjectRoot) { Dock = DockStyle.Fill };
            var window = new DpiAwareForm { Text = "Object · " + ResourceDisplayName.Format(resourcePath), ClientSize = new Size(1400, 900), StartPosition = FormStartPosition.CenterParent };
            window.Controls.Add(editor); window.Show(FindForm()); return;
        }
        CloseEntityWizard(trashPending: true);

        _pendingEntityPath = isNew ? resourcePath : null;
        _entityWizard = new TerrainEntityWizardPanel(resourcePath, ProjectRoot, presetType)
        {
            Dock = DockStyle.Fill,
        };
        _entityWizard.Saved += (_, _) =>
        {
            string saved = _entityWizard?.ResourcePath ?? resourcePath;
            _pendingEntityPath = null;
            CloseEntityWizard(trashPending: false);
            BindAndPlaceSavedEntity(saved, place: true);
            _entityListPanel.RefreshEntities();
            UpdateStatus();
        };
        _entityWizard.Cancelled += (_, _) =>
        {
            CloseEntityWizard(trashPending: isNew);
            _entityListPanel.RefreshEntities();
            UpdateStatus();
        };

        _entityWizardWindow = new DpiAwareForm
        {
            Text = isNew ? "Add terrain object" : "Edit terrain object",
            BackColor = EditorChrome.Canvas, ClientSize = new Size(1180, 780),
            MinimumSize = new Size(960, 660), StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, ShowInTaskbar = false,
        };
        _entityWizardWindow.Controls.Add(_entityWizard);
        _entityWizardWindow.FormClosed += (_, _) => CloseEntityWizard(trashPending: true);
        SetMode(TerrainEditorMode.Entities);
        _entityWizardWindow.Show(FindForm());
        UpdateStatus();
    }

    private void CloseEntityWizard(bool trashPending)
    {
        Form? window = _entityWizardWindow;
        _entityWizardWindow = null;
        if (window is not null && !window.IsDisposed)
        {
            if (_entityWizard is not null) window.Controls.Remove(_entityWizard);
            window.Dispose();
        }
        if (_entityWizard is not null)
        {
            _entityWizardHost.Controls.Remove(_entityWizard);
            _entityWizard.Dispose();
            _entityWizard = null;
        }

        if (trashPending && !string.IsNullOrWhiteSpace(_pendingEntityPath))
        {
            try
            {
                ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
                resources.MoveToTrash(_pendingEntityPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup for cancelled creation.
            }
        }

        _pendingEntityPath = null;

        if (_entityWizardHost.Controls.Count == 0)
        {
            Label wizardHint = new()
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Font = EditorChrome.SmallFont,
                ForeColor = EditorChrome.Muted,
                Text = "Select an entity to edit, or use + on a group header to create one in-panel.",
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _entityWizardHost.Controls.Add(wizardHint);
            _entityWizardHost.Visible = false;
        }
    }

    private void BindAndPlaceSavedEntity(string resourcePath, bool place)
    {
        if (string.IsNullOrWhiteSpace(resourcePath) || !File.Exists(resourcePath))
        {
            return;
        }

        string relative = ResourceNames.Name(ProjectRoot, resourcePath);
        if (!_settings.Entities.Contains(relative, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Entities.Add(relative);
        }

        ApplySavedEntityRulesToPlacements(resourcePath, relative);

        if (!place)
        {
            MarkDirty();
            RefreshComponentsPanel();
            _viewport.Invalidate();
            return;
        }

        _placementEntityPath = resourcePath;
        MarkDirty();
        RefreshComponentsPanel();
        SetMode(TerrainEditorMode.Select);
        RefreshActiveToolCard();
        _statusLabel.Text = "Click terrain to place the object · Ctrl keeps placing · Esc cancels";
    }

    private void ApplySavedEntityRulesToPlacements(string resourcePath, string relative)
    {
        TerrainEntityDocument? document = TryLoadEntityDocument(resourcePath);
        string clip = document?.Components
            .FirstOrDefault(component => component.Type == TerrainEntityComponentKinds.Model)
            ?.Get("AnimationClip") ?? string.Empty;
        TerrainEntityComponent? rule = document?.Components
            .FirstOrDefault(component => component.Type == TerrainEntityComponentKinds.Condition);
        foreach (TerrainPlacedEntity placed in _nature.PlacedEntities)
        {
            if (!string.Equals(placed.Entity, relative, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            placed.AnimationClip = clip;
            placed.IfExpression = rule?.Get("If") ?? string.Empty;
            placed.ThenClip = rule?.Get("ThenClip") ?? string.Empty;
            placed.ElseClip = rule?.Get("ElseClip") ?? string.Empty;
            placed.ThenSource = rule?.Get("ThenSource") ?? string.Empty;
            placed.ElseSource = rule?.Get("ElseSource") ?? string.Empty;
            placed.Normalize();
        }
    }

    private void DeleteObjectDefinition(string relative)
    {
        string path = Path.GetFullPath(ResolveEntityFullPath(relative));
        string ownedRoot = Path.GetFullPath(ResourcePath + ".parts") + Path.DirectorySeparatorChar;
        bool owned = path.StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase);
        string contents = File.ReadAllText(path);
        List<string> references = [.. _settings.Entities];
        var placements = ClonePlaced(_nature.PlacedEntities);
        void Apply(bool restore)
        {
            if (owned)
            {
                if (restore) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); }
                else if (File.Exists(path)) File.Delete(path);
            }
            _settings.Entities = restore ? [.. references] : references.Where(item => !string.Equals(item, relative, StringComparison.OrdinalIgnoreCase)).ToList();
            _nature.PlacedEntities = restore ? ClonePlaced(placements)
                : ClonePlaced(placements.Where(item => !string.Equals(item.Entity, relative, StringComparison.OrdinalIgnoreCase)).ToList());
            RefreshComponentsPanel();
            _viewport.Invalidate();
        }
        Apply(false);
        PushEdit("Delete terrain object definition", () => Apply(false), () => Apply(true));
    }
}
