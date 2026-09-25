using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Commands;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Studio.Forms;

/// <summary>
/// Help → Commands. PGSL and Engine catalogues, Auto-Test (includes registry cross-check),
/// and Visual Test against a live 3D scene plus 2D HUD.
/// </summary>
public sealed class PgslCommandReferenceForm : DpiAwareForm
{
    private const string AllCategories = "(all categories)";
    private const int VisualBudgetMs = 60_000;

    private readonly TabControl _tabs = new();
    private readonly ListView _list = new();
    private readonly ComboBox _category = new();
    private readonly ComboBox _dimension = new();
    private readonly TextBox _search = new();
    private readonly Button _autoTest = new();
    private readonly Button _visualTest = new();
    private readonly Button _cancelVisualTest = new();
    private readonly ComboBox _backend = new();
    private readonly Label _summary = new();
    private readonly ProgressBar _progress = new();
    private readonly SplitContainer _split = new();
    private CommandVisualTestPanel? _visual;
    private readonly Dictionary<string, PgslCommandTestResult> _pgslResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EngineCommandTestResult> _engineResults = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<PgslCommandInfo> _pgslCatalogue = [];
    private IReadOnlyList<EngineCommandInfo> _engineCatalogue = [];
    private bool _testing;
    private bool _engineTab;
    private CancellationTokenSource? _visualCancel;

    public PgslCommandReferenceForm()
    {
        Text = "Commands — PGSL Game Code + Engine API";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 640);
        Size = new Size(1280, 860);
        BackColor = ThemeService.Palette.Canvas;
        ForeColor = ThemeService.Palette.Text;
        Font = ThemeService.InterfaceFont;

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.ShowItemToolTips = true;
        _list.BackColor = ThemeService.Palette.Canvas;
        _list.ForeColor = ThemeService.Palette.Text;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Command", 240);
        _list.Columns.Add("Description", 420);
        _list.Columns.Add("Category", 150);
        _list.Columns.Add("Mode", 95);
        _list.Columns.Add("Result", 110);
        _list.Columns.Add("Time / sample", 140, HorizontalAlignment.Right);

