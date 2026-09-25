using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Commands;
using Genesis.Application.Core.Editing;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Editing;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>The invocation retains the original editing target while a menu/palette owns focus.</summary>
internal sealed record ShellCommandContext(Control? FocusedControl, IStudioDocument? Document,
    ResourceItem? BrowserResource, bool BrowserHasFocus);

public sealed partial class StudioShellForm
{
    private readonly StudioCommandCatalog<ShellCommandContext> _commands = new();
    private readonly List<(ToolStripItem Item, string Id)> _commandBindings = [];
    private readonly System.Windows.Forms.Timer _commandStateTimer = new() { Interval = 160 };
    private Control? _lastCommandFocus;
    private ShellCommandContext? _menuContext;
    private bool _paletteOpen;
    private bool _paletteQueued;
    private StudioShortcutFilter? _shortcutFilter;

    internal StudioCommandCatalog<ShellCommandContext> CommandCatalog => _commands;

    private void RegisterShellCommands()
    {
        Register("project.new", "New Project…", "Project", "Create a new project.", _ => CreateProject(), Keys.Control | Keys.Shift | Keys.N);
        Register("project.open", "Open Project…", "Project", "Open an existing project.", _ => OpenProject(), Keys.Control | Keys.Shift | Keys.O);
        Register("project.close", "Close Project", "Project", "Return to the Project Hub.", _ => CloseProject());
        Register("document.save", "Save", "File", "Save the active resource.", context => context.Document!.Save(),
            Keys.Control | Keys.S, context => DocumentAvailability(context, StudioDocumentCommand.Save));
        Register("project.saveAll", "Save Project", "File", "Save every open resource, including separate Image and Model editors.",
            _ => SaveAll(), Keys.Control | Keys.Shift | Keys.S, keywords: "save all windows");
        Register("project.run", "Run", "Project", "Save and run the project in its matching Player.", _ => RunProject(false), Keys.F5);
        Register("project.debug", "Debug", "Project", "Save and run with the in-game debugger attached.", _ => RunProject(true), Keys.F6);
        Register("project.export", "Export Game…", "Project", "Save all resources and open the game exporter.", _ => ExportProject(), Keys.Control | Keys.B, keywords: "build package ship");
        Register("studio.exit", "Exit", "Studio", "Close Studio using its normal unsaved-document prompts.", _ => Close(), Keys.Alt | Keys.F4);
        RegisterEdit("edit.undo", "Undo", EditCommand.Undo, Keys.Control | Keys.Z);
        RegisterEdit("edit.redo", "Redo", EditCommand.Redo, Keys.Control | Keys.Y, Keys.Control | Keys.Shift | Keys.Z);
        RegisterEdit("edit.cut", "Cut", EditCommand.Cut, Keys.Control | Keys.X);
        RegisterEdit("edit.copy", "Copy", EditCommand.Copy, Keys.Control | Keys.C);
        RegisterEdit("edit.paste", "Paste", EditCommand.Paste, Keys.Control | Keys.V);
        RegisterEdit("edit.delete", "Delete", EditCommand.Delete, Keys.Delete);
        RegisterEdit("edit.selectAll", "Select All", EditCommand.SelectAll, Keys.Control | Keys.A);
        RegisterBrowser("resource.duplicate", "Duplicate Resource", ResourceBrowserCommand.Duplicate, Keys.Control | Keys.D);
        RegisterBrowser("resource.rename", "Rename Resource", ResourceBrowserCommand.Rename, Keys.F2);
        RegisterBrowser("resource.newFolder", "New Resource Subfolder", ResourceBrowserCommand.NewFolder, Keys.Control | Keys.Alt | Keys.N);
        Register("resource.find", "Find Resources", "Resources", "Search resources by their public names and type.", _ => FocusFinder(), Keys.Control | Keys.F);
        Register("studio.preferences", "Preferences…", "Studio", "Change Studio settings.", _ => ShowPreferences(), Keys.Control | Keys.Oemcomma);
        Register("view.assets", "Assets", "View", "Show the Resource Browser.", _ => ShowToolWindow(_assetBrowser));
        Register("view.inspector", "Inspector", "View", "Show the Inspector.", _ => ShowToolWindow(_inspector));
        Register("view.console", "Console", "View", "Show diagnostics and build output.", _ => ShowToolWindow(_console), Keys.Control | Keys.Oemtilde);
        Register("view.start", "Start Page", "View", "Show the project's start page.", _ => ShowStartPage());
        Register("view.reset", "Reset Workspace Layout", "View", "Restore the default dock layout.", _ => ResetLayout());
        Register("resource.refresh", "Refresh Asset Database", "Resources", "Refresh the project's resource catalog and browser.",
            _ => _assetBrowser.Execute(ResourceBrowserCommand.Refresh), Keys.Control | Keys.R);
        RegisterBrowser("resource.tags", "Edit Library Tags…", ResourceBrowserCommand.EditLibraryTags, Keys.None);
        RegisterBrowser("resource.tags.undo", "Undo Library Tag Edit", ResourceBrowserCommand.UndoLibraryTags, Keys.None);
        RegisterBrowser("resource.tags.redo", "Redo Library Tag Edit", ResourceBrowserCommand.RedoLibraryTags, Keys.None);
        RegisterBrowser("resource.favourite", "Toggle Resource Favourite", ResourceBrowserCommand.ToggleFavourite, Keys.None);
        Register("resource.showAll", "Show All Assets", "Resources", "Show every project resource, keeping the current type and text filters.",
            _ => ShowResourceLibrary(ResourceBrowserCommand.ShowAll));
        Register("resource.showFavourites", "Show Favourite Assets", "Resources", "Show bookmarked resources shared with asset pickers.",
            _ => ShowResourceLibrary(ResourceBrowserCommand.ShowFavourites));
        Register("resource.showRecent", "Show Recent Assets", "Resources", "Show recently opened or accepted resources, newest first.",
            _ => ShowResourceLibrary(ResourceBrowserCommand.ShowRecent));
        Register("resource.resetFilters", "Reset Asset Filters", "Resources", "Return to All assets and clear name, type and Finder filters.",
            _ => ShowResourceLibrary(ResourceBrowserCommand.ResetFilters));
        Register("resource.clearRecent", "Clear Recent Assets", "Resources", "Clear recent resource history without deleting any resources or favourites.",
            _ => _assetBrowser.Execute(ResourceBrowserCommand.ClearRecent));
        Register("project.validate", "Validate Project", "Project", "List project validation errors and warnings in the Console.", _ => ValidateProject());
        Register("tools.packages", "Package Manager", "Tools", "Manage project packs.", _ => ShowPackageManager());
        Register("tools.profiler", "Profiler", "Tools", "Open runtime profiling diagnostics.", _ => ShowRuntimeDiagnostics(false));
        Register("tools.frameDebugger", "Frame Debugger", "Tools", "Inspect render-frame diagnostics.", _ => ShowRuntimeDiagnostics(true));
        Register("help.documentation", "Genesis Documentation", "Help", "Show the current Genesis master document in Explorer.", _ => OpenDocumentation(), Keys.F1);
        Register("help.pgsl", "PGSL Command Reference", "Help", "Browse PGSL and Engine gameplay APIs.", _ => ShowPgslCommandReference());
        Register("help.copyBuildInfo", "Copy Build Information", "Help",
            "Copy the loaded Studio build identity, executable location and runtime details.",
            _ => { Clipboard.SetText(StudioBuildInfo.DiagnosticText); SetStatus("Build information copied."); });
        Register("help.about", "About Genesis", "Help", "Show Studio product information.", _ => ShowAbout());
        Register("studio.commands", "Command Palette…", "Studio", "Find Studio commands and keyboard shortcuts.", ShowCommandPalette,
            Keys.Control | Keys.Shift | Keys.P,
            _ => _paletteOpen ? CommandAvailability.Unavailable("The command palette is already open.") : CommandAvailability.Available,
            "search actions keyboard shortcuts");
    }

