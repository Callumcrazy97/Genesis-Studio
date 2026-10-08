using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>A worker job used a command, property or name a worker job cannot reach.</summary>
public sealed class PgslWorkerJobException(string message) : InvalidOperationException(message);

/// <summary>
/// A worker job's instruction budget and cancellation. The VM checks it every <see cref="Slice"/>
/// instructions of a call instead of applying the per-call limit, so a job's whole run counts.
/// </summary>
internal sealed class PgslJobBudget(long limit, CancellationToken cancellation)
{
    public const int Slice = 1 << 16;

    public long Limit { get; } = limit;

    public void Check(long spent)
    {
        cancellation.ThrowIfCancellationRequested();
        if (spent > Limit)
            throw new PgslWorkerJobException($"The job ran more than its budget of {Limit.ToString(CultureInfo.InvariantCulture)} instructions; JobScriptBudget gives it more.");
    }
}

// Script jobs: a function of the project's Scripts run on a worker thread, so generating a chunk,
// a path or a map does not hold up the frame. The job runs on a VM of its own with a context of its
// own: private copies of the grids and lists given to it (taken when it starts, so the game may
// change its own meanwhile), the library functions as they were when it was created, and only the
// pure commands (maths, text, noise, grids, lists, maps, its own variables). Anything that reaches
// the game (instances, drawing, sound, files, random numbers, the clock, other Objects' data)
// raises "X is not available in a worker job". The same arguments and data always give the same
// result. JobTake copies the grids and lists marked to come back into the game's own.
public static partial class PgslCommands
{
    #region Script jobs

    /// <summary>Instructions a script job may run unless JobScriptBudget says otherwise (about a third of a second).</summary>
    internal const long DefaultScriptJobBudget = 100_000_000;
    internal const long MaximumScriptJobBudget = 2_000_000_000;

    // What a worker job may run, by command category; a few commands of those are refused by name
    // because they reach shared state (the random generator, the log).
    private static readonly HashSet<string> WorkerCategories = new(StringComparer.Ordinal)
    {
        "Math", "3D Math", "Strings", "General", "Noise", "Grids", "Lists", "Maps", "Data Structures", "Arrays", "Variables", "JSON",
    };

    private static readonly string[] DsFamilies = ["list", "grid", "map", "stack", "queue"];

    private static readonly HashSet<string> WorkerRefused = new(StringComparer.OrdinalIgnoreCase)
    {
        "Random", "RandomRange", "Choose", "DsListShuffle", "PgListShuffle", "Print",
    };

    /// <summary>Whether a worker job may run a command, by its name and category.</summary>
    internal static bool WorkerMayRun(string name, string category) =>
        name is not null && !WorkerRefused.Contains(name) && WorkerCategories.Contains(category ?? string.Empty);

    private sealed class ScriptJobSetup
    {
        public string Function;
        public int ParameterCount;
        public IReadOnlyDictionary<string, UserFunction> Library;
        public readonly List<(string Family, int Handle, bool CopyBack)> Shared = [];
        public readonly Dictionary<string, object> Variables = new(StringComparer.Ordinal);
        public long Budget = DefaultScriptJobBudget;
    }

    /// <summary>What a finished script job hands back: the function's result and the data to copy out.</summary>
    internal sealed class ScriptJobOutcome
    {
        public object Result;
        public List<(string Family, int Handle, object Data)> Outputs = [];
        public bool Taken;
        /// <summary>How long the job ran on its worker, from taking a worker to its result, for speed reports.</summary>
        public double WorkerMilliseconds;
    }

    // Worker bridges are made from the game's command table and reused; each serves one job at a time.
    private static readonly ConcurrentBag<PgslEngineBridge> WorkerBridges = [];
    private static PgslEngineBridge _workerBridgeSource;
    private static readonly object WorkerBridgeGate = new();

    private static PgslEngineBridge RentWorkerBridge(PgslEngineBridge game)
    {
        lock (WorkerBridgeGate)
        {
            if (!ReferenceEquals(_workerBridgeSource, game))
            {
                WorkerBridges.Clear();
                _workerBridgeSource = game;
            }
        }
        return WorkerBridges.TryTake(out PgslEngineBridge bridge) ? bridge : PgslEngineBridge.CreateWorker(game, WorkerMayRun);
    }

