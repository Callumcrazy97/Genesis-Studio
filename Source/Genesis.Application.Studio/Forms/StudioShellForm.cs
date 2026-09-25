using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core;
using Genesis.Application.Core.Editing;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Editing;
using Genesis.Application.Studio.Theme;
using Genesis.Shared.Assets;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Studio.Forms;

public sealed partial class StudioShellForm : DpiAwareForm
{
    private readonly StudioServices _services;
    private readonly ProjectSession _project;
    private readonly ResourceService _resources;
    private readonly EditorDocumentRegistry _editorRegistry = new();
    private readonly bool _persistLayout;
    private readonly DockPanel _dockPanel;
    private readonly ResourceBrowserDock _assetBrowser;
    private readonly InspectorDock _inspector;
    private readonly ConsoleDock _console;
    private readonly WelcomeDocument _welcome;
    private readonly ToolStripStatusLabel _status;
    private readonly ToolStripStatusLabel _projectStatus;
    private readonly ToolStripStatusLabel _renderBackendStatus;
    private ToolStripButton? _validateButton;
    private readonly DeserializeDockContent _deserializeDockContent;
    private readonly System.Windows.Forms.Timer _autoSaveTimer;
    private readonly System.Windows.Forms.Timer _finderDebounce;
    private readonly ResourceSearchService _resourceSearch = new();
    private readonly HashSet<ResourceKind> _finderKinds = [];
    private readonly Dictionary<ResourceKind, ToolStripMenuItem> _finderKindItems = [];
    private readonly Dictionary<string, ImageEditorWindow> _imageEditorWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelComposerWindow> _modelComposerWindows = new(StringComparer.OrdinalIgnoreCase);
    private ToolStripTextBox _finderSearch = null!;
    private ToolStripDropDownButton _finderFilter = null!;
    private ToolStripMenuItem _finderAllTypesItem = null!;
    private ToolStripMenuItem _finderIncludeSubfoldersItem = null!;
    private RuntimeDiagnosticsForm? _profilerWindow;
    private RuntimeDiagnosticsForm? _frameDebuggerWindow;
    private ToolStripMenuItem _finderContentsItem = null!;
    private CancellationTokenSource? _finderCancellation;
    private bool _syncingFinderFilters;
    private bool _finderSearchPending;
    private bool _focusFinderResultsWhenReady;
    private bool _openFinderResultWhenReady;
    private string _appliedFinderTerm = string.Empty;
    private long _finderGeneration;
    private readonly ProjectAssetMonitor _assetMonitor;