    private void ShowResourceLibrary(ResourceBrowserCommand command)
    {
        ShowToolWindow(_assetBrowser);
        _assetBrowser.Execute(command);
        _assetBrowser.FocusResults();
    }

    private void Register(string id, string title, string category, string description,
        Action<ShellCommandContext> action, Keys key = Keys.None,
        Func<ShellCommandContext, CommandAvailability>? availability = null, string keywords = "") =>
        _commands.Register(new(id, title, category, description, action, availability,
            key == Keys.None ? [] : [Shortcut(key)], keywords));

    private static CommandShortcut Shortcut(Keys key) => new((int)key, ShortcutText(key));
    private static string ShortcutText(Keys key)
    {
        string prefix = key.HasFlag(Keys.Control) ? "Ctrl+" : string.Empty;
        if (key.HasFlag(Keys.Alt)) prefix += "Alt+";
        if (key.HasFlag(Keys.Shift)) prefix += "Shift+";
        return prefix + ((key & Keys.KeyCode) switch
        {
            Keys.Oemcomma => ",", Keys.Oemtilde => "`", Keys.Delete => "Del",
            _ => (key & Keys.KeyCode).ToString(),
        });
    }

    private void RegisterEdit(string id, string title, EditCommand edit, params Keys[] shortcuts) =>
        _commands.Register(new(id, title, "Edit", title + " in the original focused editor or text field.",
            context => ExecuteFocusedEdit(context, edit), context => FocusedEditAvailability(context, edit),
            shortcuts.Select(Shortcut)));