        _tabs.Dock = DockStyle.Top;
        _tabs.Height = 28;
        _tabs.TabPages.Add(new TabPage("PGSL Game Code"));
        _tabs.TabPages.Add(new TabPage("Engine API"));
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            _engineTab = _tabs.SelectedIndex == 1;
            _dimension.Visible = !_engineTab;
            _visual?.SetVisualMode(_engineTab);
            PopulateFilters();
            RefreshList();
        };

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.Panel1MinSize = 0;
        _split.Panel2MinSize = 0;
        _split.Panel1.Controls.Add(_list);
        _split.Panel2Collapsed = true;
        _split.SizeChanged += (_, _) => LayoutVisualSplit();
        _split.HandleCreated += (_, _) => LayoutVisualSplit();

        Controls.Add(_split);
        Controls.Add(BuildFilterBar());
        Controls.Add(_tabs);
        Controls.Add(BuildStatusBar());

        _pgslCatalogue = PgslCommandAutoTester.Catalogue();
        _engineCatalogue = EngineCommandAutoTester.Catalogue();
        PopulateFilters();
        RefreshList();

        FormClosed += (_, _) => _visualCancel?.Cancel();
    }

    public int VisibleCommandCount => _list.Items.Count;

    public int CatalogueCount => _pgslCatalogue.Count;

    public int EngineCatalogueCount => _engineCatalogue.Count;

    public bool ContainsCommand(string qualifiedName) => _pgslCatalogue.Any(command =>
        string.Equals(command.QualifiedName, qualifiedName, StringComparison.OrdinalIgnoreCase));

    public bool VisualViewportVisible => !_split.Panel2Collapsed && _visual is not null;

    public string PgslTabCaption => _tabs.TabPages[0].Text;

    public string EngineTabCaption => _tabs.TabPages[1].Text;

    public string ActiveCommandPathCaption => _engineTab
        ? "ENGINE BACKEND / API"
        : "PGSL GAME CODE";

    public int VisualDemoSubjectCount => _visual?.VisualDemoSubjectCount ?? 0;

    public int VisualDemoResourceCount => _visual?.VisualDemoResourceCount ?? 0;

    public bool PgslVisualBaselineReady => _visual?.PgslBaselineReady == true;

    public bool VisualTestRunning => _testing && _visualCancel is not null;

    public bool VisualCancelEnabled => _cancelVisualTest.Enabled;

    public string VisualCancelButtonCaption => _cancelVisualTest.Text;

    public string VisualRendererLabel => _visual?.ActiveRendererLabel
        ?? BackendLabel(RenderBackendCatalog.Describe(RenderBackendSelection.EffectiveBackend));

    public string VisualModeHudLabel => _visual?.VisualTestModeLabel
        ?? (_engineTab ? "ENGINE API" : "PGSL GAME CODE");

    public void SelectPgslTab() => _tabs.SelectedIndex = 0;

    public void SelectEngineTab() => _tabs.SelectedIndex = 1;

    public Bitmap? CaptureVisualFrame(int settleFrames = 3) =>
        EnsureVisual(RenderBackendSelection.EffectiveBackend).Viewport.CaptureFrame(settleFrames);

    public void ShowVisualScene()
    {
        RenderBackendOption backend = ResolveSelectedBackends()[0];
        _split.Panel2Collapsed = false;
        _split.PerformLayout();
        LayoutVisualSplit();
        CommandVisualTestPanel visual = EnsureVisual(backend);
        visual.SetVisualMode(_engineTab);
        string kind = ActiveCommandPathCaption;
        visual.SetStatus($"{kind} Visual Preview", "Five-subject 3D command scene", visual.VisualModeDescription);
        visual.ClearLive();
        visual.SetOrbiting(true);
        System.Windows.Forms.Application.DoEvents();
    }

    /// <summary>
    /// Preferences → Rendering → Test Backends. Selects the Engine tab and the requested backend
    /// so Visual Test draws on that GPU path, not All.
    /// </summary>
    public void PrepareBackendVisualTest(RenderBackendOption backend)
    {
        _tabs.SelectedIndex = 1;
        _backend.SelectedIndex = 1 + BackendCatalogIndex(backend);
    }

    private void LayoutVisualSplit()
    {
        if (_split.Panel2Collapsed || !_split.IsHandleCreated)
            return;

        int span = _split.Orientation == Orientation.Horizontal
            ? _split.ClientSize.Height
            : _split.ClientSize.Width;
        int usable = span - _split.SplitterWidth;
        if (usable < 120)
            return;

        int panel2 = Math.Min(320, Math.Max(140, usable / 2));
        int panel1 = usable - panel2;
        if (panel1 < 80)
        {
            panel1 = Math.Max(40, usable / 2);
            panel2 = usable - panel1;
        }

        _split.Panel1MinSize = 0;
        _split.Panel2MinSize = 0;
        _split.SplitterDistance = panel1;
    }

    private CommandVisualTestPanel EnsureVisual(RenderBackendOption backend)
    {
        if (_visual is not null && _visual.Viewport.BackendOverride == backend)
            return _visual;

        if (_visual is not null)
        {
            _split.Panel2.Controls.Remove(_visual);
            _visual.Dispose();
            _visual = null;
        }

        var panel = new CommandVisualTestPanel();
        panel.SetVisualMode(_engineTab);
        panel.Viewport.BackendOverride = backend;
        _split.Panel2.Controls.Add(panel);
        _visual = panel;
        return panel;
    }

    private Control BuildFilterBar()
    {
        Panel bar = new()
        {
            BackColor = ThemeService.Palette.Surface,
            Dock = DockStyle.Top,
            Height = 78,
            Padding = new Padding(10, 8, 10, 8),
        };

        _category.DropDownStyle = ComboBoxStyle.DropDownList;
        _category.Width = 200;
        _category.Location = new Point(10, 8);
        _category.SelectedIndexChanged += (_, _) => RefreshList();

        _dimension.DropDownStyle = ComboBoxStyle.DropDownList;
        _dimension.Width = 90;
        _dimension.Location = new Point(218, 8);
        _dimension.Items.AddRange(["All", "1D", "2D", "3D"]);
        _dimension.SelectedIndex = 0;
        _dimension.SelectedIndexChanged += (_, _) => RefreshList();

        _search.Width = 220;
        _search.Location = new Point(316, 8);
        _search.PlaceholderText = "Filter by name or description…";
        _search.TextChanged += (_, _) => RefreshList();

        _autoTest.Text = "Auto-test";
        _autoTest.Width = 110;
        _autoTest.Location = new Point(548, 7);
        _autoTest.FlatStyle = FlatStyle.Flat;
        _autoTest.BackColor = ThemeService.Palette.Accent;
        _autoTest.ForeColor = Color.White;
        _autoTest.Click += (_, _) => RunAutoTestFromUi();

        _visualTest.Text = "Visual Test";
        _visualTest.Width = 120;
        _visualTest.Location = new Point(666, 7);
        _visualTest.FlatStyle = FlatStyle.Flat;
        _visualTest.BackColor = ThemeService.Palette.Surface;
        _visualTest.ForeColor = ThemeService.Palette.Text;
        _visualTest.Click += async (_, _) => await RunVisualTestAsync();

        _cancelVisualTest.Text = "Cancel";
        _cancelVisualTest.Width = 90;
        _cancelVisualTest.Location = new Point(794, 7);
        _cancelVisualTest.FlatStyle = FlatStyle.Flat;
        _cancelVisualTest.BackColor = ThemeService.Palette.Surface;
        _cancelVisualTest.ForeColor = ThemeService.Palette.TextMuted;
        _cancelVisualTest.Enabled = false;
        _cancelVisualTest.AccessibleName = "Cancel visual test";
        _cancelVisualTest.AccessibleDescription = "Stop the running PGSL or Engine visual-test sweep.";
        _cancelVisualTest.Click += (_, _) => CancelVisualTest();

        Label backendCaption = new()
        {
            Text = "Backend",
            AutoSize = true,
            Location = new Point(10, 48),
            ForeColor = ThemeService.Palette.TextMuted,
        };

        _backend.DropDownStyle = ComboBoxStyle.DropDownList;
        _backend.Width = 160;
        _backend.Location = new Point(72, 44);
        _backend.Items.Add("All");
        foreach (RenderBackendDescriptor descriptor in RenderBackendCatalog.All)
            _backend.Items.Add(BackendLabel(descriptor));
        _backend.SelectedIndex = 1 + Math.Max(0, BackendCatalogIndex(RenderBackendSelection.RequestedBackend));

        _progress.Location = new Point(248, 48);
        _progress.Width = 220;
        _progress.Height = 18;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Visible = false;

        bar.Controls.AddRange([
            _category, _dimension, _search, _autoTest, _visualTest, _cancelVisualTest,
            backendCaption, _backend, _progress,
        ]);
        return bar;
    }

    /// <summary>Requests cancellation of the active visual-test sweep.</summary>
    public void CancelVisualTest()
    {
        if (!_testing || _visualCancel is null)
            return;

        _visualCancel.Cancel();
        _cancelVisualTest.Enabled = false;
        _cancelVisualTest.ForeColor = ThemeService.Palette.TextMuted;
        _summary.Text = "Cancelling visual test…";
        _summary.ForeColor = ThemeService.Palette.TextMuted;
    }

    private Control BuildStatusBar()
    {
        Panel bar = new()
        {
            BackColor = ThemeService.Palette.Surface,
            Dock = DockStyle.Bottom,
            Height = 30,
            Padding = new Padding(10, 6, 10, 6),
        };
        _summary.Dock = DockStyle.Fill;
        _summary.ForeColor = ThemeService.Palette.TextMuted;
        bar.Controls.Add(_summary);
        return bar;
    }

    private void PopulateFilters()
    {
        _category.Items.Clear();
        _category.Items.Add(AllCategories);
        IEnumerable<string> categories = _engineTab
            ? _engineCatalogue.Select(command => command.Category)
            : _pgslCatalogue.Select(command => command.Category);
        foreach (string category in categories.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal))
            _category.Items.Add(category);
        _category.SelectedIndex = 0;
    }

    private void RefreshList()
    {
        string category = _category.SelectedItem as string ?? AllCategories;
        string dimension = _dimension.SelectedItem as string ?? "All";
        string search = _search.Text.Trim();

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            if (_engineTab)
            {
                foreach (EngineCommandInfo command in _engineCatalogue)
                {
                    if (!Matches(command.Name, command.Signature, command.Description, command.Category, "3D", category, "All", search))
                        continue;
                    ListViewItem item = new(command.Signature) { Tag = command };
                    item.SubItems.Add(command.Description ?? "");
                    item.SubItems.Add(command.Category ?? "");
                        item.SubItems.Add("ENGINE API");
                    if (_engineResults.TryGetValue(command.Name, out EngineCommandTestResult? result))
                    {
                        item.SubItems.Add(DescribeEngine(result.Outcome));
                        item.SubItems.Add(string.IsNullOrWhiteSpace(result.SampleData)
                            ? $"{result.ElapsedMicroseconds:0.00} µs"
                            : result.SampleData);
                        item.ForeColor = ColourForEngine(result.Outcome);
                    }
                    else
                    {
                        item.SubItems.Add(command.IsImplemented ? "—" : "not implemented");
                        item.SubItems.Add("—");
                        if (!command.IsImplemented) item.ForeColor = ThemeService.Palette.TextMuted;
                    }

                    _list.Items.Add(item);
                }
            }
            else
            {
                foreach (PgslCommandInfo command in _pgslCatalogue)
                {
                    string commandDimension = PgslCommandAutoTester.DimensionOf(command.Category);
                    if (!Matches(command.Name, command.QualifiedName, command.Description, command.Category, commandDimension, category, dimension, search))
                        continue;
                    ListViewItem item = new(command.QualifiedName) { Tag = command };
                    item.SubItems.Add(command.Description ?? "");
                    item.SubItems.Add(command.Category ?? "");
                    item.SubItems.Add("PGSL " + commandDimension);
                    if (_pgslResults.TryGetValue(command.Name, out PgslCommandTestResult? result))
                    {
                        item.SubItems.Add(Describe(result.Outcome));
                        item.SubItems.Add($"{result.ElapsedMicroseconds:0.00} µs");
                        item.ForeColor = ColourFor(result.Outcome);
                    }
                    else
                    {
                        item.SubItems.Add(command.IsImplemented ? "—" : "not implemented");
                        item.SubItems.Add("—");
                        if (!command.IsImplemented) item.ForeColor = ThemeService.Palette.TextMuted;
                    }

                    item.ToolTipText = command.Signature;
                    _list.Items.Add(item);
                }
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        UpdateSummary();
    }

    private static bool Matches(
        string name, string qualified, string? description, string? category, string dim,
        string categoryFilter, string dimensionFilter, string search)
    {
        if (categoryFilter != AllCategories && !string.Equals(category, categoryFilter, StringComparison.Ordinal))
            return false;
        if (dimensionFilter != "All" && dim != dimensionFilter)
            return false;
        if (search.Length == 0) return true;
        return name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || qualified.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (description ?? "").Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(PgslCommandOutcome outcome) => outcome switch
    {
        PgslCommandOutcome.Callable => "callable (unverified)",
        PgslCommandOutcome.Threw => "THREW",
        PgslCommandOutcome.NotImplemented => "not implemented",
        _ => "not callable",
    };

    private static string DescribeEngine(EngineCommandOutcome outcome) => outcome switch
    {
        EngineCommandOutcome.Callable => "callable (unverified)",
        EngineCommandOutcome.Threw => "THREW",
        EngineCommandOutcome.NotImplemented => "not implemented",
        _ => "not callable",
    };

    private static Color ColourFor(PgslCommandOutcome outcome) => outcome switch
    {
        PgslCommandOutcome.Callable => ThemeService.Palette.Warning,
        PgslCommandOutcome.Threw => ThemeService.Palette.Error,
        _ => ThemeService.Palette.TextMuted,
    };

    private static Color ColourForEngine(EngineCommandOutcome outcome) => outcome switch
    {
        EngineCommandOutcome.Callable => ThemeService.Palette.Warning,
        EngineCommandOutcome.Threw => ThemeService.Palette.Error,
        _ => ThemeService.Palette.TextMuted,
    };

    private void UpdateSummary()
    {
        if (_engineTab)
        {
            if (_engineResults.Count == 0)
            {
                _summary.Text = $"{_engineCatalogue.Count} ENGINE API commands · showing {_list.Items.Count} · "
                    + "Auto-test invokes every Engine.* command and cross-checks PGSL wrappers";
                return;
            }

            int callable = _engineResults.Values.Count(r => r.Outcome == EngineCommandOutcome.Callable);
            int threw = _engineResults.Values.Count(r => r.Outcome == EngineCommandOutcome.Threw);
            int unfinished = _engineResults.Values.Count(r => r.Outcome == EngineCommandOutcome.NotImplemented);
            _summary.Text = $"{_engineResults.Count} ENGINE API swept · {callable} callable (not effect-verified) · "
                + $"{unfinished} not implemented · {threw} threw";
            _summary.ForeColor = threw > 0 || unfinished > 0 ? ThemeService.Palette.Error : ThemeService.Palette.Warning;
            return;
        }

        if (_pgslResults.Count == 0)
        {
            _summary.Text = $"{_pgslCatalogue.Count} PGSL GAME CODE commands · showing {_list.Items.Count} · "
                + "Auto-test invokes every command with dummy data and cross-checks Engine.*";
            return;
        }

        int pgslCallable = _pgslResults.Values.Count(r => r.Outcome == PgslCommandOutcome.Callable);
        int pgslThrew = _pgslResults.Values.Count(r => r.Outcome == PgslCommandOutcome.Threw);
        int pgslUnfinished = _pgslResults.Values.Count(r => r.Outcome == PgslCommandOutcome.NotImplemented);
        double total = _pgslResults.Values.Sum(r => r.ElapsedMicroseconds);
        _summary.Text = $"{_pgslResults.Count} PGSL GAME CODE swept · {pgslCallable} callable (not effect-verified) · "
            + $"{pgslUnfinished} not implemented · {pgslThrew} threw · {total / 1000.0:0.0} ms";
        _summary.ForeColor = pgslThrew > 0 || pgslUnfinished > 0 ? ThemeService.Palette.Error : ThemeService.Palette.Warning;
    }

    private void RunAutoTestFromUi()
    {
        RunAutoTest();
        try
        {
            string path = WriteDesktopLog(
                _engineTab ? "Genesis-Commands-Engine-AutoTest.txt" : "Genesis-Commands-PGSL-AutoTest.txt",
                BuildAutoTestLog());
            _summary.Text += $" · log {path}";
        }
        catch (Exception exception)
        {
            _summary.Text += $" · log failed: {exception.Message}";
            _summary.ForeColor = ThemeService.Palette.Error;
        }
    }

    /// <summary>
    /// Run the sweep for the active tab and fold in registry cross-check. Public so headless
    /// tests drive the same button path. Does not write Desktop files (the UI button does).
    /// </summary>
    public void RunAutoTest()
    {
        if (_testing) return;
        _testing = true;
        _autoTest.Enabled = false;
        _visualTest.Enabled = false;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Visible = true;
        _summary.Text = _engineTab
            ? "Sweeping Engine.* callability (effects require family/visual tests)…"
            : "Sweeping PGSL callability (effects require family/visual tests)…";
        System.Windows.Forms.Application.DoEvents();

        try
        {
            CommandCrossCheckReport cross = CommandRegistryCrossCheck.Run();
            if (_engineTab)
            {
                EngineCommandTestReport report = EngineCommandAutoTester.Run(repeats: 3);
                _engineResults.Clear();
                foreach (EngineCommandTestResult result in report.Results)
                    _engineResults[result.Name] = result;
            }
            else
            {
                PgslCommandTestReport report = PgslCommandAutoTester.Run(repeats: 3);
                _pgslResults.Clear();
                foreach (PgslCommandTestResult result in report.Results)
                    _pgslResults[result.Name] = result;
            }

            RefreshList();
            _summary.Text += $" · cross-check Engine {cross.EngineCount} / PGSL {cross.PgslCount}"
                + $" · missing in PGSL {cross.MissingInPgsl.Count} · missing in Engine {cross.MissingInEngine.Count}";
        }
        finally
        {
            _progress.Visible = false;
            _autoTest.Enabled = true;
            _visualTest.Enabled = true;
            _testing = false;
        }
    }

    public async Task RunVisualTestAsync(int budgetMs = VisualBudgetMs)
    {
        if (_testing) return;
        _testing = true;
        _autoTest.Enabled = false;
        _visualTest.Enabled = false;
        _cancelVisualTest.Enabled = true;
        _cancelVisualTest.ForeColor = ThemeService.Palette.Warning;
        _tabs.Enabled = false;
        _backend.Enabled = false;
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Minimum = 0;
        _progress.Maximum = 1000;
        _progress.Value = 0;
        _split.Panel2Collapsed = false;
        _split.PerformLayout();
        LayoutVisualSplit();
        _visualCancel?.Cancel();
        _visualCancel?.Dispose();
        _visualCancel = new CancellationTokenSource();
        CancellationToken token = _visualCancel.Token;
        var lines = new List<string>();
        string kind = _engineTab ? "ENGINE API" : "PGSL GAME CODE";
        IReadOnlyList<RenderBackendOption> backends = ResolveSelectedBackends();

        try
        {
            int totalMs = budgetMs * Math.Max(1, backends.Count);
            Stopwatch overall = Stopwatch.StartNew();
            for (int b = 0; b < backends.Count; b++)
            {
                token.ThrowIfCancellationRequested();
                RenderBackendOption backend = backends[b];
                RenderBackendDescriptor descriptor = RenderBackendCatalog.Describe(backend);
                string label = BackendLabel(descriptor);
                lines.Add($"=== {kind} VISUAL TEST · {label} ({descriptor.SettingsValue}) ===");

                if (!descriptor.IsImplemented || !RenderBackendSelection.IsAvailable(backend))
                {
                    lines.Add($"[SKIP] {label} is not available on this machine");
                    _summary.Text = $"{kind} · {label} skipped (unavailable)";
                    continue;
                }

                CommandVisualTestPanel visual;
                try
                {
                    visual = EnsureVisual(backend);
                    visual.SetVisualMode(_engineTab);
                    visual.SetOrbiting(true);
                    visual.SetStatus($"{kind} Visual Test", $"{label} · building scene", visual.VisualModeDescription);
                    System.Windows.Forms.Application.DoEvents();

                    if (!ProbeBaselineFrame(visual, out string baselineDetail))
                    {
                        lines.Add($"[BACKEND-FAIL] {label}: {baselineDetail}");
                        _summary.Text = $"{kind} · {label} baseline failed: {baselineDetail}";
                        _summary.ForeColor = ThemeService.Palette.Error;
                        continue;
                    }

                    lines.Add($"[BASELINE-OK] {label}: {baselineDetail}");
                }
                catch (Exception exception)
                {
                    lines.Add($"[FAIL] {label} failed to start: {exception.Message}");
                    _summary.Text = $"{kind} · {label} failed to start";
                    continue;
                }

                if (_engineTab)
                    await RunEngineVisualAsync(visual, lines, budgetMs, label, token, overall, totalMs);
                else
                    await RunPgslVisualAsync(visual, lines, budgetMs, label, token, overall, totalMs);

                visual.ClearLive();
                visual.SetStatus($"{kind} Visual Test", $"{label} complete", "");
            }

            // A click that lands as the final budget finishes must still win over completion; do
            // not emit a log or present a completed status after cancellation was requested.
            token.ThrowIfCancellationRequested();
            string path = WriteDesktopLog($"Genesis-Commands-{(_engineTab ? "EngineAPI" : "PGSLGameCode")}-VisualTest.txt", string.Join(Environment.NewLine, lines));
            _visual?.SetStatus($"{kind} Visual Test", "Complete", path);
            _summary.Text = $"{kind} visual test ({backends.Count} backend(s), {budgetMs / 1000}s each) wrote {path}";
        }
        catch (OperationCanceledException)
        {
            _visual?.ClearLive();
            _visual?.SetStatus($"{kind} Visual Test", "Cancelled", "");
            _summary.Text = "Visual test cancelled.";
            _summary.ForeColor = ThemeService.Palette.TextMuted;
        }
        catch (Exception exception)
        {
            _summary.Text = "Visual test failed: " + exception.Message;
            _summary.ForeColor = ThemeService.Palette.Error;
            WriteDesktopLog($"Genesis-Commands-{(_engineTab ? "EngineAPI" : "PGSLGameCode")}-VisualTest.txt", "Error: " + exception);
        }
        finally
        {
            _visual?.ClearLive();
            _progress.Visible = false;
            _autoTest.Enabled = true;
            _visualTest.Enabled = true;
            _cancelVisualTest.Enabled = false;
            _cancelVisualTest.ForeColor = ThemeService.Palette.TextMuted;
            _tabs.Enabled = true;
            _backend.Enabled = true;
            _visualCancel?.Dispose();
            _visualCancel = null;
            _testing = false;
        }
    }

    private async Task RunPgslVisualAsync(
        CommandVisualTestPanel visual,
        List<string> lines,
        int budgetMs,
        string backendLabel,
        CancellationToken token,
        Stopwatch overall,
        int totalMs)
    {
        IReadOnlyList<(MemberInfo Member, PgslCommandAttribute Attribute)> members = PgslCommandAutoTester.Members();
        lines.Add($"--- PGSL Visual Test ({backendLabel}) ---");
        Stopwatch clock = Stopwatch.StartNew();
        int index = 0;
        foreach ((MemberInfo member, PgslCommandAttribute attribute) in members)
        {
            token.ThrowIfCancellationRequested();
            index++;
            bool scene = IsDrawing3D(attribute.Category);
            object[] args = VisualArgs(member, attribute.Category, index);
            visual.SetStatus(
                $"PGSL Visual Test · {backendLabel}",
                $"{index}/{members.Count}  {attribute.Signature}",
                attribute.Category);
            visual.SetLivePgsl(member, args, scene);
            RecordVisualInvocation(visual, lines, attribute.Signature);
            await PaceToBudgetAsync(clock, index, members.Count, budgetMs, overall, totalMs, token);
        }

        await FinishBudgetAsync(clock, budgetMs, token);
    }

    private async Task RunEngineVisualAsync(
        CommandVisualTestPanel visual,
        List<string> lines,
        int budgetMs,
        string backendLabel,
        CancellationToken token,
        Stopwatch overall,
        int totalMs)
    {
        IReadOnlyList<(MethodInfo Method, EngineCommandAttribute Attribute)> members = EngineCommandAutoTester.Members();
        lines.Add($"--- Engine Visual Test ({backendLabel}) ---");
        Stopwatch clock = Stopwatch.StartNew();
        int index = 0;
        foreach ((MethodInfo method, EngineCommandAttribute attribute) in members)
        {
            token.ThrowIfCancellationRequested();
            index++;
            object[] args = VisualEngineArgs(method, attribute, index);
            visual.SetStatus(
                $"Engine Visual Test · {backendLabel}",
                $"{index}/{members.Count}  {attribute.Signature}",
                attribute.Category);
            if (SkipLiveEngine(method.Name))
            {
                visual.ClearLive();
                visual.SetStatus(
                    $"Engine Visual Test · {backendLabel}",
                    $"{index}/{members.Count}  {attribute.Signature}",
                    "Logged only — does not resize or retarget this window");
                lines.Add($"[HUD] {attribute.Signature}");
            }
            else
            {
                visual.SetLiveEngine(method, args);
                RecordVisualInvocation(visual, lines, attribute.Signature);
            }

            await PaceToBudgetAsync(clock, index, members.Count, budgetMs, overall, totalMs, token);
        }

        await FinishBudgetAsync(clock, budgetMs, token);
    }

    private static void RecordVisualInvocation(
        CommandVisualTestPanel visual,
        List<string> lines,
        string signature)
    {
        // Drive one frame synchronously. A timer tick is not proof that the command ran: a slow
        // backend can spend the whole budget compiling or fault before reaching DrawScene/HUD.
        visual.Viewport.Host.RenderFrame();

        if (visual.LastCommandException is Exception commandFault)
        {
            lines.Add($"[COMMAND-FAULT] {signature} :: {commandFault.GetType().Name}: {commandFault.Message}");
            return;
        }

        if (visual.Viewport.Host.LastRenderException is Exception renderFault)
        {
            lines.Add($"[RENDER-FAULT] {signature} :: {renderFault.GetType().Name}: {renderFault.Message}");
            return;
        }

        if (visual.CommandInvocationCount == 0)
        {
            lines.Add($"[NOT-RUN] {signature} :: renderer did not reach the command hook");
            return;
        }

        lines.Add($"[INVOKED] {signature} :: callability only; visual effect is inspected in the live frame");
    }

    private static bool ProbeBaselineFrame(CommandVisualTestPanel visual, out string detail)
    {
        visual.ClearLive();
        visual.Viewport.Host.ClearRenderFault();
        visual.Viewport.Host.RenderFrame();
        if (visual.Viewport.Host.LastRenderException is Exception renderFault)
        {
            detail = $"render fault: {renderFault.GetType().Name}: {renderFault.Message}";
            return false;
        }

        using Bitmap? frame = visual.Viewport.CaptureFrame(settleFrames: 0);
        if (frame is null)
        {
            detail = visual.Viewport.Host.LastRenderException is Exception readbackFault
                ? $"readback fault: {readbackFault.GetType().Name}: {readbackFault.Message}"
                : "backend returned no readable frame";
            return false;
        }

        int samples = 0;
        int nonBlack = 0;
        int distinct = 0;
        int previous = int.MinValue;
        int stepX = Math.Max(1, frame.Width / 32);
        int stepY = Math.Max(1, frame.Height / 18);
        for (int y = stepY / 2; y < frame.Height; y += stepY)
        for (int x = stepX / 2; x < frame.Width; x += stepX)
        {
            Color color = frame.GetPixel(x, y);
            int packed = (color.R >> 4) << 8 | (color.G >> 4) << 4 | (color.B >> 4);
            samples++;
            if (color.R + color.G + color.B > 24) nonBlack++;
            if (packed != previous) distinct++;
            previous = packed;
        }

        if (samples == 0 || nonBlack < Math.Max(8, samples / 20) || distinct < 4)
        {
            detail = $"blank/flat frame ({nonBlack}/{samples} non-black samples, {distinct} transitions)";
            return false;
        }

        detail = $"readback {frame.Width}x{frame.Height}, {nonBlack}/{samples} non-black samples";
        return true;
    }

    private async Task PaceToBudgetAsync(
        Stopwatch clock,
        int completed,
        int count,
        int budgetMs,
        Stopwatch overall,
        int totalMs,
        CancellationToken token)
    {
        int remaining = Math.Max(1, count - completed);
        long remainingMs = Math.Max(16, budgetMs - clock.ElapsedMilliseconds);
        int delay = (int)Math.Max(16, remainingMs / remaining);
        await Task.Delay(delay, token);
        int overallValue = (int)Math.Clamp(overall.ElapsedMilliseconds * 1000.0 / Math.Max(1, totalMs), 0, 1000);
        if (overallValue != _progress.Value)
            _progress.Value = overallValue;
        System.Windows.Forms.Application.DoEvents();
    }

    private static async Task FinishBudgetAsync(Stopwatch clock, int budgetMs, CancellationToken token)
    {
        int leftover = budgetMs - (int)clock.ElapsedMilliseconds;
        if (leftover > 16)
            await Task.Delay(leftover, token);
    }

    private IReadOnlyList<RenderBackendOption> ResolveSelectedBackends()
    {
        if (_backend.SelectedIndex <= 0)
            return RenderBackendCatalog.All.Select(descriptor => descriptor.Backend).ToArray();

        int catalogIndex = _backend.SelectedIndex - 1;
        if (catalogIndex < 0 || catalogIndex >= RenderBackendCatalog.All.Count)
            return [RenderBackendSelection.RequestedBackend];
        return [RenderBackendCatalog.All[catalogIndex].Backend];
    }

    private static int BackendCatalogIndex(RenderBackendOption backend)
    {
        for (int i = 0; i < RenderBackendCatalog.All.Count; i++)
        {
            if (RenderBackendCatalog.All[i].Backend == backend)
                return i;
        }

        return 0;
    }

    private static string BackendLabel(RenderBackendDescriptor descriptor) => descriptor.Backend switch
    {
        _ => descriptor.ShortName,
    };

    private static bool IsDrawing3D(string? category) =>
        string.Equals(category, "Drawing 3D", StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, "Camera Systems — 3D", StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, "Engine · Models", StringComparison.OrdinalIgnoreCase);

    private static bool SkipLiveEngine(string name) =>
        name is "WindowSize" or "WindowMode" or "DisplayResolution" or "NetHost" or "NetConnect";

    private static object[] VisualArgs(MemberInfo member, string? category, int index)
    {
        ParameterInfo[] parameters = member is MethodInfo method
            ? method.GetParameters()
            : [];
        var args = new object[parameters.Length];
        bool drawing2d = string.Equals(category, "Drawing 2D", StringComparison.OrdinalIgnoreCase);
        bool drawing3d = IsDrawing3D(category);
        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            if (drawing3d && type == typeof(double))
            {
                args[i] = i switch
                {
                    0 => 0.0,
                    1 => 1.4,
                    2 => 0.0,
                    3 => 1.6 + (index % 5) * 0.15,
                    _ => 0.6 + i * 0.2,
                };
                continue;
            }

            if (drawing2d && type == typeof(double))
            {
                args[i] = 24.0 + i * 18.0 + (index % 8) * 6.0;
                continue;
            }

            args[i] = Dummy(type, i);
        }

        return args;
    }

    private static object[] VisualEngineArgs(MethodInfo method, EngineCommandAttribute attribute, int index)
    {
        ParameterInfo[] parameters = method.GetParameters();
        var args = new object[parameters.Length];
        string name = method.Name;
        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            if (string.Equals(name, "Fog", StringComparison.Ordinal) && type == typeof(bool))
            {
                args[i] = index % 4 != 0;
                continue;
            }

            if (string.Equals(name, "Lighting", StringComparison.Ordinal) && type == typeof(bool))
            {
                args[i] = index % 5 != 0;
                continue;
            }

            if (string.Equals(name, "DebugView", StringComparison.Ordinal) && type == typeof(string))
            {
                args[i] = index % 3 == 0 ? "Shaded" : index % 3 == 1 ? "Normals" : "Depth";
                continue;
            }

            if (name.StartsWith("FogColour", StringComparison.Ordinal) && type == typeof(float))
            {
                args[i] = i switch { 0 => 0.45f, 1 => 0.62f, 2 => 0.85f, _ => 0.8f };
                continue;
            }

            if ((name.Contains("FogStart", StringComparison.Ordinal) || name.Contains("FogEnd", StringComparison.Ordinal))
                && type == typeof(float))
            {
                args[i] = name.Contains("End", StringComparison.Ordinal) ? 48f + i : 12f;
                continue;
            }

            if (string.Equals(name, "SetCameraFov", StringComparison.Ordinal) && type == typeof(float))
            {
                args[i] = 45f + (index % 6) * 5f;
                continue;
            }

            if (string.Equals(name, "WindowTitle", StringComparison.Ordinal) && type == typeof(string))
            {
                args[i] = "Genesis Visual Test";
                continue;
            }

            if (string.Equals(name, "DrawRect", StringComparison.Ordinal) && type == typeof(float))
            {
                args[i] = i switch
                {
                    0 => 24f,
                    1 => 110f,
                    2 => 200f,
                    3 => 52f,
                    4 => 0.25f,
                    5 => 0.82f,
                    6 => 1f,
                    7 => 0.9f,
                    _ => 1f,
                };
                continue;
            }

            if (string.Equals(name, "DrawLine", StringComparison.Ordinal) && type == typeof(float))
            {
                args[i] = i switch
                {
                    0 => 24f,
                    1 => 170f,
                    2 => 240f,
                    3 => 210f,
                    4 => 1f,
                    5 => 0.75f,
                    6 => 0.2f,
                    7 => 1f,
                    _ => 1f,
                };
                continue;
            }

            if (string.Equals(name, "DrawText", StringComparison.Ordinal))
            {
                if (type == typeof(string)) { args[i] = "Engine HUD"; continue; }
                if (type == typeof(float))
                {
                    args[i] = i switch { 1 => 24f, 2 => 140f, 3 => 16f, 4 => 1f, 5 => 0.9f, 6 => 0.4f, _ => 1f };
                    continue;
                }
            }

            if (string.Equals(name, "SetFog", StringComparison.Ordinal) && type == typeof(bool))
            {
                args[i] = true;
                continue;
            }

            if (string.Equals(name, "SetAmbientLight", StringComparison.Ordinal) && type == typeof(float))
            {
                args[i] = i switch { 0 => 0.42f, 1 => 0.50f, 2 => 0.62f, _ => 0.5f };
                continue;
            }

            args[i] = Dummy(type, i);
        }

        _ = attribute;
        return args;
    }

    private static object Dummy(Type type, int position)
    {
        if (type == typeof(double)) return 1.0 + position;
        if (type == typeof(bool)) return position % 2 == 0;
        if (type == typeof(string)) return position == 0 ? "VisualTest" : "x";
        if (type == typeof(int)) return 1 + position;
        if (type == typeof(float)) return 1f + position;
        if (type == typeof(byte[])) return Array.Empty<byte>();
        if (type == typeof(float[])) return new[] { 1f, 2f, 3f, 4f };
        if (type == typeof(Color)) return Color.FromArgb(255, 40, 160, 220);
        if (type == typeof(object[])) return new object[] { 1.0, 2.0, 3.0 };
        if (type == typeof(object)) return 1.0 + position;
        return null!;
    }

    private string BuildAutoTestLog()
    {
        var text = new StringBuilder();
        CommandCrossCheckReport cross = CommandRegistryCrossCheck.Run();
        text.AppendLine(_engineTab ? "--- Engine Auto-Test ---" : "--- PGSL Auto-Test ---");
        text.AppendLine($"Written {DateTime.Now.ToString("u", CultureInfo.InvariantCulture)}");
        text.AppendLine();
        if (_engineTab)
        {
            foreach (EngineCommandTestResult result in _engineResults.Values.OrderBy(r => r.Category).ThenBy(r => r.Name))
                text.AppendLine($"[{DescribeEngine(result.Outcome)}] {result.Signature}  sample={result.SampleData}  {result.Detail}");
        }
        else
        {
            foreach (PgslCommandTestResult result in _pgslResults.Values.OrderBy(r => r.Category).ThenBy(r => r.Name))
                text.AppendLine($"[{Describe(result.Outcome)}] {result.Signature}  {result.Detail}");
        }

        text.AppendLine();
        text.Append(cross.ToMarkdown());
        return text.ToString();
    }

    private static string WriteDesktopLog(string fileName, string contents)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Directory.CreateDirectory(desktop);
        string path = Path.Combine(desktop, fileName);
        File.WriteAllText(path, contents ?? "");
        return path;
    }
}
