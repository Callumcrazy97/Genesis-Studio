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
// own: the grids, lists and maps given to it as they were when it started (shared, not copied: see
// PgslCommands.SharedData.cs, so the game may change its own meanwhile and the job never sees it),
// the library functions as they were when it was created, and only the pure commands (maths, text,
// noise, grids, lists, maps, its own variables). Anything that reaches the game (instances, drawing,
// sound, files, random numbers, the clock, other Objects' data) raises "X is not available in a
// worker job". The same arguments and data always give the same result. JobTake puts the structures
// marked to come back, and changed by the job, into the game's own.
public static partial class PgslCommands
{
    #region Script jobs

    /// <summary>Instructions a script job may run unless JobScriptBudget says otherwise (about a third of a second).</summary>
    internal const long DefaultScriptJobBudget = 100_000_000;
    internal const long MaximumScriptJobBudget = 2_000_000_000;

    // What a worker job may run, by command category; a few commands of those are refused by name
    // because they reach shared state (the random generator, the log). A command added to one of
    // these categories runs in jobs too: it must touch only its arguments and the context's own
    // data (PgslContext.Variables), or be refused here.
    private static readonly HashSet<string> WorkerCategories = new(StringComparer.Ordinal)
    {
        "Math", "3D Math", "Strings", "General", "Noise", "Grids", "Lists", "Maps", "Data Structures", "Arrays", "Variables", "JSON",
    };

    private static readonly string[] DsFamilies = ["list", "grid", "map", "stack", "queue", "priority", "nlist"];
    // The store keys of each family's next handle, made once rather than for every job started.
    private static readonly string[] DsNextKeys = [.. DsFamilies.Select(family => "__ds_next_" + family)];

    private static readonly HashSet<string> WorkerRefused = new(StringComparer.OrdinalIgnoreCase)
    {
        "Random", "RandomRange", "Choose", "RandomSeed", "DsListShuffle", "PgListShuffle", "Print",
    };

    // Commands of other categories a job may run: building a mesh lent to it (JobScriptMesh), which
    // writes only the job's own build, and its per-worker caches.
    private static readonly HashSet<string> WorkerAllowed = new(StringComparer.Ordinal)
    {
        "MeshClear", "MeshAddVertex", "MeshAddTriangle", "MeshAddCube", "MeshAddCubeTiles", "MeshAddQuad", "MeshAddQuadColors",
        "MeshVertexCount", "MeshTriangleCount", "MeshAddQuadsFromList", "MeshAddQuadsFromNumList", "MeshAddVerticesFromList", "MeshAddVerticesFromNumList",
        "JobCacheNumList", "JobCacheGet", "JobCacheSet",
        // Scene changes queued in the job's own buffer, made by the game at JobTake.
        "SceneQueuePosition", "SceneQueuePositionsFromNumList", "SceneQueueVariable", "SceneQueueDestroy", "SceneQueueStaticMove", "SceneQueueCount",
    };

    /// <summary>Whether a worker job may run a command, by its name and category.</summary>
    internal static bool WorkerMayRun(string name, string category) =>
        name is not null && !WorkerRefused.Contains(name)
        && (WorkerCategories.Contains(category ?? string.Empty) || WorkerAllowed.Contains(name));

    private sealed class ScriptJobSetup
    {
        public string Function;
        public int ParameterCount;
        public IReadOnlyDictionary<string, UserFunction> Library;
        /// <summary>What the job is given; a grid with a region is lent in part (JobScriptGridRegion).</summary>
        public readonly List<(string Family, int Handle, bool CopyBack, GridRect? Region)> Shared = [];
        public readonly Dictionary<string, object> Variables = new(StringComparer.Ordinal);
        public long Budget = DefaultScriptJobBudget;
        /// <summary>JobScriptShareAll: every structure and instance variable of the Object, as at the start.</summary>
        public bool ShareAll;
        /// <summary>Meshes the job builds (JobScriptMesh): each starts empty in the job and is handed over at JobTake.</summary>
        public readonly List<int> Meshes = [];
        /// <summary>Jobs of this Object it waits for (JobScriptAfter); it reads what they changed.</summary>
        public readonly List<int> After = [];
        /// <summary>Taken by a worker before normal jobs waiting (JobScriptUrgent).</summary>
        public bool Urgent;
    }