    private void RegisterBrowser(string id, string title, ResourceBrowserCommand command, Keys shortcut) =>
        Register(id, title, "Resources", title + " in the focused Resource Browser.", _ => _assetBrowser.Execute(command),
            shortcut, context => BrowserAvailability(context, command));

    internal ShellCommandContext CaptureCommandContext(Control? source = null)
    {
        Control? focus = source ?? EditCommandRouter.FocusedControl();
        if (focus is ToolStrip || focus is null)
            focus = _lastCommandFocus is { IsDisposed: false } ? _lastCommandFocus : null;
        else if (BelongsToProjectWindow(focus)) _lastCommandFocus = focus;
        else focus = null;
        IStudioDocument? document = FindFocusedDocument(focus) ?? _dockPanel.ActiveDocument as IStudioDocument;
        return new(focus, document, _assetBrowser.SelectedResource, IsWithin(focus, _assetBrowser));
    }

    private IStudioDocument? FindFocusedDocument(Control? focus)
    {
        for (Control? control = focus; control is not null; control = control.Parent)
            if (control is IStudioDocument document) return document;
        // DockPanelSuite can parent a document through a floating host rather than its own Form.
        return _dockPanel.Contents.OfType<IStudioDocument>()
            .FirstOrDefault(document => document is Control control && IsWithin(focus, control));
    }

    private bool BelongsToProjectWindow(Control control)
    {
        Form? form = control.FindForm();
        if (ReferenceEquals(form, this) || IsWithin(control, _dockPanel)) return true;
        if (form is ImageEditorWindow image) return _imageEditorWindows.Values.Contains(image);
        if (form is ModelComposerWindow model) return _modelComposerWindows.Values.Contains(model);
        // DockPanelSuite floating windows are not descendants of the shell.
        return form is not null && _dockPanel.Contents.Any(content =>
            content is Control dock && (ReferenceEquals(dock, form) || IsWithin(control, dock)));
    }

    private static bool IsWithin(Control? control, Control ancestor)
    {
        for (; control is not null; control = control.Parent)
            if (ReferenceEquals(control, ancestor)) return true;
        return false;
    }

    private static CommandAvailability DocumentAvailability(ShellCommandContext context, StudioDocumentCommand command)
    {
        using IDisposable focus = EditorInputGuard.UseCommandFocus(context.FocusedControl);
        return context.Document is { } document && document is not Control { IsDisposed: true }
            && document.CanExecute(command)
            ? CommandAvailability.Available : CommandAvailability.Unavailable("No active resource can " + command.ToString().ToLowerInvariant() + ".");
    }