    private static void ReturnWorkerBridge(PgslEngineBridge game, PgslEngineBridge bridge)
    {
        bridge.SetContext(null);
        lock (WorkerBridgeGate)
        {
            if (ReferenceEquals(_workerBridgeSource, game) && WorkerBridges.Count < NativeJobPool.ScriptWorkerCount)
                WorkerBridges.Add(bridge);
        }
    }

    private static ScriptJobSetup PreparedJob(double id)
    {
        if (GetContext() == null || !JobPools.TryGetValue(GetContext(), out NativeJobPool pool)) return null;
        return pool.PreparedPayload(JobId(id)) as ScriptJobSetup;
    }

    private static bool Refuse(string error)
    {
        SetJobError(error);
        return false;
    }

    [PgslCommand("JobScriptCreate", "JobScriptCreate(function) -> job",
        "Prepare a worker job that runs a function of the project's Scripts; give it data with JobScriptGrid / JobScriptList, then JobScriptStart; 0 on failure (JobLastError says why)", "Native Jobs")]
    public static double JobScriptCreate(string function)
    {
        if (GetContext() == null) return 0;
        string name = function?.Trim() ?? string.Empty;
        IReadOnlyDictionary<string, UserFunction> library = ScriptAssetRegistry.Functions();
        if (name.Length == 0 || !library.TryGetValue(name, out UserFunction found))
        {
            SetJobError($"No function '{name}' in the project's Scripts: a worker job runs a function of a Script resource.");
            return 0;
        }
        if (VMEngine.Bridge?.NativeIdMap is { } commands && commands.ContainsKey(name))
        {
            SetJobError($"'{name}' is also the name of an engine command; give the function another name to run it as a job.");
            return 0;
        }
        try
        {
            int id = Jobs.Prepare("script", new ScriptJobSetup { Function = found.Name ?? name, ParameterCount = found.Parameters?.Count ?? 0, Library = library });
            SetJobError(string.Empty);
            return id;
        }
        catch (InvalidOperationException error) { SetJobError(error.Message); return 0; }
    }

    [PgslCommand("JobScriptGrid", "JobScriptGrid(job, grid, copyBack) -> bool",
        "Give a prepared job its own copy of a grid (same handle inside the job); copyBack true copies the job's grid into this one at JobTake", "Native Jobs")]
    public static bool JobScriptGrid(double job, double grid, bool copyBack) => ShareWithJob(job, "grid", grid, copyBack);

    [PgslCommand("JobScriptList", "JobScriptList(job, list, copyBack) -> bool",
        "Give a prepared job its own copy of a list (same handle inside the job); copyBack true copies the job's list into this one at JobTake", "Native Jobs")]
    public static bool JobScriptList(double job, double list, bool copyBack) => ShareWithJob(job, "list", list, copyBack);

    private static bool ShareWithJob(double job, string family, double handle, bool copyBack)
    {
        ScriptJobSetup setup = PreparedJob(job);
        if (setup == null) return Refuse("Not a prepared script job (JobScriptCreate makes one; data is given before JobScriptStart).");
        bool exists = family == "grid" ? Resolve<PgslGrid>("grid", handle) != null : Resolve<List<object>>("list", handle) != null;
        if (!exists) return Refuse($"No {family} with handle {StringOf(handle)} in this Object.");
        int at = setup.Shared.FindIndex(entry => entry.Family == family && entry.Handle == (int)handle);
        if (at >= 0) setup.Shared[at] = (family, (int)handle, copyBack);
        else setup.Shared.Add((family, (int)handle, copyBack));
        SetJobError(string.Empty);
        return true;
    }

    [PgslCommand("JobScriptVariable", "JobScriptVariable(job, name, value) -> bool",
        "Give a prepared job a variable of its own with this number (a seed, a setting, a grid's handle), so a function that reads that instance variable runs unchanged in the job", "Native Jobs")]
    public static bool JobScriptVariable(double job, string name, double value) => SetJobVariable(job, name, value);

    [PgslCommand("JobScriptVariableText", "JobScriptVariableText(job, name, text) -> bool",
        "Give a prepared job a variable of its own holding text", "Native Jobs")]
    public static bool JobScriptVariableText(double job, string name, string text) => SetJobVariable(job, name, text ?? string.Empty);