    /// <summary>What a finished script job hands back: the function's result and the data to copy out.</summary>
    internal sealed class ScriptJobOutcome
    {
        public object Result;
        public List<(string Family, int Handle, object Data, GridRect? Region)> Outputs = [];
        /// <summary>The grid copies made for parts lent to the job, given back for reuse once taken or released.</summary>
        public List<object> LentCopies;
        public bool Taken;
        /// <summary>How long the job ran on its worker, from taking a worker to its result, for speed reports.</summary>
        public double WorkerMilliseconds;
        /// <summary>The meshes the job built, finished, for JobTake to hand to the game's meshes.</summary>
        public List<(int Id, Genesis.Runtime.Rendering.MeshBuffers Build)> Meshes;
        /// <summary>Scene changes the job queued (SceneQueue*), made by JobTake in order.</summary>
        public SceneCommandBuffer SceneCommands;
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
        "Give a prepared job a grid as it is when the job starts (same handle inside the job; shared, not copied); copyBack true puts the job's grid into this one at JobTake if the job changed it", "Native Jobs")]
    public static bool JobScriptGrid(double job, double grid, bool copyBack) => ShareWithJob(job, "grid", grid, copyBack);

    [PgslCommand("JobScriptList", "JobScriptList(job, list, copyBack) -> bool",
        "Give a prepared job a list as it is when the job starts (same handle inside the job; shared, not copied); copyBack true puts the job's list into this one at JobTake if the job changed it", "Native Jobs")]
    public static bool JobScriptList(double job, double list, bool copyBack) => ShareWithJob(job, "list", list, copyBack);

    [PgslCommand("JobScriptMap", "JobScriptMap(job, map, copyBack) -> bool",
        "Give a prepared job a map as it is when the job starts (same handle inside the job; shared, not copied); copyBack true puts the job's map into this one at JobTake if the job changed it", "Native Jobs")]
    public static bool JobScriptMap(double job, double map, bool copyBack) => ShareWithJob(job, "map", map, copyBack);

    [PgslCommand("JobScriptPriority", "JobScriptPriority(job, queue, copyBack) -> bool",
        "Give a prepared job a priority queue (DsPriorityCreate) as it is when the job starts (same handle inside the job; shared, not copied); copyBack true puts the job's queue into this one at JobTake if the job changed it", "Native Jobs")]
    public static bool JobScriptPriority(double job, double queue, bool copyBack) => ShareWithJob(job, "priority", queue, copyBack);

    [PgslCommand("JobScriptShareAll", "JobScriptShareAll(job) -> bool",
        "Let a prepared job read every grid, list, map, stack, queue and priority queue of this Object and its instance variables (numbers, text, true/false), as they are when it starts; shared, not copied, so the cost does not grow with their size", "Native Jobs")]
    public static bool JobScriptShareAll(double job)
    {
        ScriptJobSetup setup = PreparedJob(job);
        if (setup == null) return Refuse("Not a prepared script job (JobScriptCreate makes one; data is given before JobScriptStart).");
        setup.ShareAll = true;
        SetJobError(string.Empty);
        return true;
    }

    private static bool ShareWithJob(double job, string family, double handle, bool copyBack, GridRect? region = null)
    {
        ScriptJobSetup setup = PreparedJob(job);
        if (setup == null) return Refuse("Not a prepared script job (JobScriptCreate makes one; data is given before JobScriptStart).");
        bool exists = family switch
        {
            "grid" => ResolveRead<PgslGrid>("grid", handle) != null,
            "map" => ResolveRead<PgslMap>("map", handle) != null,
            "priority" => ResolveRead<PgslPriority>("priority", handle) != null,
            "nlist" => ResolveRead<PgslNumberList>("nlist", handle) != null,
            _ => ResolveRead<List<object>>("list", handle) != null,
        };
        if (!exists) return Refuse($"No {family} with handle {StringOf(handle)} in this Object.");
        // A part lent is one of perhaps several parts of the same structure (each comes back); a whole
        // structure given again replaces what was given before.
        int at = setup.Shared.FindIndex(entry => entry.Family == family && entry.Handle == (int)handle
            && (region == null ? entry.Region == null : entry.Region == region));
        if (at >= 0) setup.Shared[at] = (family, (int)handle, copyBack, region);
        else setup.Shared.Add((family, (int)handle, copyBack, region));
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

    [PgslCommand("JobScriptUrgent", "JobScriptUrgent(job, urgent) -> bool",
        "Have a prepared job taken by the next free worker before the normal jobs waiting (the chunk beside the player before far ones); jobs of one kind still go in the order they start", "Native Jobs")]
    public static bool JobScriptUrgent(double job, bool urgent)
    {
        ScriptJobSetup setup = PreparedJob(job);
        if (setup == null) return Refuse("Not a prepared script job (JobScriptCreate makes one; it is marked before JobScriptStart).");
        setup.Urgent = urgent;
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

        Dictionary<string, object> store = Store;
        // Grids lent in part (JobScriptGridRegion) are not shared: the job gets a copy of its own.
        HashSet<string> lent = null;
        foreach ((string family, int handle, _, GridRect? region) in setup.Shared)
        {
            string key = DsKey(family, handle);
            if (!store.TryGetValue(key, out object entry)) return Refuse($"The {family} {handle} given to the job no longer exists.");
            // A list lent in part is shared as a whole list is; only its range comes back.
            if (region == null || family != "grid") continue;
            if ((entry is SharedData sharedEntry ? sharedEntry.Value : entry) is not PgslGrid) return Refuse($"The grid {handle} lent to the job is no longer a grid.");
            (lent ??= new HashSet<string>(StringComparer.Ordinal)).Add(key);
        }

        CompileResult driver;
        try { driver = JobDriver(setup.Library, setup.Function, arguments.Length); }
        catch (Exception error) { return Refuse("The job's call did not compile: " + error.Message); }
        PgslEngineBridge game = VMEngine.Bridge;
        if (driver == null || game == null) return Refuse("The job's call did not compile.");
        PgslContext owner = GetContext();
        if (!JobPools.TryGetValue(owner, out NativeJobPool pool)) return Refuse("Not a prepared script job (it may have started already).");

        // The job's data, as it is now: shared with the job, not copied (PgslCommands.SharedData.cs),
        // so the game may go on changing its own while the job runs and the job never sees it.
        // Sized once: grown entry by entry, a voxel game's 2 500 structures reallocated it a dozen times a job.
        Dictionary<string, object> data = new(setup.ShareAll ? store.Count : setup.Shared.Count + DsFamilies.Length, StringComparer.Ordinal);
        // Structures the job makes get the handles they would get here, after the game's own.
        foreach (string nextKey in DsNextKeys)
            if (store.TryGetValue(nextKey, out object next)) data[nextKey] = next;
        List<SharedData> held = new(setup.ShareAll ? store.Count : setup.Shared.Count);
        void Give(string key, object value)
        {
            if (data.ContainsKey(key) || lent?.Contains(key) == true) return;
            SharedData shared = ShareEntry(store, key, value);
            if (shared == null) return;
            data[key] = shared;
            held.Add(shared);
        }
        if (setup.ShareAll)
        {
            // Sharing writes the store (an entry becomes a SharedData), so the keys are listed first,
            // into a list kept for the next job rather than one made per job.
            List<KeyValuePair<string, object>> structures = ShareAllScratch;
            structures.Clear();
            foreach (KeyValuePair<string, object> pair in store)
                if (IsStructureKey(pair.Key)) structures.Add(pair);
            foreach (KeyValuePair<string, object> pair in structures) Give(pair.Key, pair.Value);
            structures.Clear();
        }
        List<object> lentCopies = null;
        List<PendingGridCopy> pendingCopies = null;
        foreach ((string family, int handle, _, GridRect? region) in setup.Shared)
        {
            string key = DsKey(family, handle);
            if (region == null || family != "grid") { Give(key, store[key]); continue; }
            // The game goes on writing its own grid in place; only the rectangles come back. Several
            // rectangles of one grid share one copy, made on the worker as the job starts (or by the
            // game's next write of the grid, if that comes first).
            if (data.ContainsKey(key)) continue;
            PendingGridCopy pending = LendLater((PgslGrid)(store[key] is SharedData sharedGrid ? sharedGrid.Value : store[key]));
            data[key] = pending.Target;
            (lentCopies ??= []).Add(pending.Target);
            (pendingCopies ??= []).Add(pending);
        }
        // The Object's variables the job's code can read, not all of them: a game with thousands of
        // variables copied every one for each job (unless the code reads variables by name).
        KeyValuePair<string, VmValue>[] variables = null;
        if (setup.ShareAll && owner.ActiveVm is PgslVm ownerVm)
            variables = VariablesReadBy(setup.Library, setup.Function, game) is { } reads
                ? ownerVm.ShareableVariables(reads) : ownerVm.ShareableVariables();

        // The setup is the job's alone and is never changed once started: its variables go as they are.
        int copyBackCount = 0;
        foreach ((_, _, bool copyBack, _) in setup.Shared) if (copyBack) copyBackCount++;
        (string Family, int Handle, GridRect? Region)[] copyBacks = copyBackCount == 0 ? [] : new (string, int, GridRect?)[copyBackCount];
        copyBackCount = 0;
        foreach ((string family, int handle, bool copyBack, GridRect? region) in setup.Shared)
            if (copyBack) copyBacks[copyBackCount++] = (family, handle, region);
        var run = new ScriptJobRun(setup.Function, driver, setup.Library, copied, data, variables, setup.Variables, copyBacks,
            setup.Budget, game, owner.RoomWidth, owner.RoomHeight, lentCopies,
            setup.Meshes.Count == 0 ? [] : [.. setup.Meshes], setup.After.Count == 0 ? [] : [.. setup.After], pool, pendingCopies);
        // The job lets go of its data once its work has ended (or it ended without running), and owes
        // no copies of grids any more.
        if (!pool.Launch(JobId(job), token => RunScriptJob(run, token), () => { ReleaseShared(held); DropPending(pendingCopies); },
                setup.After.Count > 0 ? setup.After : null, setup.Urgent))
        {
            ReleaseShared(held);
            DropPending(pendingCopies);
            ReturnGridCopies(lentCopies);
            return Refuse("Not a prepared script job (it may have started already).");
        }
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
        "When a script job has succeeded, put the grids, lists and maps it was given with copyBack, and changed, into this Object's own (once); its result stays readable until JobRelease", "Native Jobs")]
    public static bool JobTake(double job)
    {
        NativeJobPool.Snapshot snapshot = JobSnapshot(job);
        if (snapshot.State != "succeeded")
            return Refuse("Job has not succeeded: " + snapshot.State + (snapshot.Error.Length > 0 ? ". " + snapshot.Error : "."));
        if (snapshot.Value is not ScriptJobOutcome outcome) return Refuse("Not a script job: it has no data to take.");
        if (outcome.Taken) return Refuse("This job's data was taken already.");
        // Each is the job's own (it changed it, so it made its own copy) and the job has ended: it
        // becomes the Object's structure under the same handle, in place of whatever it holds now.
        Dictionary<string, object> store = Store;
        foreach ((string family, int handle, object data, GridRect? region) in outcome.Outputs)
        {
            string key = DsKey(family, handle);
            if (store == null || !store.TryGetValue(key, out object current)) continue;
            // A part lent: its rectangle (or a list's range) is written into the game's own; everything
            // else stays the game's.
            if (region is { } rect)
            {
                if (data is PgslGrid fromJob) MergeGridRegion(handle, fromJob, rect);
                else if (data is List<object> listFromJob) MergeListRange(handle, listFromJob, rect.X1, rect.X2);
                else if (data is PgslNumberList numbersFromJob) MergeNumListRange(handle, numbersFromJob, rect.X1, rect.X2);
                continue;
            }
            object live = current is SharedData shared ? shared.Value : current;
            if (live?.GetType() == data?.GetType()) store[key] = data;
        }
        // Meshes built in the job become the game's meshes' content as built: arrays taken, not copied.
        if (outcome.Meshes is { } built)
            foreach ((int id, Genesis.Runtime.Rendering.MeshBuffers build) in built)
                Genesis.Runtime.Rendering.ScriptMeshes.TakeBuild(id, build);
        outcome.Meshes = null;
        // Then the scene changes it queued, in the order it queued them.
        if (outcome.SceneCommands is { } queued)
        {
            queued.Apply();
            SceneCommandBuffer.Return(queued);
        }
        outcome.SceneCommands = null;
        outcome.Taken = true;
        outcome.Outputs = [];
        ReturnGridCopies(outcome.LentCopies);
        outcome.LentCopies = null;
        SetJobError(string.Empty);
        return true;
    }

    // The call each job runs ("__jobResult = F(argument0, ...);"), compiled once per function, number
    // of arguments and set of Scripts rather than for every job: the compiler's work was a large part
    // of what starting a job allocated. It calls a library function only, so no command ids are in it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyDictionary<string, UserFunction>, ConcurrentDictionary<(string, int), CompileResult>> JobDrivers = new();

    private static CompileResult JobDriver(IReadOnlyDictionary<string, UserFunction> library, string function, int arguments)
    {
        CompileResult Compile()
        {
            var call = new StringBuilder("__jobResult = ").Append(function).Append('(');
            for (int i = 0; i < arguments; i++) call.Append(i == 0 ? "argument0" : ", argument" + i.ToString(CultureInfo.InvariantCulture));
            return VMEngine.Compile(call.Append(");").ToString());
        }
        if (library == null) return Compile();
        ConcurrentDictionary<(string, int), CompileResult> drivers = JobDrivers.GetOrCreateValue(library);
        if (drivers.TryGetValue((function, arguments), out CompileResult cached)) return cached;
        CompileResult driver = Compile();
        // A call that did not compile is never kept: the next job tries again.
        if (driver != null) drivers[(function, arguments)] = driver;
        return driver;
    }

    // The instance variables a job's function can read, worked out once per function and set of
    // Scripts: the names its code loads, and the code of every library function it calls.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyDictionary<string, UserFunction>, ConcurrentDictionary<string, string[]>> JobReads = new();
    private static readonly string[] EveryVariable = new string[1];

    /// <summary>The variables a job's function can read; null when it reads any by name (VariableGet...).</summary>
    private static string[] VariablesReadBy(IReadOnlyDictionary<string, UserFunction> library, string function, PgslEngineBridge game)
    {
        if (library == null || function == null || game == null) return null;
        string[] names = JobReads.GetOrCreateValue(library).GetOrAdd(function, name => FindVariablesReadBy(library, name, game) ?? EveryVariable);
        return ReferenceEquals(names, EveryVariable) ? null : names;
    }

    private static string[] FindVariablesReadBy(IReadOnlyDictionary<string, UserFunction> library, string function, PgslEngineBridge game)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(function);
        while (pending.Count > 0)
        {
            string name = pending.Pop();
            if (!visited.Add(name) || !library.TryGetValue(name, out UserFunction body) || body?.Bytecode == null) continue;
            foreach (Instruction instruction in body.Bytecode)
            {
                switch (instruction.Opcode)
                {
                    // Named as PgslProgram decodes them: an instance variable's own name, and never a
                    // function's local (which lives in its frame).
                    case Opcode.LOAD_VAR when instruction.Operand is string variable:
                        if (variable.StartsWith(PgslParser.InstanceVariablePrefix, StringComparison.Ordinal))
                            names.Add(variable[PgslParser.InstanceVariablePrefix.Length..]);
                        else if (!variable.StartsWith(PgslParser.FunctionLocalPrefix, StringComparison.Ordinal))
                            names.Add(variable);
                        break;
                    case Opcode.CALL when instruction.Operand is (string called, int):
                        if (ReadsVariablesByName(called)) return null;
                        pending.Push(called);
                        break;
                    case Opcode.CALL_NATIVE when instruction.Operand is (int id, int):
                        if (ReadsVariablesByName(game.NativeName(id))) return null;
                        break;
                }
            }
        }
        return [.. names];
    }

    // VariableGet and its kin read a variable whose name is worked out as the code runs.
    private static bool ReadsVariablesByName(string command) =>
        command != null && command.StartsWith("Variable", StringComparison.OrdinalIgnoreCase);

    /// <summary>How long a finished script job ran on its worker; 0 for any other job.</summary>
    internal static double ScriptJobWorkerMilliseconds(double job) =>
        JobSnapshot(job).Value is ScriptJobOutcome outcome ? outcome.WorkerMilliseconds : 0;

    private sealed record ScriptJobRun(string Function, CompileResult Driver, IReadOnlyDictionary<string, UserFunction> Library,
        object[] Arguments, Dictionary<string, object> Data, KeyValuePair<string, VmValue>[] SharedVariables, Dictionary<string, object> Variables,
        (string Family, int Handle, GridRect? Region)[] CopyBack, long Budget, PgslEngineBridge Game, double RoomWidth, double RoomHeight,
        List<object> LentCopies, int[] Meshes, int[] After, NativeJobPool Pool, List<PendingGridCopy> PendingCopies = null);

    // A job with no mesh lent still has a table, so a mesh command in it names the mistake rather
    // than reaching the game's meshes from a worker thread. Never written.
    private static readonly Dictionary<int, Genesis.Runtime.Rendering.MeshBuffers> NoJobMeshes = [];

    // What the jobs it waited for changed (and marked to come back) is what this job reads under
    // those handles: a copy, since the game may take the original at JobTake and change it.
    private static void ReadFinishedJobs(ScriptJobRun run, Dictionary<string, object> store)
    {
        foreach (int other in run.After)
        {
            NativeJobPool.Snapshot snapshot = run.Pool.Read(other);
            if (snapshot.State == "invalid") continue;
            if (snapshot.State != "succeeded")
                throw new PgslWorkerJobException($"Job {other.ToString(CultureInfo.InvariantCulture)}, which this job waits for, {snapshot.State}" + (snapshot.Error.Length > 0 ? ": " + snapshot.Error : "."));
            if (snapshot.Value is not ScriptJobOutcome finished) continue;
            foreach ((string family, int handle, object data, GridRect? _) in finished.Outputs)
                store[DsKey(family, handle)] = CopyStructure(data);
        }
    }

    // On a worker thread: a VM, bridge and context of the job's own; nothing of the game's is written.
    private static NativeJobPool.Result RunScriptJob(ScriptJobRun run, CancellationToken token)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ledger = PgslAllocationLedger.Enabled;
        long allocatedBefore = ledger ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long commandsBefore = ledger ? PgslAllocationLedger.CommandBytesSoFar : 0;
        PgslEngineBridge bridge = RentWorkerBridge(run.Game);
        // The job's data was made for this job alone: it becomes the job's store as it is, not copied again.
        PgslContext context = new() { RoomWidth = run.RoomWidth, RoomHeight = run.RoomHeight, Variables = run.Data };
        // Each mesh lent is built from nothing in pooled arrays of the job's own.
        context.JobMeshes = NoJobMeshes;
        if (run.Meshes.Length > 0)
        {
            context.JobMeshes = new Dictionary<int, Genesis.Runtime.Rendering.MeshBuffers>(run.Meshes.Length);
            foreach (int mesh in run.Meshes) context.JobMeshes[mesh] = new Genesis.Runtime.Rendering.MeshBuffers();
        }
        PgslContext previous = BindContext(context);
        bool handedOver = false;
        try
        {
            // The job's copies of grids lent in part, made here off the game's frame (unless the game
            // wrote the grid first and made them then).
            if (run.PendingCopies != null) foreach (PendingGridCopy pending in run.PendingCopies) Settle(pending);
            if (run.After.Length > 0) ReadFinishedJobs(run, context.Variables);
            bridge.SetContext(context);
            // The bridge's own VM, emptied: a VM per job was much of what a job allocated.
            PgslVm vm = bridge.WorkerVm ??= new PgslVm(bridge);
            vm.ResetForJob();
            vm.JobBudget = new PgslJobBudget(run.Budget, token);
            vm.LibraryFunctions = run.Library;
            context.ActiveVm = vm;
            if (run.SharedVariables != null) vm.LoadVariables(run.SharedVariables);
            foreach (KeyValuePair<string, object> pair in run.Variables) vm.SetVariable(pair.Key, pair.Value);
            vm.SetScriptArguments(run.Arguments);
            vm.Execute(run.Driver.Instructions, run.Driver.Constants, clearVariables: false);
            vm.TryReadVariable("__jobResult", out object value);
            var outcome = new ScriptJobOutcome
            {
                Result = value switch { double or bool or string => value, null => 0.0, _ => Convert.ToString(value, CultureInfo.InvariantCulture) },
            };
            // Only what the job changed comes back: one it left alone is still the shared original
            // (and the game's own may have moved on since).
            // A part lent (a region) is the job's copy, never shared: its rectangle always comes back.
            foreach ((string family, int handle, GridRect? region) in run.CopyBack)
                if (context.Variables.TryGetValue(DsKey(family, handle), out object data) && data is not SharedData)
                    outcome.Outputs.Add((family, handle, data, region));
            outcome.LentCopies = run.LentCopies;
            if (run.Meshes.Length > 0)
            {
                outcome.Meshes = new List<(int, Genesis.Runtime.Rendering.MeshBuffers)>(run.Meshes.Length);
                foreach (int mesh in run.Meshes) outcome.Meshes.Add((mesh, context.JobMeshes[mesh]));
            }
            outcome.SceneCommands = context.SceneCommands;
            handedOver = true;
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
            // A job that failed or was cancelled gives its meshes' arrays back to the pool.
            if (!handedOver && context.JobMeshes != NoJobMeshes)
                foreach (Genesis.Runtime.Rendering.MeshBuffers build in context.JobMeshes.Values) build.Release();
            context.JobMeshes = null;
            // A job that failed or was cancelled made none of its queued changes.
            if (!handedOver) SceneCommandBuffer.Return(context.SceneCommands);
            context.SceneCommands = null;
            context.ActiveVm = null;
            // The pooled VM lets go of the job's data now, not when its next job starts.
            bridge.WorkerVm?.ResetForJob();
            ReturnWorkerBridge(run.Game, bridge);
            BindContext(previous);
            if (ledger)
                PgslAllocationLedger.AddScope("job " + run.Function, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                    PgslAllocationLedger.CommandBytesSoFar - commandsBefore);
        }
    }

    #endregion
}