    private CommandAvailability BrowserAvailability(ShellCommandContext context, ResourceBrowserCommand command)
    {
        if (!context.BrowserHasFocus || context.FocusedControl is { IsDisposed: true }
            || EditCommandRouter.IsTextEntry(context.FocusedControl))
            return CommandAvailability.Unavailable("Focus the Resource Browser first.");
        if (!string.Equals(context.BrowserResource?.FullPath, _assetBrowser.SelectedResource?.FullPath, StringComparison.OrdinalIgnoreCase))
            return CommandAvailability.Unavailable("The selected resource changed. Select it again.");
        return _assetBrowser.CanExecute(command)
            ? CommandAvailability.Available
            : CommandAvailability.Unavailable("This action is not available for the selected resource or protected folder.");
    }

    private CommandAvailability FocusedEditAvailability(ShellCommandContext context, EditCommand command)
    {
        using IDisposable focus = EditorInputGuard.UseCommandFocus(context.FocusedControl);
        if (context.FocusedControl is { IsDisposed: true }) return CommandAvailability.Unavailable("The original editor was closed.");
        if (EditCommandRouter.IsTextEntry(context.FocusedControl))
            return TextCommandSupport.CanExecute(context.FocusedControl, command)
                ? CommandAvailability.Available : CommandAvailability.Unavailable("This text field cannot perform that action.");
        if (EditCommandRouter.FindTarget(context.FocusedControl, command) is not null) return CommandAvailability.Available;
        if (context.BrowserHasFocus && command is (EditCommand.Undo or EditCommand.Redo))
            return BrowserAvailability(context, command == EditCommand.Undo
                ? ResourceBrowserCommand.UndoLibraryTags : ResourceBrowserCommand.RedoLibraryTags);
        if (command is EditCommand.Undo or EditCommand.Redo)
            return DocumentAvailability(context, command == EditCommand.Undo ? StudioDocumentCommand.Undo : StudioDocumentCommand.Redo);
        ResourceBrowserCommand? browserCommand = ToBrowserCommand(command);
        return browserCommand is { } browser ? BrowserAvailability(context, browser)
            : CommandAvailability.Unavailable("The focused editor has no applicable selection.");
    }

    private void ExecuteFocusedEdit(ShellCommandContext context, EditCommand command)
    {
        if (EditCommandRouter.IsTextEntry(context.FocusedControl))
        {
            TextCommandSupport.Execute(context.FocusedControl, command);
            return;
        }
        using (EditorInputGuard.UseCommandFocus(context.FocusedControl))
        {
            if (EditCommandRouter.FindTarget(context.FocusedControl, command) is { } target)
            {
                if (!target.TryEdit(command)) throw new InvalidOperationException("The selection changed; the edit was not applied.");
                return;
            }
            if (context.BrowserHasFocus && command is (EditCommand.Undo or EditCommand.Redo))
            {
                _assetBrowser.Execute(command == EditCommand.Undo
                    ? ResourceBrowserCommand.UndoLibraryTags : ResourceBrowserCommand.RedoLibraryTags);
                return;
            }
            if (command is EditCommand.Undo or EditCommand.Redo)
            {
                context.Document!.Execute(command == EditCommand.Undo ? StudioDocumentCommand.Undo : StudioDocumentCommand.Redo);
                return;
            }
        }
        // Browser operations may show a confirmation dialog. Native focus owns that dialog.
        if (ToBrowserCommand(command) is { } browser) _assetBrowser.Execute(browser);
    }

    private static ResourceBrowserCommand? ToBrowserCommand(EditCommand command) => command switch
    {
        EditCommand.Cut => ResourceBrowserCommand.Cut, EditCommand.Copy => ResourceBrowserCommand.Copy,
        EditCommand.Paste => ResourceBrowserCommand.Paste, EditCommand.Delete => ResourceBrowserCommand.Delete,
        _ => null,
    };

    private bool DispatchShortcut(Keys keyData)
    {
        string? id = _commands.FindShortcut((int)keyData);
        if (id is null) return false;
        ShellCommandContext context = CaptureCommandContext();
        // Do not register Edit menu accelerators: the native text control owns its keyboard.
        if ((id.StartsWith("edit.", StringComparison.Ordinal) || id is "resource.rename" or "resource.duplicate")
            && EditCommandRouter.IsTextEntry(context.FocusedControl)) return false;
        if (!_commands.GetAvailability(id, context).Enabled) return false;
        InvokeCommand(id, context);
        return true;
    }