    public StudioShellForm(
        StudioServices services,
        ProjectSession project,
        bool persistLayout = true)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        RenderingPreferencesBridge.Apply(_services.Settings.Current);
        RenderingPreferencesBridge.ApplyProject(project.Manifest);
        _persistLayout = persistLayout;
        _resources = new ResourceService(project);
        _assetMonitor = new ProjectAssetMonitor(project.RootPath);
        _assetMonitor.Changed += OnProjectAssetsChanged;
        _assetMonitor.Error += OnAssetMonitorError;
        _finderDebounce = new System.Windows.Forms.Timer { Interval = 220 };
        _finderDebounce.Tick += FinderDebounceTick;

        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1480, 900);
        MinimumSize = new Size(1080, 700);
        StartPosition = FormStartPosition.CenterScreen;
        IsMdiContainer = true;
        KeyPreview = true;
        Text = $"{project.Manifest.Name} — {StudioBuildInfo.WindowLabel}";
        BackColor = ThemeService.Palette.Canvas;
        ForeColor = ThemeService.Palette.Text;
        Icon = Branding.WindowIcon ?? Icon;

        RegisterShellCommands();
        MenuStrip menu = BuildMenu();
        ToolStrip commandBar = BuildCommandBar();
        Panel topChrome = new()
        {
            BackColor = ThemeService.Palette.Surface,
            Dock = DockStyle.Top,
            Height = menu.Height + commandBar.Height,
        };
        menu.Dock = DockStyle.Top;
        commandBar.Dock = DockStyle.Fill;
        topChrome.Controls.Add(commandBar);
        topChrome.Controls.Add(menu);
        Controls.Add(topChrome);
        MainMenuStrip = menu;

        StatusStrip statusStrip = new()
        {
            BackColor = ThemeService.Palette.Surface,
            Renderer = ThemeService.CreateToolStripRenderer(),
            SizingGrip = false,
            ShowItemToolTips = true,
        };
        _status = new ToolStripStatusLabel("Ready")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _projectStatus = new ToolStripStatusLabel(project.Manifest.Name)
        {
            ForeColor = ThemeService.Palette.TextMuted,
        };
        statusStrip.Items.Add(_status);
        _renderBackendStatus = new ToolStripStatusLabel(
            RenderingPreferencesBridge.BackendStatusText(_services.Settings.Current.Rendering))
        {
            ForeColor = ThemeService.Palette.Accent,
        };
        statusStrip.Items.Add(_renderBackendStatus);
        statusStrip.Items.Add(_projectStatus);
        statusStrip.Items.Add(new ToolStripStatusLabel("H" + StudioBuildInfo.Revision)
        {
            Name = "StudioBuildIdentity", ToolTipText = StudioBuildInfo.DiagnosticText,
            ForeColor = ThemeService.Palette.TextMuted,
        });
        Controls.Add(statusStrip);

        _dockPanel = new DockPanel
        {
            BackColor = ThemeService.Palette.Canvas,
            Dock = DockStyle.Fill,
            DockBottomPortion = 0.23,
            DockLeftPortion = 0.20,
            DockRightPortion = 0.22,
            DocumentStyle = DocumentStyle.DockingMdi,
            Theme = ThemeService.CreateDockTheme(),
        };
        _resources.BeforeReferenceEdit = () =>
        {
            if (_dockPanel.Contents.OfType<IStudioDocument>().Any(document => document.IsDirty)
                || _imageEditorWindows.Values.Any(window => !window.IsDisposed && window.IsDirty)
                || _modelComposerWindows.Values.Any(window => !window.IsDisposed && window.IsDirty))
                throw new InvalidOperationException("Save all open resources before renaming. A rename updates references throughout the project.");
        };
        Controls.Add(_dockPanel);
        _dockPanel.BringToFront();
        BackdropSurface.Attach(_dockPanel, ThemeBackdrop.OpenScrim);

        _assetBrowser = new ResourceBrowserDock(_resources, services.Settings);
        _assetBrowser.HostFinderControls(_finderSearch, _finderFilter);
        _inspector = new InspectorDock();
        _inspector.ResourceOpenRequested += (_, resource) => OpenResource(resource);
        _console = new ConsoleDock(services.Log);
        _welcome = new WelcomeDocument(project);
        foreach (GenesisDockContent dock in new GenesisDockContent[] { _assetBrowser, _inspector, _console, _welcome })
            dock.SetProjectShortcutRouter(RouteSharedWindowShortcut);
        _deserializeDockContent = DeserializeDockContent;
        _editorRegistry.Register(ResourceKind.Image, CreateImageViewerDocument);
        RegisterSuiteEditors();

        _assetBrowser.ResourceSelected += (_, args) => _inspector.Inspect(args.Resource);
        _assetBrowser.ResourceSelectionCleared += (_, _) => _inspector.ClearSelection();
        _assetBrowser.FiltersResetRequested += (_, _) => { _finderSearch.Text = string.Empty; ResetFinderFilters(); };
        _inspector.EditRouter = RouteInspectorEdit;
        _inspector.LiveValueProvider = ResolveLiveInspectorValues;
        _inspector.ResourceEdited += OnInspectorResourceEdited;
        _assetBrowser.ResourceOpenRequested += (_, args) => OpenResource(args.Resource);
        _assetBrowser.StatusMessage += (_, message) => SetStatus(message);
        _assetBrowser.FinderInvalidated += OnFinderInvalidated;
        _welcome.ActionRequested += (_, action) => HandleWelcomeAction(action);

        _autoSaveTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _autoSaveTimer.Tick += (_, _) => AutoSaveTick();
        _services.Settings.SettingsChanged += OnSettingsChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        ApplyRuntimePreferences();

        ThemeService.Apply(this);
        menu.Renderer = ThemeService.CreateToolStripRenderer();
        commandBar.Renderer = ThemeService.CreateToolStripRenderer();

        StartCommandStateUpdates();

        Shown += (_, _) => _services.Log.Information(
            "Studio",
            $"Workspace opened for '{_project.Manifest.Name}'.");
    }

    public ProjectSession Project => _project;

    public ResourceBrowserDock AssetBrowser => _assetBrowser;

    internal DockPanel DockPanel => _dockPanel;

    public InspectorDock Inspector => _inspector;

    public IReadOnlyList<ResourceDocument> OpenDocuments =>
        _dockPanel.Contents.OfType<ResourceDocument>().ToArray();

    public IReadOnlyList<IStudioDocument> OpenStudioDocuments =>
        _dockPanel.Contents.OfType<IStudioDocument>().ToArray();

    internal ProjectAssetMonitor AssetMonitor => _assetMonitor;

    public ResourceDocument OpenResourceDocument(ResourceItem resource)
    {
        OpenResource(resource);
        if (resource.Kind == ResourceKind.Image)
        {
            throw new InvalidOperationException(
                "Sprite resources open in the image viewer. Use OpenStudioResource instead.");
        }

        return OpenDocuments.First(
            document => string.Equals(
                document.Resource.FullPath,
                resource.FullPath,
                StringComparison.OrdinalIgnoreCase));
    }

    public IStudioDocument OpenStudioResource(ResourceItem resource)
    {
        OpenResource(resource);
        string identity = ResourceIdentity(resource);
        return OpenStudioDocuments.First(
            document => string.Equals(document.DocumentIdentity, identity, StringComparison.OrdinalIgnoreCase));
    }

    public event EventHandler? CloseProjectRequested;

    public event EventHandler<ProjectSessionEventArgs>? SwitchProjectRequested;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        bool loaded = false;
        if (_persistLayout && File.Exists(ApplicationPaths.LayoutFile))
        {
            try
            {
                _dockPanel.LoadFromXml(ApplicationPaths.LayoutFile, _deserializeDockContent);
                loaded = true;
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException or ArgumentException)
            {
                _services.Log.Warning(
                    "Workspace",
                    $"Saved layout could not be restored: {exception.Message}");
            }
        }

        if (!loaded)
        {
            SetDefaultLayout();
        }
        else if (!_dockPanel.Documents.Any())
        {
            _welcome.Show(_dockPanel, DockState.Document);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyDockThemeIfNeeded();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_persistLayout)
        {
            try
            {
                ApplicationPaths.EnsureUserDirectories();
                _dockPanel.SaveAsXml(ApplicationPaths.LayoutFile);
            }
            catch (IOException exception)
            {
                _services.Log.Warning("Workspace", $"Layout could not be saved: {exception.Message}");
            }
        }

        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        DispatchShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    // Retain the regression seam; keyboard, menus, toolbar and palette now use one catalog.
    internal bool RouteEditCommand(Keys keyData) =>
        (Editing.EditCommandRouter.Map(keyData) is not null || keyData == Keys.F2)
        && DispatchShortcut(keyData);

    private MenuStrip BuildMenu()
    {
        MenuStrip menu = new()
        {
            BackColor = ThemeService.Palette.Surface, Dock = DockStyle.Top,
            Padding = new Padding(8, 3, 8, 3), Renderer = ThemeService.CreateToolStripRenderer(),
        };
        ToolStripMenuItem file = new("&File");
        file.DropDownItems.Add(CommandMenuItem("project.new"));
        file.DropDownItems.Add(CommandMenuItem("project.open"));
        file.DropDownItems.Add(CommandMenuItem("project.close"));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(CommandMenuItem("document.save"));
        file.DropDownItems.Add(CommandMenuItem("project.saveAll"));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(CommandMenuItem("project.run"));
        file.DropDownItems.Add(CommandMenuItem("project.debug"));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(CommandMenuItem("project.export"));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(CommandMenuItem("studio.exit"));
        ToolStripMenuItem edit = new("&Edit");
        foreach (string id in new[] { "edit.undo", "edit.redo" }) edit.DropDownItems.Add(CommandMenuItem(id));
        edit.DropDownItems.Add(new ToolStripSeparator());
        foreach (string id in new[] { "edit.cut", "edit.copy", "edit.paste", "edit.selectAll" }) edit.DropDownItems.Add(CommandMenuItem(id));
        edit.DropDownItems.Add(CommandMenuItem("resource.duplicate", "Duplicate"));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(CommandMenuItem("resource.rename", "Rename"));
        edit.DropDownItems.Add(CommandMenuItem("edit.delete"));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(CommandMenuItem("resource.find"));
        edit.DropDownItems.Add(CommandMenuItem("studio.preferences"));
        ToolStripMenuItem view = new("&View");
        view.DropDownItems.Add(CommandMenuItem("studio.commands"));
        view.DropDownItems.Add(new ToolStripSeparator());
        foreach (string id in new[] { "view.assets", "view.inspector", "view.console", "view.start" }) view.DropDownItems.Add(CommandMenuItem(id));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(CommandMenuItem("view.reset"));
        ToolStripMenuItem tools = new("&Tools");
        tools.DropDownItems.Add(CommandMenuItem("resource.refresh"));
        tools.DropDownItems.Add(CommandMenuItem("resource.newFolder"));
        tools.DropDownItems.Add(CommandMenuItem("project.validate"));
        tools.DropDownItems.Add(new ToolStripSeparator());
        foreach (string id in new[] { "tools.packages", "tools.profiler", "tools.frameDebugger" }) tools.DropDownItems.Add(CommandMenuItem(id));
        ToolStripMenuItem help = new("&Help");
        help.DropDownItems.Add(CommandMenuItem("help.documentation"));
        help.DropDownItems.Add(CommandMenuItem("help.pgsl", "PGSL Command Reference"));
        help.DropDownItems.Add(CommandMenuItem("studio.commands"));
        help.DropDownItems.Add(CommandMenuItem("studio.commands", "Keyboard Shortcuts"));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(CommandMenuItem("help.copyBuildInfo"));
        help.DropDownItems.Add(CommandMenuItem("help.about"));
        menu.Items.AddRange([file, edit, view, tools, help]);
        menu.MenuActivate += (_, _) => _menuContext = CaptureCommandContext();
        foreach (ToolStripMenuItem group in menu.Items.OfType<ToolStripMenuItem>())
            group.DropDownOpening += (_, _) => RefreshCommandBindings(includeMenus: true);
        return menu;
    }

    /// <summary>
    /// Builds the workspace command bar.
    /// </summary>
    /// <remarks>
    /// Grouped by intent rather than separated arbitrarily: Play (Run/Debug), Health (Validate),
    /// History (Undo/Redo), then Save. Two deliberate changes from the first version:
    ///
    /// * <b>Validate is on the bar.</b> It was previously reachable only through Tools, even though
    ///   the Start page presented it as one of three primary actions — and it is the thing worth
    ///   doing *before* Run, which was already here.
    /// * <b>The "Workspace" preset dropdown is gone.</b> It listed Default/2D/3D/Scripting/Profiling
    ///   and its entire handler was <c>SetStatus("Workspace preset: …")</c> — it never reflowed a
    ///   single dock. A control that visibly does nothing when used costs more trust than the space
    ///   it saves. Layout presets can come back when they actually move docks; the original
    ///   per-preset layouts are still described in Part III §7, Phase 0.
    /// </remarks>
    private ToolStrip BuildCommandBar()
    {
        ToolStrip bar = new()
        {
            BackColor = ThemeService.Palette.Surface,
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Height = 44,
            Padding = new Padding(10, 5, 10, 5),
            Renderer = ThemeService.CreateToolStripRenderer(),
        };

        ToolStripButton run = new("▶  Run")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Success,
            Margin = new Padding(0, 0, 2, 0),
            Padding = new Padding(6, 0, 6, 0),
            ToolTipText = "Run project (F5)",
        };
        BindCommandButton(run, "project.run");
        bar.Items.Add(run);

        ToolStripButton debug = new("◆  Debug")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Padding = new Padding(6, 0, 6, 0),
            ToolTipText = "Run with the in-game debugger overlay (F6)",
        };
        BindCommandButton(debug, "project.debug");
        bar.Items.Add(debug);

        bar.Items.Add(new ToolStripSeparator());

        _validateButton = new ToolStripButton("✓  Validate")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Padding = new Padding(6, 0, 6, 0),
            ToolTipText = "Validate the project and list any issues in the Console",
        };
        BindCommandButton(_validateButton, "project.validate");
        bar.Items.Add(_validateButton);

        bar.Items.Add(new ToolStripSeparator());

        ToolStripButton undo = new("↶")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Padding = new Padding(4, 0, 4, 0),
            ToolTipText = "Undo in the active editor (Ctrl+Z)",
        };
        BindCommandButton(undo, "edit.undo");
        bar.Items.Add(undo);

        ToolStripButton redo = new("↷")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Padding = new Padding(4, 0, 4, 0),
            ToolTipText = "Redo in the active editor (Ctrl+Y)",
        };
        BindCommandButton(redo, "edit.redo");
        bar.Items.Add(redo);

        bar.Items.Add(new ToolStripSeparator());

        ToolStripButton save = new("Save Project")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Padding = new Padding(6, 0, 6, 0),
            ToolTipText = "Save every open resource in the project (Ctrl+Shift+S)",
        };
        BindCommandButton(save, "project.saveAll");
        bar.Items.Add(save);

        ToolStripButton commands = new("Commands…") { Alignment = ToolStripItemAlignment.Right };
        BindCommandButton(commands, "studio.commands");
        bar.Items.Add(commands);
        BuildFinderControls();
        return bar;
    }



    /// <summary>
    /// Refreshes the Validate button's issue badge from the project validator.
    /// </summary>
    /// <remarks>
    /// Deliberately called after explicit validation and after a save rather than on a timer or on
    /// every resource change: the validator walks the project, and an ambient badge is not worth
    /// making ordinary editing pay for it.
    /// </remarks>
    private void UpdateValidationBadge(int errors, int warnings)
    {
        if (_validateButton is null)
        {
            return;
        }

        if (errors == 0 && warnings == 0)
        {
            _validateButton.Text = "✓  Validate";
            _validateButton.ForeColor = ThemeService.Palette.Text;
            return;
        }

        _validateButton.Text = errors > 0
            ? $"✓  Validate  ({errors})"
            : $"✓  Validate  ({warnings})";
        _validateButton.ForeColor = errors > 0
            ? ThemeService.Palette.Error
            : ThemeService.Palette.Warning;
    }

    /// <summary>
    /// Builds the Finder's search box and filter menu.
    /// </summary>
    /// <remarks>
    /// Built here but not parented here. These live in the Assets dock, next to the tree their
    /// results appear in — <see cref="ResourceBrowserDock.HostFinderControls"/> adopts them once
    /// that dock exists. The shell keeps every handler, so moving them changed no behaviour.
    /// </remarks>
    private void BuildFinderControls()
    {
        _finderFilter = new ToolStripDropDownButton("Filter")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Name = "ResourceFinderFilter",
            ToolTipText = "Choose resource types, recursion, and whether to search authored content",
        };
        BuildFinderFilterMenu();

        _finderSearch = new ToolStripTextBox
        {
            AutoSize = false,
            BorderStyle = BorderStyle.None,
            Name = "ResourceFinderSearch",
            Size = new Size(260, 28),
            ToolTipText = "Find resources by name, folder or library tags (Ctrl+F). Use tag:forest and -tag:ui for exact tag filters.",
        };
        _finderSearch.TextBox.PlaceholderText = "Find resources…";
        _finderSearch.TextChanged += (_, _) =>
        {
            _focusFinderResultsWhenReady = false;
            _openFinderResultWhenReady = false;
            QueueFinderSearch();
        };
        _finderSearch.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Escape)
            {
                _finderSearch.Clear();
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
            else if (args.KeyCode == Keys.Down)
            {
                UseFinderResults(open: false);
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
            else if (args.KeyCode == Keys.Enter)
            {
                UseFinderResults(open: true);
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
        };
    }

    private void BuildFinderFilterMenu()
    {
        _finderFilter.DropDownItems.Add(new ToolStripLabel("SCOPE") { Enabled = false });
        _finderIncludeSubfoldersItem = new ToolStripMenuItem("Include subfolders")
        {
            CheckOnClick = true,
            Checked = true,
            Name = "FinderIncludeSubfolders",
        };
        _finderContentsItem = new ToolStripMenuItem("Search inside resources")
        {
            CheckOnClick = true,
            Name = "FinderSearchContents",
            ToolTipText = "Search bounded text, JSON, shaders, notes, PGSL, and Object event scripts",
        };
        _finderIncludeSubfoldersItem.CheckedChanged += (_, _) => FinderOptionsChanged();
        _finderContentsItem.CheckedChanged += (_, _) => FinderOptionsChanged();
        _finderFilter.DropDownItems.Add(_finderIncludeSubfoldersItem);
        _finderFilter.DropDownItems.Add(_finderContentsItem);
        _finderFilter.DropDownItems.Add(new ToolStripSeparator());
        _finderFilter.DropDownItems.Add(new ToolStripLabel("RESOURCE TYPES") { Enabled = false });

        _finderAllTypesItem = new ToolStripMenuItem("All resource types")
        {
            CheckOnClick = true,
            Checked = true,
            Name = "FinderAllResourceTypes",
        };
        _finderAllTypesItem.CheckedChanged += (_, _) => AllFinderTypesChanged();
        _finderFilter.DropDownItems.Add(_finderAllTypesItem);

        AddFinderKindItem(ResourceKind.Folder, "Folders");
        foreach (ResourceDefinition definition in ResourceDefinitions.All)
        {
            AddFinderKindItem(definition.Kind, definition.DisplayName);
        }

        _finderFilter.DropDownItems.Add(new ToolStripSeparator());
        _finderFilter.DropDownItems.Add("Reset filters", null, (_, _) => ResetFinderFilters());
        _finderFilter.DropDown.Closing += (_, args) =>
        {
            // Finder filters are a checklist, not a command menu. Keep it open while several kinds
            // are selected; clicking elsewhere or the button again still closes it normally.
            if (args.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
            {
                args.Cancel = true;
            }
        };
        UpdateFinderFilterSummary();
    }

    private void AddFinderKindItem(ResourceKind kind, string displayName)
    {
        ToolStripMenuItem item = new(displayName)
        {
            CheckOnClick = true,
            Name = "FinderType" + kind,
            Tag = kind,
        };
        item.CheckedChanged += (_, _) => FinderKindChanged(kind, item.Checked);
        _finderKindItems.Add(kind, item);
        _finderFilter.DropDownItems.Add(item);
    }

    private void AllFinderTypesChanged()
    {
        if (_syncingFinderFilters)
        {
            return;
        }

        _syncingFinderFilters = true;
        try
        {
            if (_finderAllTypesItem.Checked)
            {
                _finderKinds.Clear();
                foreach (ToolStripMenuItem item in _finderKindItems.Values)
                {
                    item.Checked = false;
                }
            }
            else if (_finderKinds.Count == 0)
            {
                _finderAllTypesItem.Checked = true;
            }
        }
        finally
        {
            _syncingFinderFilters = false;
        }

        FinderOptionsChanged();
    }

    private void FinderKindChanged(ResourceKind kind, bool selected)
    {
        if (_syncingFinderFilters)
        {
            return;
        }

        if (selected)
        {
            _finderKinds.Add(kind);
        }
        else
        {
            _finderKinds.Remove(kind);
        }

        _syncingFinderFilters = true;
        try
        {
            _finderAllTypesItem.Checked = _finderKinds.Count == 0;
        }
        finally
        {
            _syncingFinderFilters = false;
        }

        FinderOptionsChanged();
    }

    private void ResetFinderFilters()
    {
        _syncingFinderFilters = true;
        try
        {
            _finderKinds.Clear();
            _finderAllTypesItem.Checked = true;
            _finderIncludeSubfoldersItem.Checked = true;
            _finderContentsItem.Checked = false;
            foreach (ToolStripMenuItem item in _finderKindItems.Values)
            {
                item.Checked = false;
            }
        }
        finally
        {
            _syncingFinderFilters = false;
        }

        FinderOptionsChanged();
    }

    private void FinderOptionsChanged()
    {
        if (_syncingFinderFilters)
        {
            return;
        }

        UpdateFinderFilterSummary();
        QueueFinderSearch(runImmediately: true);
    }

    private void UpdateFinderFilterSummary()
    {
        int activeOptions = (_finderKinds.Count > 0 ? 1 : 0)
                            + (_finderIncludeSubfoldersItem.Checked ? 0 : 1)
                            + (_finderContentsItem.Checked ? 1 : 0);
        _finderFilter.Text = activeOptions == 0 ? "Filter" : $"Filter ({activeOptions})";
        string types = _finderKinds.Count == 0
            ? "all resource types"
            : string.Join(", ", _finderKinds.OrderBy(kind => kind).Select(kind => kind.ToString()));
        _finderFilter.ToolTipText = $"Search {types}; "
                                    + (_finderIncludeSubfoldersItem.Checked ? "include" : "exclude")
                                    + " subfolders; "
                                    + (_finderContentsItem.Checked ? "include" : "exclude")
                                    + " authored content";
    }

    /// <summary>Puts the caret in the Finder, revealing the Assets dock if it is closed.</summary>
    /// <remarks>
    /// The search box used to sit in the command bar, which is always on screen, so Ctrl+F could
    /// simply focus it. It lives in the Assets dock now, and a dock the user has closed or
    /// auto-hidden cannot take focus — Ctrl+F would silently do nothing.
    /// </remarks>
    private void FocusFinder()
    {
        if (_assetBrowser.DockState == DockState.Hidden || _assetBrowser.DockPanel is null)
        {
            _assetBrowser.Show(_dockPanel, DockState.DockLeft);
        }
        else
        {
            _assetBrowser.Activate();
        }

        _finderSearch.Focus();
        _finderSearch.SelectAll();
    }

    private void QueueFinderSearch(bool runImmediately = false)
    {
        _finderDebounce.Stop();
        _finderCancellation?.Cancel();
        if (string.IsNullOrWhiteSpace(_finderSearch.Text))
        {
            _finderGeneration++;
            _finderSearchPending = false;
            _appliedFinderTerm = string.Empty;
            _assetBrowser.ClearFinderResults();
            _status.Text = "Ready";
            return;
        }

        _finderSearchPending = true;
        _finderDebounce.Interval = runImmediately ? 1 : 220;
        _finderDebounce.Start();
    }

    private void UseFinderResults(bool open)
    {
        string term = _finderSearch.Text.Trim();
        if (term.Length == 0)
        {
            if (!open)
            {
                EnsureFinderResultsVisible(restoreFinderFocus: false);
                _assetBrowser.FocusResults();
            }

            return;
        }

        bool current = !_finderSearchPending
                       && string.Equals(term, _appliedFinderTerm, StringComparison.Ordinal);
        if (current)
        {
            EnsureFinderResultsVisible(restoreFinderFocus: false);
            if (open)
            {
                _assetBrowser.OpenSelectedResult();
            }
            else
            {
                _assetBrowser.FocusResults();
            }

            return;
        }

        _openFinderResultWhenReady = open;
        _focusFinderResultsWhenReady = !open;
        QueueFinderSearch(runImmediately: true);
    }

    private async void FinderDebounceTick(object? sender, EventArgs e)
    {
        _finderDebounce.Stop();
        _finderCancellation?.Cancel();
        _finderCancellation?.Dispose();
        _finderCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _finderCancellation.Token;
        long generation = ++_finderGeneration;
        string term = _finderSearch.Text.Trim();
        if (term.Length == 0)
        {
            _assetBrowser.ClearFinderResults();
            return;
        }

        if (ResourceLibraryQuery.Parse(term).Error is { } queryError)
        {
            _finderSearchPending = false;
            _appliedFinderTerm = term;
            _openFinderResultWhenReady = false;
            _focusFinderResultsWhenReady = false;
            _assetBrowser.ApplyFinderResults(term, new ResourceSearchResponse([], 0, 0, false));
            _status.Text = "Finder: " + queryError;
            return;
        }

        _status.Text = _finderContentsItem.Checked
            ? $"Searching resource names and content for ‘{term}’…"
            : $"Searching resources for ‘{term}’…";
        try
        {
            ResourceSearchResponse response = await _resourceSearch.SearchAsync(
                _assetBrowser.ResourceTreeSnapshot,
                BuildFinderQuery(term),
                cancellationToken);
            if (generation != _finderGeneration || cancellationToken.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            ApplyFinderResponse(term, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer keystroke owns the result surface; the superseded scan must not repaint it.
        }
        catch (Exception exception)
        {
            // Finder must remain advisory when files are being replaced externally. Unexpected
            // failures are logged with context rather than taking down the Studio message loop.
            _services.Log.Warning("Finder", $"Search for '{term}' failed: {exception.Message}");
            _status.Text = $"Search failed: {exception.Message}";
            if (generation == _finderGeneration)
            {
                _finderSearchPending = false;
                _appliedFinderTerm = string.Empty;
                _focusFinderResultsWhenReady = false;
                _openFinderResultWhenReady = false;
                _assetBrowser.ClearFinderResults();
            }
        }
    }

    private ResourceSearchQuery BuildFinderQuery(string term) => new()
    {
        Term = term,
        ScopePath = _resources.AssetsRoot,
        IncludeSubfolders = _finderIncludeSubfoldersItem.Checked,
        SearchContents = _finderContentsItem.Checked,
        Kinds = _finderKinds.ToArray(),
    };

    private void ApplyFinderResponse(string term, ResourceSearchResponse response)
    {
        bool open = _openFinderResultWhenReady;
        bool focus = _focusFinderResultsWhenReady;
        _appliedFinderTerm = term;
        _finderSearchPending = false;
        _assetBrowser.ApplyFinderResults(term, response);
        EnsureFinderResultsVisible(restoreFinderFocus: !open && !focus);

        string suffix = response.Truncated ? "+ (result limit reached)" : string.Empty;
        string skipped = response.SkippedContentFiles > 0
            ? $" · {response.SkippedContentFiles} bounded or unavailable file(s) skipped"
            : string.Empty;
        _status.Text = $"Finder: {response.Results.Count}{suffix} result(s) for ‘{term}’{skipped}.";

        _openFinderResultWhenReady = false;
        _focusFinderResultsWhenReady = false;
        if (open)
        {
            _assetBrowser.OpenSelectedResult();
        }
        else if (focus)
        {
            _assetBrowser.FocusResults();
        }
    }

    private void EnsureFinderResultsVisible(bool restoreFinderFocus)
    {
        bool finderHadFocus = restoreFinderFocus && _finderSearch.TextBox.Focused;
        if (_assetBrowser.DockState == DockState.Hidden || _assetBrowser.DockPanel is null)
        {
            _assetBrowser.Show(_dockPanel, DockState.DockLeft);
            if (finderHadFocus && IsHandleCreated)
            {
                BeginInvoke(() => _finderSearch.Focus());
            }
        }
    }

    private void OnFinderInvalidated(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_finderSearch.Text))
        {
            QueueFinderSearch(runImmediately: true);
        }
    }

    internal ToolStripTextBox FinderSearchBox => _finderSearch;

    internal ToolStripDropDownButton FinderFilterButton => _finderFilter;

    internal bool FinderResultsCurrent =>
        !_finderSearchPending
        && string.Equals(
            _finderSearch.Text.Trim(),
            _appliedFinderTerm,
            StringComparison.Ordinal);

    internal void UseFinderResultsForTest(bool open) => UseFinderResults(open);

    /// <summary>Drives a shell shortcut the way <see cref="ProcessCmdKey"/> would.</summary>
    /// <remarks>
    /// Lets the gate assert that Ctrl+F still reaches the Finder now that it lives in a dock the
    /// user can close, without having to synthesise a real key message.
    /// </remarks>
    internal void RouteShortcutForTest(Keys keyData)
    {
        Message message = default;
        ProcessCmdKey(ref message, keyData);
    }

    /// <summary>Deterministic seam that drives the same query and result surface without a timer.</summary>
    internal ResourceSearchResponse RunFinderSearchForTest(
        string term,
        bool includeSubfolders,
        bool searchContents,
        params ResourceKind[] kinds)
    {
        _finderDebounce.Stop();
        _finderCancellation?.Cancel();
        _finderSearchPending = false;
        _appliedFinderTerm = string.Empty;
        _focusFinderResultsWhenReady = false;
        _openFinderResultWhenReady = false;
        _syncingFinderFilters = true;
        try
        {
            _finderKinds.Clear();
            _finderKinds.UnionWith(kinds);
            _finderAllTypesItem.Checked = _finderKinds.Count == 0;
            _finderIncludeSubfoldersItem.Checked = includeSubfolders;
            _finderContentsItem.Checked = searchContents;
            foreach ((ResourceKind kind, ToolStripMenuItem item) in _finderKindItems)
            {
                item.Checked = _finderKinds.Contains(kind);
            }
        }
        finally
        {
            _syncingFinderFilters = false;
        }

        UpdateFinderFilterSummary();
        _finderSearch.Text = term;
        _finderDebounce.Stop();
        ResourceSearchResponse response = _resourceSearch.Search(
            _assetBrowser.ResourceTreeSnapshot,
            BuildFinderQuery(term));
        ApplyFinderResponse(term.Trim(), response);
        return response;
    }

    /// <summary>Returns the deterministic test seam to the same idle state a user gets from Reset.</summary>
    internal void ResetFinderForTest()
    {
        _finderDebounce.Stop();
        _finderCancellation?.Cancel();
        _syncingFinderFilters = true;
        try
        {
            _finderKinds.Clear();
            _finderAllTypesItem.Checked = true;
            _finderIncludeSubfoldersItem.Checked = true;
            _finderContentsItem.Checked = false;
            foreach (ToolStripMenuItem item in _finderKindItems.Values)
            {
                item.Checked = false;
            }
        }
        finally
        {
            _syncingFinderFilters = false;
        }

        UpdateFinderFilterSummary();
        _finderSearch.Clear();
        _finderDebounce.Stop();
        _assetBrowser.ClearFinderResults();
        _status.Text = "Ready";
    }

    private void SetDefaultLayout()
    {
        _dockPanel.SuspendLayout(true);
        _assetBrowser.Show(_dockPanel, DockState.DockLeft);
        _assetBrowser.DockHandler.Pane?.SetContentIndex(_assetBrowser, 0);
        _inspector.Show(_dockPanel, DockState.DockRight);
        _console.Show(_dockPanel, DockState.DockBottom);
        _welcome.Show(_dockPanel, DockState.Document);
        _dockPanel.ResumeLayout(true, true);
    }

    private IDockContent? DeserializeDockContent(string persistString)
    {
        if (persistString == typeof(ResourceBrowserDock).FullName)
        {
            return _assetBrowser;
        }

        if (persistString == typeof(InspectorDock).FullName)
        {
            return _inspector;
        }

        if (persistString == typeof(ConsoleDock).FullName)
        {
            return _console;
        }

        if (persistString == typeof(WelcomeDocument).FullName)
        {
            return _welcome;
        }

        return null;
    }

    private void ShowToolWindow(GenesisDockContent content)
    {
        if (content.DockState == DockState.Hidden || content.DockPanel is null)
        {
            content.Show(_dockPanel);
        }
        else
        {
            content.Activate();
        }
    }

    private void ShowStartPage()
    {
        if (_welcome.IsDisposed)
        {
            return;
        }

        if (_welcome.DockPanel is null)
        {
            _welcome.Show(_dockPanel, DockState.Document);
        }
        else
        {
            _welcome.Activate();
        }
    }

    private void OpenLinkedResourceFromEditor(string path, bool editModel = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full = Path.IsPathRooted(path) ? path : Path.Combine(_project.RootPath, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full) || ResourceDefinitions.FromPath(full) is not { } definition)
        {
            return;
        }

        var linked = new ResourceItem
        {
            Name = ResourceDisplayName.Format(full),
            FullPath = full,
            RelativePath = Path.GetRelativePath(_project.RootPath, full).Replace('\\', '/'),
            Kind = definition.Kind,
            IsFolder = false,
        };
        if (editModel && linked.Kind == ResourceKind.Model) OpenModelComposer(linked);
        else OpenResource(linked);
    }

    private void OpenResource(ResourceItem resource)
    {
        // Terrain Entities are authored through a modal wizard (matches the create/edit flow
        // in the Terrain Editor's own library panel), not a dock document — there's nothing
        // useful to keep open in the workspace once the dialog closes.
        if (resource.Kind == ResourceKind.TerrainEntity)
        {
            using Genesis.Application.Editors.Suite.Terrain.TerrainEntityWizardDialog wizard = new(
                resource.FullPath, _project.RootPath, presetType: null);
            wizard.ShowDialog(this);
            SetStatus($"Edited {resource.Name}");
            _assetBrowser.RememberOpened(resource);
            return;
        }

        IStudioDocument? existing = _dockPanel.Contents
            .OfType<IStudioDocument>()
            .FirstOrDefault(
                document => string.Equals(
                    document.DocumentIdentity,
                    ResourceIdentity(resource),
                    StringComparison.OrdinalIgnoreCase));
        if (existing is GenesisDockContent existingContent)
        {
            existingContent.Activate();
            _assetBrowser.RememberOpened(resource);
            return;
        }

        GenesisDockContent document = _editorRegistry.Create(
            resource,
            item => new ResourceDocument(item, _services.Log));
        document.SetProjectShortcutRouter(RouteSharedWindowShortcut);
        document.Show(_dockPanel, DockState.Document);
        SetStatus($"Opened {resource.Name}");
        _assetBrowser.RememberOpened(resource);
    }

    private static string ResourceIdentity(ResourceItem resource) =>
        resource.AssetId != Guid.Empty
            ? resource.AssetId.ToString("N")
            : Path.GetFullPath(resource.FullPath);

    private GenesisDockContent CreateImageViewerDocument(ResourceItem resource)
    {
        ImageViewerDocument document = new(resource, _services.Log);
        document.BindTextureGroups(
            () => TextureGroupCatalog.Names(_project.Manifest),
            (name, atlasSize) =>
            {
                if (!TextureGroupCatalog.TryAdd(_project.Manifest, name, atlasSize, out string error))
                    return error;
                _services.Projects.Save(_project);
                return null;
            },
            name =>
            {
                TextureGroupDeleteResult result = TextureGroupCatalog.TryDelete(
                    _project, _services.Projects, name);
                return result.Succeeded ? null : result.Error;
            });
        document.EditorRequested += (_, args) => OpenImageEditor(args);
        document.InspectorStateChanged += (_, _) => RefreshInspectorDeferred(resource);
        return document;
    }

    private void RefreshInspectorDeferred(ResourceItem resource)
    {
        if (_inspector.IsDisposed) return;
        if (_inspector.IsHandleCreated)
        {
            _inspector.BeginInvoke(new Action(() => _inspector.RefreshLiveValues(resource)));
        }
        else
        {
            _inspector.RefreshLiveValues(resource);
        }
    }

    /// <summary>Routes every specialised resource kind to its suite editor surface.</summary>
    private void RegisterSuiteEditors()
    {
        SuiteChromeBridge.Push();
        string root = _project.RootPath;

        GenesisDockContent Wrap(
            ResourceItem resource,
            Func<string, string, Genesis.Application.Editors.Suite.IEditorSurface> factory,
            string title)
        {
            Genesis.Application.Editors.Suite.IEditorSurface Create() => factory(resource.FullPath, root);
            SuiteEditorDocument document = new(resource, _services.Log, Create(), title, Create);
            document.InspectorStateChanged += (_, _) => RefreshInspectorDeferred(resource);
            document.OpenLinkedResourceRequested += (_, path) => OpenLinkedResourceFromEditor(path);
            return document;
        }

        _editorRegistry.Register(ResourceKind.Room, resource => Wrap(
            resource, (path, projectRoot) => new Genesis.Application.Editors.Suite.Rooms.RoomEditorControl(path, projectRoot), "Room Editor"));
        _editorRegistry.Register(ResourceKind.Terrain, resource => Wrap(
            resource, (path, projectRoot) => new Genesis.Application.Editors.Suite.Terrain.TerrainEditorControl(path, projectRoot), "Terrain Editor"));
        _editorRegistry.Register(ResourceKind.GameObject, resource => Wrap(
            resource, (path, projectRoot) => new Genesis.Application.Editors.Suite.Objects.ObjectEditorControl(path, projectRoot), "Object Editor"));
        _editorRegistry.Register(ResourceKind.PgslScript, resource => Wrap(
            resource, (path, projectRoot) => new Genesis.Application.Editors.Suite.Scripts.PgslScriptEditorControl(path, projectRoot), "PGSL Editor"));
        _editorRegistry.Register(ResourceKind.Audio, resource => Wrap(
            resource, (path, projectRoot) => new Genesis.Application.Editors.Suite.Assets.AudioEditorControl(path, projectRoot), "Audio Editor"));
        _editorRegistry.Register(ResourceKind.Shader, resource => Wrap(
            resource, (path, projectRoot) => new Genesis.Application.Editors.Suite.Assets.ShaderEditorControl(path, projectRoot), "Shader Editor"));
        _editorRegistry.Register(ResourceKind.UserInterface, resource => Wrap(
            resource, (path, projectRoot) => new Genesis.Application.Editors.Suite.Assets.UiEditorControl(path, projectRoot), "UI Editor"));
        _editorRegistry.Register(ResourceKind.Model, resource => Wrap(
            resource,
            (path, projectRoot) =>
            {
                Genesis.Application.Editors.Suite.Assets.ModelViewerControl viewer = new(
                    path,
                    projectRoot);
                viewer.ComposeRequested += (_, _) => OpenModelComposer(resource);
                return viewer;
            },
            "Model Viewer"));
        _editorRegistry.Register(ResourceKind.Particle, resource => Wrap(
            resource, (path, projectRoot) => new Genesis.Application.Editors.Suite.Assets.ParticleEditorControl(path, projectRoot), "Particle Editor"));
        _editorRegistry.Register(ResourceKind.Physics, resource => Wrap(
            resource, (path, projectRoot) => new Genesis.Application.Editors.Suite.Assets.PhysicsEditorControl(path, projectRoot), "Physics Editor"));
        _editorRegistry.Register(ResourceKind.Pathing, resource => Wrap(
            resource, (path, projectRoot) =>
            {
                Genesis.Application.Editors.Suite.Assets.PathingEditorControl editor = new(path, projectRoot);
                editor.DebugRequested += (_, args) => RunProject(
                    debug: true,
                    roomOverride: args.TargetRoom,
                    additionalEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [Genesis.Runtime.Debugger.NavigationDebugTelemetry.InitialPanelEnvironmentVariable] = Genesis.Runtime.Debugger.NavigationDebugTelemetry.AiNavigationPanelValue,
                        [Genesis.Runtime.Debugger.NavigationDebugTelemetry.DebugPathingEnvironmentVariable] = args.PathingAsset,
                    });
                return editor;
            }, "Pathing & Navigation Editor"));
        _editorRegistry.Register(ResourceKind.Note, resource => Wrap(
            resource, (path, projectRoot) => new Genesis.Application.Editors.Suite.Assets.NoteEditorControl(path, projectRoot), "Note Editor"));
    }

    internal void OpenImageEditor(ImageEditorRequestedEventArgs args)
    {
        string identity = ImageViewerDocument.Identity(args.Resource) + ":editor";
        if (_imageEditorWindows.TryGetValue(identity, out ImageEditorWindow? existing)
            && !existing.IsDisposed)
        {
            existing.Activate();
            existing.BringToFront();
            _assetBrowser.RememberOpened(args.Resource);
            return;
        }

        ImageEditorWindow editor = new(_services.Log, args.Resource, args.Session, args.Workspace);
        editor.SetProjectShortcutRouter(RouteSharedWindowShortcut);
        editor.FormClosed += (_, _) => _imageEditorWindows.Remove(identity);
        _imageEditorWindows[identity] = editor;
        editor.Show(this);
        SetStatus($"Editing {args.Resource.Name} in Image Editor window");
        _assetBrowser.RememberOpened(args.Resource);
    }

    private void OpenModelComposer(ResourceItem resource)
    {
        foreach (SuiteEditorDocument document in _dockPanel.Contents.OfType<SuiteEditorDocument>())
        {
            if (string.Equals(
                    Path.GetFullPath(document.Resource.FullPath),
                    Path.GetFullPath(resource.FullPath),
                    StringComparison.OrdinalIgnoreCase)
                && document.IsDirty)
            {
                document.Save();
            }
        }

        string identity = ModelComposerWindowIdentity(resource);
        if (_modelComposerWindows.TryGetValue(identity, out ModelComposerWindow? existing)
            && !existing.IsDisposed)
        {
            existing.Activate();
            existing.BringToFront();
            _assetBrowser.RememberOpened(resource);
            return;
        }

        ModelComposerWindow composer = new(_services.Log, resource, _project.RootPath);
        composer.Editor.OpenLinkedResourceRequested += (_, path) => OpenLinkedResourceFromEditor(path, editModel: true);
        composer.SetProjectShortcutRouter(RouteSharedWindowShortcut);
        composer.Saved += (_, _) => RefreshModelViewer(resource);
        composer.FormClosed += (_, _) => _modelComposerWindows.Remove(identity);
        _modelComposerWindows[identity] = composer;
        composer.Show(this);
        SetStatus($"Editing {resource.Name} in Model Composer");
        _assetBrowser.RememberOpened(resource);
    }

    private void RefreshModelViewer(ResourceItem resource)
    {
        foreach (SuiteEditorDocument document in _dockPanel.Contents.OfType<SuiteEditorDocument>())
        {
            if (string.Equals(
                    Path.GetFullPath(document.Resource.FullPath),
                    Path.GetFullPath(resource.FullPath),
                    StringComparison.OrdinalIgnoreCase)
                && document.Surface is Genesis.Application.Editors.Suite.Assets.ModelViewerControl)
            {
                document.ReloadSurface();
                if (!_inspector.IsDisposed)
                {
                    _inspector.RefreshLiveValues(resource);
                }

                return;
            }
        }
    }

    private static string ModelComposerWindowIdentity(ResourceItem resource) =>
        (resource.AssetId != Guid.Empty
            ? resource.AssetId.ToString("N")
            : Path.GetFullPath(resource.FullPath))
        + ":composer";

    private void OnAssetMonitorError(object? sender, Exception error)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => OnAssetMonitorError(sender, error)); return; }
        _services.Log.Error("Asset updates", "Could not refresh project assets.", error);
        ShowToolWindow(_console);
        SetStatus("Asset refresh failed: " + error.Message);
    }

    private void OnProjectAssetsChanged(object? sender, ProjectAssetChangeSet changes)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => OnProjectAssetsChanged(sender, changes));
            return;
        }

        int rebound = 0;
        foreach (IStudioDocument document in _dockPanel.Contents.OfType<IStudioDocument>().ToArray())
        {
            if (!changes.Affects(document.Resource.FullPath)) continue;
            document.Resource.Name = ResourceNames.Name(_project.RootPath, document.Resource.FullPath);
            document.HandleAssetChanges(changes);
            rebound++;
        }
        foreach (ImageEditorWindow editor in _imageEditorWindows.Values.ToArray())
            editor.HandleAssetChanges(changes);
        foreach (ModelComposerWindow composer in _modelComposerWindows.Values.ToArray())
            composer.HandleAssetChanges(changes);
        _inspector.HandleAssetChanges(changes);

        _services.Log.Information(
            "Live Reload",
            $"Generation {changes.Generation}: {changes.ChangedPaths.Count} changed, " +
            $"{changes.AffectedPaths.Count} affected, {rebound} open preview(s) rebound.");
        SetStatus($"Live reload · {changes.ChangedPaths.Count} changed · {rebound} preview(s) refreshed");
    }

    private bool RouteInspectorEdit(ResourceInspectorEditRequest request)
    {
        foreach (IStudioDocument openDocument in _dockPanel.Contents.OfType<IStudioDocument>())
        {
            if (!string.Equals(Path.GetFullPath(openDocument.Resource.FullPath),
                    Path.GetFullPath(request.Resource.FullPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (openDocument is ILiveResourceInspectorTarget liveDocument
                && request.PropertyPath.StartsWith("Runtime.", StringComparison.OrdinalIgnoreCase)
                && liveDocument.TryApplyLiveInspectorValue(request.PropertyPath, request.Value))
            {
                return true;
            }

            if (openDocument is IResourceInspectorTarget documentTarget
                && documentTarget.TryApplyInspectorValue(request.PropertyPath, request.Value))
            {
                RefreshMatchingImageEditor(request.Resource);
                return true;
            }
        }

        foreach (SuiteEditorDocument document in _dockPanel.Contents
                     .OfType<SuiteEditorDocument>())
        {
            if (!string.Equals(Path.GetFullPath(document.Resource.FullPath),
                    Path.GetFullPath(request.Resource.FullPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (document.Surface is ILiveResourceInspectorTarget live
                && request.PropertyPath.StartsWith("Runtime.", StringComparison.OrdinalIgnoreCase)
                && live.TryApplyLiveInspectorValue(request.PropertyPath, request.Value))
            {
                return true;
            }

            if (document.Surface is IResourceInspectorTarget target)
            {
                return target.TryApplyInspectorValue(request.PropertyPath, request.Value);
            }
        }

        return false;
    }

    private void RefreshMatchingImageEditor(ResourceItem resource)
    {
        string identity = ImageViewerDocument.Identity(resource) + ":editor";
        if (_imageEditorWindows.TryGetValue(identity, out ImageEditorWindow? editor)
            && !editor.IsDisposed)
        {
            editor.Editor.RefreshFromDocument();
        }
    }

    private IReadOnlyList<ResourceInspectorLiveValue> ResolveLiveInspectorValues(ResourceItem resource)
    {
        foreach (IStudioDocument openDocument in _dockPanel.Contents.OfType<IStudioDocument>())
        {
            if (string.Equals(
                    Path.GetFullPath(openDocument.Resource.FullPath),
                    Path.GetFullPath(resource.FullPath),
                    StringComparison.OrdinalIgnoreCase)
                && openDocument is ILiveResourceInspectorTarget liveDocument)
            {
                return liveDocument.GetLiveInspectorValues();
            }
        }

        foreach (SuiteEditorDocument document in _dockPanel.Contents.OfType<SuiteEditorDocument>())
        {
            if (string.Equals(
                    Path.GetFullPath(document.Resource.FullPath),
                    Path.GetFullPath(resource.FullPath),
                    StringComparison.OrdinalIgnoreCase)
                && document.Surface is ILiveResourceInspectorTarget live)
            {
                return live.GetLiveInspectorValues();
            }
        }

        return [];
    }

    private void OnInspectorResourceEdited(object? sender, ResourceInspectorEditedEventArgs args)
    {
        if (args.Persisted)
        {
            _assetMonitor.Notify(args.Resource.FullPath);
            SetStatus($"Inspector saved {args.Resource.Name} · {args.PropertyPath}");
        }
        else
        {
            SetStatus($"Inspector updated open {args.Resource.Name} · {args.PropertyPath}");
        }
    }

    private void CreateProject()
    {
        using NewProjectDialog dialog = new();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            ProjectSession session = _services.Projects.CreateProject(
                dialog.ParentDirectory,
                dialog.ProjectName,
                dialog.SelectedTemplate,
                dialog.OverwriteExisting);
            _services.Settings.AddRecentProject(session.Manifest.Name, session.ProjectFile);
            SwitchProjectRequested?.Invoke(this, new ProjectSessionEventArgs(session));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError("Could not create project", exception);
        }
    }

    private void OpenProject()
    {
        using OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            Filter = "Genesis Project (*.genesisproj)|*.genesisproj|All files (*.*)|*.*",
            RestoreDirectory = true,
            Title = "Open Genesis Project",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            ProjectSession session = _services.Projects.OpenProject(dialog.FileName);
            _services.Settings.AddRecentProject(session.Manifest.Name, session.ProjectFile);
            SwitchProjectRequested?.Invoke(this, new ProjectSessionEventArgs(session));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError("Could not open project", exception);
        }
    }

    private void CloseProject()
    {
        CloseProjectRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ShowPreferences()
    {
        using PreferencesForm preferences = new(_services.Settings, _project);
        if (preferences.ShowDialog(this) == DialogResult.OK)
        {
            SetStatus("Preferences applied.");
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        ApplyRuntimePreferences();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        ApplyDockThemeIfNeeded();
        _dockPanel.BackColor = ThemeService.Palette.Canvas;
        BackdropSurface.Refresh(this);
        ThemeService.Apply(this);
        ThemeService.Apply(_assetBrowser);
        ThemeService.Apply(_inspector);
        ThemeService.Apply(_console);
        menuRendererRefresh();

        // Live theme into open documents: suite editors restyle via the chrome bridge; every
        // other docked document gets a targeted Apply pass (the shell-level walk stops at the
        // DockPanel, so documents were previously skipped — NEXT gap "theme into documents").
        SuiteChromeBridge.Push();
        foreach (WeifenLuo.WinFormsUI.Docking.IDockContent content in _dockPanel.Contents.ToArray())
        {
            if (content is GenesisDockContent dock && dock is not SuiteEditorDocument && !dock.IsDisposed)
            {
                ThemeService.Apply(dock);
            }
        }
    }

    private void ApplyDockThemeIfNeeded()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        bool wantLight = !ThemeService.Palette.IsDark;
        bool hasLight = _dockPanel.Theme is VS2015LightTheme;
        if (wantLight != hasLight || _dockPanel.Theme is null)
        {
            _dockPanel.Theme = ThemeService.CreateDockTheme();
            return;
        }

        GenesisDockTheme.Remap(_dockPanel.Theme, ThemeService.Palette);
        _dockPanel.DockBackColor = ThemeService.Palette.Canvas;
        _dockPanel.Invalidate(true);
    }

    private void menuRendererRefresh()
    {
        if (MainMenuStrip is not null)
        {
            MainMenuStrip.Renderer = ThemeService.CreateToolStripRenderer();
        }

        foreach (Control control in Controls)
        {
            if (control is ToolStrip strip)
            {
                strip.Renderer = ThemeService.CreateToolStripRenderer();
            }
            else if (control is Panel panel)
            {
                foreach (Control child in panel.Controls)
                {
                    if (child is ToolStrip nested)
                    {
                        nested.Renderer = ThemeService.CreateToolStripRenderer();
                    }
                }
            }
        }
    }

    private void ApplyRuntimePreferences()
    {
        RenderingPreferencesBridge.Apply(_services.Settings.Current);
        _renderBackendStatus.Text =
            RenderingPreferencesBridge.BackendStatusText(_services.Settings.Current.Rendering);
        _renderBackendStatus.ForeColor =
            Genesis.Rendering.Core.RenderBackendSelection.IsFallbackActive
                ? ThemeService.Palette.Warning
                : ThemeService.Palette.Accent;

        EditingSettings editing = _services.Settings.Current.Editing;
        _autoSaveTimer.Stop();
        if (editing.AutoSave)
        {
            _autoSaveTimer.Interval = Math.Max(15_000, editing.AutoSaveMinutes * 60_000);
            _autoSaveTimer.Start();
        }
    }

    private void AutoSaveTick()
    {
        if (!_services.Settings.Current.Editing.AutoSave || _documentSaves.IsSaving || _paletteOpen) return;
        DocumentSaveResult result = SaveOpenDocuments();
        if (ReportSaveResult(result, "Autosave") && result.SavedNames.Count > 0)
            SetStatus($"Autosaved {result.SavedNames.Count} resource(s) across all project windows.");
    }

    /// <summary>
    /// Stops the editor viewports rendering while Studio is in the background.
    /// </summary>
    /// <remarks>
    /// This used to print "Preview paused while Studio is unfocused" and pause nothing at all — a
    /// setting that reported an action it never took. Pausing means what it says: every hardware
    /// viewport in every open editor stops its render driver, which is the whole point of the
    /// option (several D3D viewports redrawing at 60fps behind another application is the reason
    /// someone turns it on).
    /// </remarks>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_services.Settings.Current.Runtime.PauseWhenStudioLosesFocus) return;

        int paused = SetViewportRendering(active: false);
        SetStatus($"Preview paused while Studio is unfocused ({paused} viewport(s)).");
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _assetBrowser?.RefreshBookmarks();
        if (!_services.Settings.Current.Runtime.PauseWhenStudioLosesFocus) return;

        SetViewportRendering(active: true);
    }

    /// <summary>Starts or stops every hardware viewport hosted in this window. Returns how many.</summary>
    private int SetViewportRendering(bool active)
    {
        int count = 0;
        foreach (Genesis.Rendering.Viewport.D3DViewportControl viewport in FindViewports(this))
        {
            viewport.RenderingSuspended = !active;
            count++;
        }

        return count;
    }

    private static IEnumerable<Genesis.Rendering.Viewport.D3DViewportControl> FindViewports(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Genesis.Rendering.Viewport.D3DViewportControl viewport)
            {
                yield return viewport;
            }

            foreach (Genesis.Rendering.Viewport.D3DViewportControl nested in FindViewports(child))
            {
                yield return nested;
            }
        }
    }

    private Genesis.Runtime.Project.ProjectRunSession? _playerSession;

    private void OnPlayerExited(int code, string details)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || Disposing) return;
            if (code == 0) { SetStatus("Game stopped."); return; }
            string message = $"The game stopped with exit code {code}.\n{details}";
            _services.Log.Error("Runner", message);
            ShowToolWindow(_console);
            SetStatus("Game failed — see Console for the error.");
            if (!Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)
                MessageBox.Show(this, message, "Genesis — game error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _commandStateTimer.Stop();
            _commandStateTimer.Dispose();
            _shortcutFilter?.Dispose();
            _shortcutFilter = null;
            _playerSession?.Dispose();
            _services.Settings.SettingsChanged -= OnSettingsChanged;
            ThemeService.ThemeChanged -= OnThemeChanged;
            _assetBrowser.FinderInvalidated -= OnFinderInvalidated;
            _assetMonitor.Changed -= OnProjectAssetsChanged;
            _assetMonitor.Error -= OnAssetMonitorError;
            _assetMonitor.Dispose();
            _autoSaveTimer.Stop();
            _autoSaveTimer.Dispose();
            _finderDebounce.Stop();
            _finderDebounce.Tick -= FinderDebounceTick;
            _finderDebounce.Dispose();
            _finderCancellation?.Cancel();
            _finderCancellation?.Dispose();
            _finderCancellation = null;
        }

        base.Dispose(disposing);
    }

    private void ValidateProject()
    {
        IReadOnlyList<ProjectValidationIssue> issues = _services.Validator.Validate(_project);
        int errors = issues.Count(issue => issue.Severity == ProjectValidationSeverity.Error);
        int warnings = issues.Count(issue => issue.Severity == ProjectValidationSeverity.Warning);
        foreach (ProjectValidationIssue issue in issues)
        {
            if (issue.Severity == ProjectValidationSeverity.Error)
            {
                _services.Log.Error("Validation", $"{issue.Code}: {issue.Message} ({issue.Path})");
            }
            else if (issue.Severity == ProjectValidationSeverity.Warning)
            {
                _services.Log.Warning("Validation", $"{issue.Code}: {issue.Message} ({issue.Path})");
            }
            else
            {
                _services.Log.Information("Validation", $"{issue.Code}: {issue.Message}");
            }
        }

        UpdateValidationBadge(errors, warnings);
        ShowToolWindow(_console);
        SetStatus(
            issues.Count == 0
                ? "Project validation passed."
                : $"Validation complete: {errors} errors, {warnings} warnings.");
    }

    /// <summary>Internal (not private) purely so the headless suite can drive the F5/F6 guard
    /// directly (<c>Editor.Suite.Room.RunRequiresRoom</c>) instead of simulating a keystroke.</summary>
    internal void RunProject(bool debug, string? roomOverride = null, IDictionary<string, string>? additionalEnvironment = null)
    {
        // Fail fast and clearly: check the project's own resource tree (the source of truth
        // for what actually exists) before saving/compiling. The deeper launch path also
        // falls back through ProjectRoomResolver, but that's a filesystem heuristic search —
        // an explicit, direct check here gives a faster, unambiguous error instead of relying
        // on that fallback finding nothing several steps into the run.
        bool hasRoom = Flatten(_resources.BuildTree()).Any(item => item.Kind == ResourceKind.Room && !item.IsFolder);
        if (!hasRoom)
        {
            _services.Log.Error("Runner", "Cannot run — the project has no Room. Create at least one Room before pressing F5.");
            SetStatus("Cannot run — create at least one Room first.");
            return;
        }

        if (!TrySaveProject(debug ? "Debug" : "Run")) return;
        string projectRoot = _project.RootPath;

        _playerSession?.Dispose();
        _playerSession = null;

        // Strictly validate every saved PGSL event before launch. Hand-written compatibility C# is
        // compiled separately; authored PGSL runs on the same validated VM in Studio and Player.
        Genesis.Runtime.Project.ProjectRunLauncher.CompileOutcome compile =
            Genesis.Runtime.Project.ProjectRunLauncher.CompileScripts(projectRoot);
        if (!compile.Success)
        {
            _services.Log.Error(
                "Runner",
                $"Script compile failed:{Environment.NewLine}{compile.ErrorMessage}");
            ShowToolWindow(_console);
            SetStatus("Run cancelled — script errors (see Console).");
            return;
        }

        Dictionary<string, string> playerEnvironment = new(
            RenderingPreferencesBridge.BuildPlayerEnvironment(_services.Settings.Current.Rendering, _project.Manifest),
            StringComparer.OrdinalIgnoreCase);
        if (additionalEnvironment is not null)
            foreach ((string key, string value) in additionalEnvironment)
                playerEnvironment[key] = value;

        Genesis.Runtime.Project.ProjectRunLauncher.LaunchOutcome launch =
            Genesis.Runtime.Project.ProjectRunLauncher.Launch(
                projectRoot,
                roomName: string.IsNullOrWhiteSpace(roomOverride) ? _project.Manifest.StartRoom : roomOverride,
                extraEnvironment: playerEnvironment,
                debug: debug,
                supervised: true,
                exited: OnPlayerExited);
        if (!launch.Success)
        {
            _services.Log.Error("Runner", launch.ErrorMessage ?? "Unknown launch failure.");
            ShowToolWindow(_console);
            SetStatus("Cannot run — " + (launch.ErrorMessage ?? "unknown launch failure."));
            return;
        }

        _playerSession = launch.Session;
        _services.Log.Information(
            "Runner",
            $"Launched Ember player from '{launch.RuntimeDir}' — room '{launch.RoomName}'.");
        SetStatus(debug ? $"Debug play launched ({launch.RoomName})." : $"Play launched ({launch.RoomName}).");
    }

    private void ExportProject()
    {
        if (!TrySaveProject("Export")) return;
        using ExportGameDialog dialog = new(_project);
        dialog.ShowDialog(this);
        if (dialog.Result is not { } result) return;
        if (result.Success)
        {
            _services.Log.Information("Build", $"Exported '{_project.Manifest.Name}' to '{result.OutputPath}'.");
            SetStatus("Game export complete.");
        }
        else if (!string.Equals(result.ErrorMessage, "Export cancelled.", StringComparison.OrdinalIgnoreCase))
        {
            _services.Log.Error("Build", result.ErrorMessage);
            ShowToolWindow(_console);
            SetStatus("Game export failed — see Console.");
        }
    }

    private void ShowRuntimeDiagnostics(bool frameDebugger)
    {
        RuntimeDiagnosticsForm? existing = frameDebugger ? _frameDebuggerWindow : _profilerWindow;
        if (existing is { IsDisposed: false })
        {
            existing.Activate();
            return;
        }

        RuntimeDiagnosticsForm window = new(_project.RootPath, frameDebugger);
        if (frameDebugger) _frameDebuggerWindow = window;
        else _profilerWindow = window;
        window.FormClosed += (_, _) =>
        {
            if (frameDebugger) _frameDebuggerWindow = null;
            else _profilerWindow = null;
        };
        window.Show(this);
    }

    private void ResetLayout()
    {
        foreach (IDockContent content in _dockPanel.Contents.ToArray())
        {
            content.DockHandler.Hide();
        }

        SetDefaultLayout();
        SetStatus("Workspace layout reset.");
    }

    private void HandleWelcomeAction(string action)
    {
        switch (action)
        {
            case "Run":
                InvokeCommand("project.run", CaptureCommandContext());
                break;
            case "Validate":
                InvokeCommand("project.validate", CaptureCommandContext());
                break;
            case not null when action.StartsWith("Open:", StringComparison.Ordinal):
                string wanted = action["Open:".Length..];
                ResourceItem? recent = _resources.BuildTree()
                    .Children
                    .SelectMany(Flatten)
                    .FirstOrDefault(
                        item => string.Equals(
                            item.Name,
                            wanted,
                            StringComparison.OrdinalIgnoreCase));
                if (recent is not null)
                {
                    OpenResource(recent);
                }
                else
                {
                    // The Start page's scan and the resource tree can disagree if a file was
                    // deleted between the page opening and the click. Say so rather than no-op.
                    SetStatus($"'{wanted}' is no longer in the project.");
                }

                break;
            case "OpenStartRoom":
                ResourceItem? startRoom = _resources.BuildTree()
                    .Children
                    .SelectMany(Flatten)
                    .FirstOrDefault(
                        item => string.Equals(
                            item.Name,
                            _project.Manifest.StartRoom,
                            StringComparison.OrdinalIgnoreCase));
                if (startRoom is not null)
                {
                    OpenResource(startRoom);
                }
                else
                {
                    SetStatus("The start room has not been created yet.");
                }

                break;
        }
    }

    private static IEnumerable<ResourceItem> Flatten(ResourceItem item)
    {
        yield return item;
        foreach (ResourceItem child in item.Children)
        {
            foreach (ResourceItem descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private void ShowComingSoon(string feature)
    {
        _services.Log.Information("Tools", $"{feature} opened from the main command surface.");
        SetStatus($"{feature} is registered in the Studio command surface.");
    }

    private void ShowPackageManager()
    {
        using PackageManagerDialog dialog = new(_project, _services.Projects);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SetStatus($"Packs updated: {string.Join(", ", _project.Manifest.EnabledPacks)}.");
        }
    }

    private void OpenDocumentation()
    {
        string? documentation = FindMasterDocumentation(AppContext.BaseDirectory);
        if (documentation is null)
        {
            SetStatus("The Genesis master document is not available in this build.");
            return;
        }
        // Explorer works even when this machine has no Markdown file association.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "explorer.exe", $"/select,\"{documentation}\"") { UseShellExecute = true });
    }

    internal static string? FindMasterDocumentation(string applicationDirectory)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(applicationDirectory));
        // Published Studio ships Documentation beside its EXE. Source/debug runs are nested.
        for (int depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Documentation", "README.md");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private void ShowPgslCommandReference()
    {
        using PgslCommandReferenceForm reference = new();
        reference.ShowDialog(this);
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            this,
            StudioBuildInfo.DiagnosticText,
            "About Genesis",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ShowError(string title, Exception exception)
    {
        _services.Log.Error("Studio", title, exception);
        MessageBox.Show(
            this,
            exception.Message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void SetStatus(string message)
    {
        _status.Text = message;
        _services.Log.Information("Status", message);
    }
}