    private static bool SetJobVariable(double job, string name, object value)
    {
        ScriptJobSetup setup = PreparedJob(job);
        if (setup == null) return Refuse("Not a prepared script job (variables are given before JobScriptStart).");
        string key = name?.Trim() ?? string.Empty;
        bool valid = key.Length > 0 && (char.IsLetter(key[0]) || key[0] == '_') && key.All(c => char.IsLetterOrDigit(c) || c == '_')
            && !key.StartsWith("__", StringComparison.Ordinal);
        if (!valid) return Refuse($"'{name}' is not a variable name.");
        if (PgslRegisterFile.Slots.ContainsKey(key))
            return Refuse($"'{key}' is a built-in instance variable; a job's own are 0. Give the value another name or pass it as an argument.");
        setup.Variables[key] = value;
        SetJobError(string.Empty);
        return true;
    }

    [PgslCommand("JobScriptBudget", "JobScriptBudget(job, instructions) -> bool",
        "Instructions a prepared job may run in all (100 000 000 unless set; 1 000 to 2 000 000 000)", "Native Jobs")]
    public static bool JobScriptBudget(double job, double instructions)
    {
        ScriptJobSetup setup = PreparedJob(job);
        if (setup == null) return Refuse("Not a prepared script job.");
        if (double.IsNaN(instructions)) return Refuse("The budget must be a number.");
        setup.Budget = (long)Math.Clamp(Math.Floor(instructions), 1000, MaximumScriptJobBudget);
        SetJobError(string.Empty);
        return true;
    }

    [PgslCommand("JobScriptStart", "JobScriptStart(job, arguments...) -> bool",
        "Start a prepared job on a worker thread with the function's arguments (numbers, true/false or text); poll JobStatus, then JobTake and JobRelease", "Native Jobs")]
    public static bool JobScriptStart(double job, params object[] arguments)
    {
        ScriptJobSetup setup = PreparedJob(job);
        if (setup == null) return Refuse("Not a prepared script job (it may have started already).");
        arguments ??= [];
        if (arguments.Length != setup.ParameterCount)
            return Refuse($"{setup.Function} takes {setup.ParameterCount} arguments and the job was given {arguments.Length}.");
        object[] copied = new object[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            copied[i] = arguments[i] switch
            {
                double or bool or string => arguments[i],
                int or float or long => Convert.ToDouble(arguments[i], CultureInfo.InvariantCulture),
                _ => null,
            };
            if (copied[i] == null)
                return Refuse($"Argument {i + 1} of a job must be a number, true or false, or text; give grids and lists with JobScriptGrid / JobScriptList.");
        }

        // The job's own copies, taken now: the game may change its grids while the job runs.
        Dictionary<string, object> store = Store;
        Dictionary<string, object> data = new(StringComparer.Ordinal);
        // Structures the job makes get the handles they would get here, after the game's own.
        foreach (string family in DsFamilies)
            if (store.TryGetValue("__ds_next_" + family, out object next)) data["__ds_next_" + family] = next;
        foreach ((string family, int handle, _) in setup.Shared)
        {
            string key = DsKey(family, handle);
            if (!store.TryGetValue(key, out object live)) return Refuse($"The {family} {handle} given to the job no longer exists.");
            data[key] = live switch
            {
                PgslGrid grid => new PgslGrid { Cells = (double[])grid.Cells.Clone(), Width = grid.Width, Height = grid.Height },
                List<object> list => new List<object>(list),
                _ => null,
            };
        }

        var call = new StringBuilder("__jobResult = ").Append(setup.Function).Append('(');
        for (int i = 0; i < arguments.Length; i++) call.Append(i == 0 ? "argument0" : ", argument" + i.ToString(CultureInfo.InvariantCulture));
        CompileResult driver;
        try { driver = VMEngine.Compile(call.Append(");").ToString()); }
        catch (Exception error) { return Refuse("The job's call did not compile: " + error.Message); }
        PgslEngineBridge game = VMEngine.Bridge;
        if (driver == null || game == null) return Refuse("The job's call did not compile.");

        PgslContext owner = GetContext();
        var run = new ScriptJobRun(setup.Function, driver, setup.Library, copied, data, new Dictionary<string, object>(setup.Variables, StringComparer.Ordinal),
            setup.Shared.Where(entry => entry.CopyBack).Select(entry => (entry.Family, entry.Handle)).ToArray(),
            setup.Budget, game, owner.RoomWidth, owner.RoomHeight);
        if (!JobPools.TryGetValue(owner, out NativeJobPool pool) || !pool.Launch(JobId(job), token => RunScriptJob(run, token)))
            return Refuse("Not a prepared script job (it may have started already).");
        SetJobError(string.Empty);
        return true;
    }