    private void InvokeCommand(string id, ShellCommandContext context)
    {
        CommandExecutionResult result = _commands.TryExecute(id, context);
        if (!result.Succeeded)
        {
            if (result.Error is not null)
            {
                _services.Log.Error("Command", result.Message, result.Error);
                ShowToolWindow(_console);
            }
            SetStatus(result.Message);
        }
        RefreshCommandBindings();
    }

    private ToolStripMenuItem CommandMenuItem(string id, string? caption = null)
    {
        StudioCommand<ShellCommandContext> command = _commands.Find(id)!;
        ToolStripMenuItem item = new(caption ?? command.Title)
        {
            Name = id, ShortcutKeyDisplayString = command.Shortcuts.FirstOrDefault()?.DisplayText ?? string.Empty,
            ShowShortcutKeys = true, ToolTipText = command.Description, AccessibleName = command.Title,
        };
        item.Click += (_, _) => InvokeCommand(id, _menuContext ?? CaptureCommandContext());
        _commandBindings.Add((item, id));
        return item;
    }

    private void BindCommandButton(ToolStripButton button, string id)
    {
        button.Name = id;
        button.AccessibleName = _commands.Find(id)!.Title;
        button.Click += (_, _) => InvokeCommand(id, CaptureCommandContext());
        _commandBindings.Add((button, id));
    }

    private void StartCommandStateUpdates()
    {
        _shortcutFilter = new StudioShortcutFilter(BelongsToProjectWindow, QueueCommandPalette);
        _commandStateTimer.Tick += (_, _) =>
        {
            if (!IsDisposed && ContainsFocus && !_paletteOpen) RefreshCommandBindings();
        };
        _commandStateTimer.Start();
        RefreshCommandBindings(includeMenus: true);
    }

    private void RefreshCommandBindings(bool includeMenus = false)
    {
        ShellCommandContext context = includeMenus && _menuContext is not null ? _menuContext : CaptureCommandContext();
        foreach ((ToolStripItem item, string id) in _commandBindings)
        {
            if (!includeMenus && item is ToolStripMenuItem) continue;
            CommandAvailability state = _commands.GetAvailability(id, context);
            if (item.Enabled != state.Enabled) item.Enabled = state.Enabled;
            if (id == "project.validate" && state.Enabled) continue; // Preserve its explicit validation badge/details.
            StudioCommand<ShellCommandContext> command = _commands.Find(id)!;
            string help = state.Enabled ? command.Description
                + (command.Shortcuts.Count == 0 ? string.Empty : " (" + command.Shortcuts[0].DisplayText + ")") : state.Reason;
            if (item.ToolTipText != help) item.ToolTipText = help;
        }
    }

    private void QueueCommandPalette(Control source)
    {
        if (_paletteQueued || _paletteOpen || IsDisposed || Disposing || !IsHandleCreated) return;
        ShellCommandContext context = CaptureCommandContext(source);
        _paletteQueued = true;
        // Do not enter another message loop from inside PreFilterMessage.
        BeginInvoke(new Action(() =>
        {
            _paletteQueued = false;
            if (!IsDisposed && !Disposing && !_paletteOpen
                && context.FocusedControl is { IsDisposed: false } target
                && BelongsToProjectWindow(target) && Form.ActiveForm?.Modal != true)
                InvokeCommand("studio.commands", context);
        }));
    }

    private void ShowCommandPalette(ShellCommandContext context)
    {
        if (_paletteOpen) return;
        _paletteOpen = true;
        try
        {
            using CommandPaletteForm palette = new(_commands, context);
            IWin32Window owner = context.FocusedControl?.FindForm() ?? this;
            if (palette.ShowDialog(owner) != DialogResult.OK || palette.SelectedCommandId is not { } id) return;
            // Menu and search controls must never become the target of Delete, Undo or Paste.
            if (context.FocusedControl is { IsDisposed: false, CanFocus: true } original) original.Focus();
            InvokeCommand(id, context);
        }
        finally { _paletteOpen = false; }
    }

    // Invoked only after a child editor has declined the key. Local tools retain first refusal.
    private bool RouteSharedWindowShortcut(Keys keyData) => DispatchShortcut(keyData);
}
