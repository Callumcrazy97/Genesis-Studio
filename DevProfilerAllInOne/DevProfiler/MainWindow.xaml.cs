using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DevProfiler.Models;
using DevProfiler.Services;
using Microsoft.Win32;
using System.IO;

namespace DevProfiler;

public partial class MainWindow : Window
{
    private readonly ProfileCoordinator _coordinator = new();
    private ProfileSession? _session;
    private ICollectionView? _functionView;
    private ICollectionView? _processFunctionView;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _coordinator.StatusChanged += Coordinator_StatusChanged;
        _coordinator.MetricUpdated += Coordinator_MetricUpdated;
        _coordinator.OutputReceived += Coordinator_OutputReceived;
        _coordinator.SessionCompleted += Coordinator_SessionCompleted;
        Closing += MainWindow_Closing;
        UpdateLanguageUi();
    }

    private TargetLanguage SelectedLanguage => LanguageCombo.SelectedIndex switch
    {
        1 => TargetLanguage.DotNet,
        2 => TargetLanguage.PowerShell,
        _ => TargetLanguage.Python
    };

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.IsRunning)
        {
            RunButton.IsEnabled = false;
            try { await _coordinator.StopAsync(); }
            finally { RunButton.IsEnabled = true; }
            return;
        }

        var target = new ProfileTarget
        {
            Language = SelectedLanguage,
            TargetPath = TargetPathBox.Text.Trim(),
            RuntimePath = RuntimePathBox.Text.Trim(),
            WorkingDirectory = WorkingDirectoryBox.Text.Trim(),
            Arguments = ArgumentsBox.Text.Trim()
        };

        try
        {
            ResetVisuals();
            _session = await _coordinator.StartAsync(target);
            BindSession(_session);
            RunButton.Content = "■ STOP";
            RunButton.Foreground = (Brush)FindResource("RedBrush");
            RunButton.BorderBrush = (Brush)FindResource("RedBrush");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to start profiler", MessageBoxButton.OK, MessageBoxImage.Error);
            SetIdleButton();
        }
    }

    private void BindSession(ProfileSession session)
    {
        MergedGrid.ItemsSource = session.Functions;
        ProcessGrid.ItemsSource = session.Processes;
        WarningsGrid.ItemsSource = session.Warnings;
        EventGrid.ItemsSource = session.Events;

        _functionView = CollectionViewSource.GetDefaultView(session.Functions);
        _functionView.Filter = FunctionFilter;
        ProcessFunctionGrid.ItemsSource = session.ProcessFunctions;
        _processFunctionView = CollectionViewSource.GetDefaultView(session.ProcessFunctions);
        _processFunctionView.Filter = ProcessFunctionFilter;
    }

    private void Coordinator_StatusChanged(string status, string details)
    {
        Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = $"● {status}";
            StatusText.Foreground = status switch
            {
                "RUNNING" => (Brush)FindResource("GreenBrush"),
                "COMPLETE" => (Brush)FindResource("CyanBrush"),
                "STOPPED" => (Brush)FindResource("OrangeBrush"),
                "STOPPING" => (Brush)FindResource("OrangeBrush"),
                _ => (Brush)FindResource("MutedBrush")
            };
            FooterText.Text = details;
            CaptureQualityText.Text = GetCaptureQuality(SelectedLanguage);
        });
    }

    private void Coordinator_MetricUpdated(MetricSample sample)
    {
        if (_session is null)
            return;

        CpuMetric.Text = $"{sample.CpuPercent:N1}%";
        RamMetric.Text = $"{sample.PrivateMemoryMb:N0} MB";
        FpsMetric.Text = sample.Fps > 0 ? $"{sample.Fps:N0}" : "—";
        TimeMetric.Text = $"{sample.TimeSeconds:N1}s";
        ProcessesMetric.Text = sample.ProcessCount.ToString("N0");
        ProcessGrid.Items.Refresh();

        IReadOnlyList<MetricSample> metrics = _session.Metrics.ToList();
        CpuGraph.SetData("Target CPU", metrics.Select(x => x.CpuPercent), (Brush)FindResource("CyanBrush"), "%");
        RamGraph.SetData("Private Memory", metrics.Select(x => x.PrivateMemoryMb), (Brush)FindResource("PurpleBrush"), " MB");
        string fpsTitle = _session.Target.Language == TargetLanguage.Python ? "Pygame FPS" : "FPS";
        FpsGraph.SetData(fpsTitle, metrics.Select(x => x.Fps), (Brush)FindResource("GreenBrush"), "");
        ThreadsGraph.SetData("Threads", metrics.Select(x => (double)x.Threads), (Brush)FindResource("OrangeBrush"), "");
        ReadGraph.SetData("Disk Read", metrics.Select(x => x.ReadMbPerSecond), (Brush)FindResource("YellowBrush"), " MB/s");
        WriteGraph.SetData("Disk Write", metrics.Select(x => x.WriteMbPerSecond), (Brush)FindResource("RedBrush"), " MB/s");
    }

    private void Coordinator_OutputReceived(string line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RawOutputBox.AppendText(line + Environment.NewLine);
            RawOutputBox.ScrollToEnd();
        });
    }

    private void Coordinator_SessionCompleted()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_session is null)
                return;
            FunctionsMetric.Text = _session.Functions.Count.ToString("N0");
            TimeMetric.Text = $"{_session.ElapsedSeconds:N2}s";
            RawOutputBox.Text = _session.RawOutput;
            _functionView?.Refresh();
            _processFunctionView?.Refresh();
            ResultCountText.Text = $"{_functionView?.Cast<object>().Count() ?? 0:N0} rows";
            SetIdleButton();
        });
    }

    private void SetIdleButton()
    {
        RunButton.Content = "▶ RUN";
        RunButton.Foreground = (Brush)FindResource("GreenBrush");
        RunButton.BorderBrush = (Brush)FindResource("GreenBrush");
    }

    private void ResetVisuals()
    {
        CpuMetric.Text = "0.0%";
        RamMetric.Text = "0 MB";
        FpsMetric.Text = "—";
        TimeMetric.Text = "0.0s";
        FunctionsMetric.Text = "—";
        ProcessesMetric.Text = "0";
        RawOutputBox.Clear();
        ResultCountText.Text = string.Empty;
        ProcessFunctionGrid.ItemsSource = null;
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        (string title, string filter) = SelectedLanguage switch
        {
            TargetLanguage.Python => ("Select Python entry point", "Python files (*.py)|*.py|All files (*.*)|*.*"),
            TargetLanguage.DotNet => ("Select .NET executable", ".NET applications (*.exe;*.dll)|*.exe;*.dll|All files (*.*)|*.*"),
            TargetLanguage.PowerShell => ("Select PowerShell script", "PowerShell scripts (*.ps1)|*.ps1|All files (*.*)|*.*"),
            _ => ("Select target", "All files (*.*)|*.*")
        };

        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter
        };
        if (dialog.ShowDialog(this) == true)
            TargetPathBox.Text = dialog.FileName;
    }

    private void BrowseRuntime_Click(object sender, RoutedEventArgs e)
    {
        string title = SelectedLanguage switch
        {
            TargetLanguage.Python => "Select python.exe",
            TargetLanguage.DotNet => "Select dotnet.exe (DLL targets only)",
            TargetLanguage.PowerShell => "Select pwsh.exe or powershell.exe",
            _ => "Select runtime"
        };

        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            RuntimePathBox.Text = dialog.FileName;
    }

    private void BrowseWorkingDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select target working directory",
            InitialDirectory = Directory.Exists(WorkingDirectoryBox.Text) ? WorkingDirectoryBox.Text : Environment.CurrentDirectory
        };
        if (dialog.ShowDialog(this) == true)
            WorkingDirectoryBox.Text = dialog.FolderName;
    }

    private void TargetPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TargetPathBox is null || WorkingDirectoryBox is null || RuntimePathBox is null)
            return;

        string target = TargetPathBox.Text.Trim();
        if (!File.Exists(target))
            return;

        string directory = Path.GetDirectoryName(target) ?? string.Empty;
        WorkingDirectoryBox.Text = directory;

        if (SelectedLanguage == TargetLanguage.Python && string.IsNullOrWhiteSpace(RuntimePathBox.Text))
        {
            foreach (string environment in new[] { ".venv", "venv", "env" })
            {
                string candidate = Path.Combine(directory, environment, "Scripts", "python.exe");
                if (File.Exists(candidate))
                {
                    RuntimePathBox.Text = candidate;
                    break;
                }
            }
        }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        RuntimePathBox.Clear();
        TargetPathBox.Clear();
        WorkingDirectoryBox.Clear();
        UpdateLanguageUi();
    }

    private void UpdateLanguageUi()
    {
        if (CaptureQualityText is null)
            return;

        CaptureQualityText.Text = GetCaptureQuality(SelectedLanguage);
        RuntimePathBox.ToolTip = SelectedLanguage switch
        {
            TargetLanguage.Python => "Optional. Leave blank to auto-detect .venv/venv/env or use python.exe from PATH.",
            TargetLanguage.DotNet => "Only needed when profiling a .dll. Leave blank for a compiled .exe.",
            TargetLanguage.PowerShell => "Optional. Leave blank to auto-detect PowerShell 7 (pwsh.exe), then Windows PowerShell.",
            _ => "Optional runtime executable."
        };
        ArgumentsBox.ToolTip = SelectedLanguage == TargetLanguage.PowerShell
            ? "PowerShell script arguments, for example: -Iterations 10 -ShowWarning"
            : "Command-line arguments";
    }

    private static string GetCaptureQuality(TargetLanguage language) => language switch
    {
        TargetLanguage.Python => "Exact Python cProfile",
        TargetLanguage.DotNet => "Sampled .NET EventPipe",
        TargetLanguage.PowerShell => "Exact script time + structured streams",
        _ => "Process metrics"
    };

    private void FunctionFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _functionView?.Refresh();
        if (ResultCountText is not null)
            ResultCountText.Text = $"{_functionView?.Cast<object>().Count() ?? 0:N0} rows";
    }

    private bool FunctionFilter(object item)
    {
        if (item is not FunctionProfile row)
            return true;
        string filter = FunctionFilterBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        return row.Function.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.File.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Process.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Source.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _processFunctionView?.Refresh();
    }

    private bool ProcessFunctionFilter(object item)
    {
        if (ProcessGrid.SelectedItem is not ProcessSnapshot process || item is not FunctionProfile row)
            return false;
        return row.ProcessId == process.ProcessId;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _coordinator.IsRunning)
        {
            MessageBox.Show(this, "Complete a profiling session before exporting.", "No completed session", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export DevProfiler report",
            Filter = "Markdown report (*.md)|*.md",
            FileName = $"{Path.GetFileNameWithoutExtension(_session.Target.TargetPath)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.md",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Debug")
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            await MarkdownExporter.ExportAsync(_session, dialog.FileName);
            MessageBox.Show(this, $"Report saved:\n{dialog.FileName}", "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        IsEnabled = false;
        try
        {
            if (_coordinator.IsRunning)
                await _coordinator.StopAsync();
            await _coordinator.DisposeAsync();
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }
}