    [PgslCommand("JobRunScript", "JobRunScript(function, arguments...) -> job",
        "Run a function of the project's Scripts on a worker thread with these arguments (no grids or lists; see JobScriptCreate); 0 on failure", "Native Jobs")]
    public static double JobRunScript(string function, params object[] arguments)
    {
        double job = JobScriptCreate(function);
        if (job == 0) return 0;
        if (JobScriptStart(job, arguments)) return job;
        string error = JobLastError();
        JobRelease(job);
        SetJobError(error);
        return 0;
    }

    [PgslCommand("JobTake", "JobTake(job) -> bool",
        "When a script job has succeeded, copy the grids and lists it was given with copyBack into this Object's own (once); its result stays readable until JobRelease", "Native Jobs")]
    public static bool JobTake(double job)
    {
        NativeJobPool.Snapshot snapshot = JobSnapshot(job);
        if (snapshot.State != "succeeded")
            return Refuse("Job has not succeeded: " + snapshot.State + (snapshot.Error.Length > 0 ? ". " + snapshot.Error : "."));
        if (snapshot.Value is not ScriptJobOutcome outcome) return Refuse("Not a script job: it has no data to take.");
        if (outcome.Taken) return Refuse("This job's data was taken already.");
        foreach ((string family, int handle, object data) in outcome.Outputs)
        {
            if (family == "grid" && data is PgslGrid result && Resolve<PgslGrid>("grid", handle) is { } grid)
            {
                grid.Cells = result.Cells;
                grid.Width = result.Width;
                grid.Height = result.Height;
            }
            else if (family == "list" && data is List<object> entries && Resolve<List<object>>("list", handle) is { } list)
            {
                list.Clear();
                list.AddRange(entries);
            }
        }
        outcome.Taken = true;
        outcome.Outputs = [];
        SetJobError(string.Empty);
        return true;
    }

    /// <summary>How long a finished script job ran on its worker; 0 for any other job.</summary>
    internal static double ScriptJobWorkerMilliseconds(double job) =>
        JobSnapshot(job).Value is ScriptJobOutcome outcome ? outcome.WorkerMilliseconds : 0;

    private sealed record ScriptJobRun(string Function, CompileResult Driver, IReadOnlyDictionary<string, UserFunction> Library,
        object[] Arguments, Dictionary<string, object> Data, Dictionary<string, object> Variables, (string Family, int Handle)[] CopyBack, long Budget,
        PgslEngineBridge Game, double RoomWidth, double RoomHeight);

    // On a worker thread: a VM, bridge and context of the job's own; nothing of the game's is written.
    private static NativeJobPool.Result RunScriptJob(ScriptJobRun run, CancellationToken token)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        PgslEngineBridge bridge = RentWorkerBridge(run.Game);
        PgslContext context = new() { RoomWidth = run.RoomWidth, RoomHeight = run.RoomHeight };
        foreach (KeyValuePair<string, object> pair in run.Data) context.Variables[pair.Key] = pair.Value;
        PgslContext previous = BindContext(context);
        try
        {
            bridge.SetContext(context);
            var vm = new PgslVm(bridge) { JobBudget = new PgslJobBudget(run.Budget, token), LibraryFunctions = run.Library };
            context.ActiveVm = vm;
            foreach (KeyValuePair<string, object> pair in run.Variables) vm.SetVariable(pair.Key, pair.Value);
            vm.SetScriptArguments(run.Arguments);
            vm.Execute(run.Driver.Instructions, run.Driver.Constants, clearVariables: false);
            vm.TryReadVariable("__jobResult", out object value);
            var outcome = new ScriptJobOutcome
            {
                Result = value switch { double or bool or string => value, null => 0.0, _ => Convert.ToString(value, CultureInfo.InvariantCulture) },
            };
            foreach ((string family, int handle) in run.CopyBack)
                if (context.Variables.TryGetValue(DsKey(family, handle), out object data))
                    outcome.Outputs.Add((family, handle, data));
            outcome.WorkerMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return new NativeJobPool.Result("script", outcome);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException(run.Function + ": " + error.Message, error);
        }
        finally
        {
            context.ActiveVm = null;
            ReturnWorkerBridge(run.Game, bridge);
            BindContext(previous);
        }
    }

    #endregion
}
