using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Editors.Suite.Objects;

public sealed partial class ObjectEditorControl
{
    private static readonly object DebugVmGate = new();
    private PgslDebugController? _debugController;
    private PgslDebuggerWindow? _debugWindow;
    private Task? _debugTask;

    public void DebugActiveEvent()
    {
        if (_debugTask is { IsCompleted: false })
        {
            _debugWindow?.Activate();
            return;
        }
        if (_activeEvent is null || string.IsNullOrWhiteSpace(_code.CodeText))
        {
            UpdateStatus("Choose an event with PGSL code before debugging.");
            return;
        }

        PgslValidationReport validation = PgslScriptValidator.ValidateSource(_code.CodeText, _activeEvent);
        if (validation.Errors.Count > 0)
        {
            ValidateActiveEvent();
            UpdateStatus("Debug cancelled — fix the current event errors first.");
            return;
        }

        _runtimePreview.Playing = false;
        ShowCodeEditor();
        _debugController?.Dispose();
        _debugController = new PgslDebugController { BreakOnStart = true, PauseOnError = true };
        _debugController.SetBreakpoints(_code.Breakpoints);
        _code.BreakpointsChanged -= SyncDebugBreakpoints;
        _code.BreakpointsChanged += SyncDebugBreakpoints;
        _debugController.Paused += DebuggerPaused;
        _debugController.Continued += DebuggerContinued;
        _debugController.Completed += DebuggerCompleted;
        _debugWindow?.Close();
        _debugWindow = new PgslDebuggerWindow(_debugController, ObjectStem + " · " + _activeEvent);
        _debugWindow.FormClosed += (_, _) => _debugWindow = null;
        _debugWindow.Show(FindForm());

        string eventName = _activeEvent;
        string source = _code.CodeText;
        PgslDebugController controller = _debugController;
        _debugTask = Task.Run(() => RunDebugWorker(controller, eventName, source));
    }

    private void RunDebugWorker(PgslDebugController controller, string eventName, string source)
    {
        try
        {
            lock (DebugVmGate)
            {
                VMEngine.Initialize();
                CompileResult compiled = VMEngine.Compile(source)
                    ?? throw new InvalidOperationException("The event did not produce executable PGSL.");
                PgslVm vm = VMEngine.CreateVm(debug: false);
                vm.Debugger = controller;
                vm.DebugSourceName = ObjectStem;
                vm.DebugEventName = eventName;
                vm.LoadUserFunctions(compiled.UserFunctions);
                PgslContext context = new() { InstanceId = 1, RoomWidth = 1280, RoomHeight = 720 };
                PgslContext? previousBridge = VMEngine.Bridge.GetContext();
                PgslContext? previousCommands = PgslCommands.BindContext(context);
                IGameContext? previousGame = PgslCommands.ActiveGameContext;
                string? previousProject = PgslCommands.ProjectPath;
                try
                {
                    VMEngine.Bridge.SetContext(context);
                    PgslCommands.ActiveGameContext = new NullGameContext { ProjectPath = ProjectRoot };
                    PgslCommands.ProjectPath = ProjectRoot;
                    vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);
                }
                finally
                {
                    VMEngine.Bridge.SetContext(previousBridge);
                    PgslCommands.BindContext(previousCommands);
                    PgslCommands.ActiveGameContext = previousGame;
                    PgslCommands.ProjectPath = previousProject;
                }
            }
        }
        catch (PgslDebugStopException)
        {
        }
        catch (Exception exception)
        {
            OnDebugUi(() => UpdateStatus("Debugger: " + exception.Message));
        }
    }

    private void SyncDebugBreakpoints(object? sender, EventArgs e) =>
        _debugController?.SetBreakpoints(_code.Breakpoints);

    private void DebuggerPaused(object? sender, PgslDebugLocation location) => OnDebugUi(() =>
    {
        _code.CurrentExecutionLine = location.Line;
        UpdateStatus(string.IsNullOrWhiteSpace(location.ErrorMessage)
            ? $"Paused at {location.FunctionName}, line {location.Line}."
            : $"Paused on error at line {location.Line}: {location.ErrorMessage}");
    });

    private void DebuggerContinued(object? sender, EventArgs e) => OnDebugUi(() =>
    {
        _code.CurrentExecutionLine = 0;
        UpdateStatus("Debugger running…");
    });

    private void DebuggerCompleted(object? sender, EventArgs e) => OnDebugUi(() =>
    {
        _code.CurrentExecutionLine = 0;
        UpdateStatus("Debug session complete.");
    });

    private void StopDebugging()
    {
        _debugController?.Stop();
        _code.CurrentExecutionLine = 0;
    }

    private void OnDebugUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
