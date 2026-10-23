#nullable disable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Linq.Expressions;

using System.Reflection;
using Genesis.Shared.Commands;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>Reflects <see cref="PgslCommands"/> into native VM dispatch table (no JSON).</summary>
public sealed class PgslEngineBridge : IPgslEngineBridge
{
    private readonly Dictionary<string, CommandDef> _commandDefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Member, int ArgumentCount), MethodInfo> _methodCache = new();
    private readonly Dictionary<string, PropertyInfo> _propertyCache = new();
    private static Dictionary<string, PropertyInfo> _staticContextPropCache;
    private readonly Type _hostType = typeof(PgslCommands);
    private PgslContext _context;

    private Func<object[], object>[] _nativeTable;
    private bool[] _nativeIsVoid;
    private string[] _nativeNames;
    private CommandDef[] _nativeDefs = Array.Empty<CommandDef>();
    private Dictionary<string, int> _nativeIdMap;

    public IReadOnlyDictionary<string, int> NativeIdMap => _nativeIdMap;
    public int CommandCount => _commandDefs.Count;

    /// <summary>
    /// The command name behind a native id. Compiled bytecode carries ids, not names, so a tool
    /// reporting on what a script actually called needs this to say anything a designer recognises.
    /// </summary>
    public string NativeName(int id) =>
        _nativeNames != null && id >= 0 && id < _nativeNames.Length ? _nativeNames[id] : null;

    /// <summary>The category a command is listed under ("Lists", "Variables"...), or null.</summary>
    internal string NativeCategory(int id) => (uint)id < (uint)_nativeDefs.Length ? _nativeDefs[id]?.Category : null;

    public void LoadFromRegistry()
    {
        PgslCommandRegistry.Build(_hostType);
        _commandDefs.Clear();
        _methodCache.Clear();

        foreach (var info in PgslCommandRegistry.GetCatalog())
        {
            var def = new CommandDef
            {
                Name = info.Name,
                CSharpMember = info.CSharpMember,
                Category = info.Category,
                Description = info.Description,
                IsProperty = info.IsProperty,
                IsImplemented = info.IsImplemented,
            };
            // A property is found by its C# member, not its name: Engine.Physics.Mass and a Mass of
            // another area are two properties.
            if (info.IsProperty && !string.IsNullOrEmpty(info.CSharpMember))
                def.Property = _hostType.GetProperty(info.CSharpMember[(info.CSharpMember.LastIndexOf('.') + 1)..], BindingFlags.Public | BindingFlags.Static);
            if (string.IsNullOrWhiteSpace(info.Namespace))
            {
                _commandDefs[info.Name] = def;
            }
            else
            {
                _commandDefs[info.QualifiedName] = def;
                if (info.Flat && !_commandDefs.ContainsKey(info.Name))
                    _commandDefs[info.Name] = def;
            }

            if (info.Flat && !string.IsNullOrEmpty(info.CSharpMember) && info.CSharpMember.Contains('.'))
            {
                string csharpName = info.CSharpMember.Substring(info.CSharpMember.LastIndexOf('.') + 1);
                if (!_commandDefs.ContainsKey(csharpName))
                    _commandDefs[csharpName] = def;
            }
        }
        // Engine commands (Engine.Camera3DSetPos...) are native commands too, so a script calls
        // them through a typed call (numbers unboxed, no argument array) like any other command,
        // rather than resolving the name and boxing every argument on every call.
        foreach ((string bare, string namespaced, string category, MethodInfo method) in PgslNamespaceResolver.EngineCommands())
        {
            var def = new CommandDef { Name = "Engine." + bare, Category = category, IsImplemented = true, EngineMethod = method, EngineName = bare };
            _commandDefs.TryAdd(def.Name, def);
            if (namespaced != null) _commandDefs.TryAdd(namespaced, def);
        }
        // The standard names (PgslApi): Engine.Physics.ApplyForce is PhysicsApplyForce itself, and
        // the value of a Get/Set pair is a property of its own (Engine.Physics.AirDrag).
        foreach (PgslApi.Entry entry in PgslApi.Entries)
        {
            if (_commandDefs.ContainsKey(entry.Name)) continue;
            if (entry.Command != null)
            {
                if (_commandDefs.TryGetValue(entry.Command, out CommandDef target)) _commandDefs[entry.Name] = target;
                continue;
            }
            _commandDefs[entry.Name] = new CommandDef
            {
                Name = entry.Name, Category = entry.Category, Description = entry.Description, IsProperty = true,
                IsImplemented = true, GetterMember = entry.GetterMember, SetterMember = entry.SetterMember,
            };
        }
        BuildNativeCallTable();
    }

    public object InvokeNative(int id, object[] args) => _nativeTable[id](args);
    public bool IsNativeVoid(int id) => id >= 0 && id < _nativeIsVoid?.Length && _nativeIsVoid[id];

    /// <summary>What a typed call takes for one parameter, and so which values it accepts unconverted.</summary>
    internal enum ArgumentKind : byte
    {
        /// <summary>A double, given an unboxed number.</summary>
        Number,
        /// <summary>A float, given an unboxed number.</summary>
        Single,
        /// <summary>A bool, given an unboxed number (true when not 0).</summary>
        Truth,
        /// <summary>A string, given text or nothing.</summary>
        Text,
        /// <summary>An int, given an unboxed number that fits one (converted as ConvertArg does).</summary>
        Integer,
        /// <summary>An object (a list, a map, any value), given whatever the script passes, as its object.</summary>
        Value,
    }

    /// <summary>
    /// How a command is called with a given number of arguments, worked out once: the overload,
    /// its parameters, and, when every parameter is a number, a bool or text, a compiled call that
    /// takes numbers unboxed and text as it is. The VM calls through this instead of resolving the
    /// command on every call.
    /// </summary>
    internal sealed class NativeCall
    {
        public int ArgumentCount;
        /// <summary>The command's own entry (a property, or a method resolved by name per call).</summary>
        public Func<object[], object> Entry;
        public MethodInfo Method;
        public MethodPlan Plan;
        /// <summary>The typed call's parameters; null when the command has no typed call.</summary>
        public ArgumentKind[] Kinds;
        public bool HasText;
        public Func<double[], object[], double> NumberCall;
        public Func<double[], object[], bool> BoolCall;
        public Action<double[], object[]> VoidCall;
        public Func<double[], object[], object> ObjectCall;
        /// <summary>
        /// For a command taking up to four numbers (doubles) and returning a number or nothing
        /// (<c>DsGridGet</c>, <c>DsListGet</c>, <c>DsGridSet</c>, <c>Floor</c>...): a delegate straight to
        /// the method, called with the numbers off the VM's stack, no arrays in between.
        /// </summary>
        public Delegate Direct;
        public DirectShape Shape;
        /// <summary>Whether the call leaves nothing for the script (<see cref="IsNativeVoid"/>), worked out once.</summary>
        public bool PushesNothing;
        /// <summary>An Engine command: values the typed call cannot take go through the command pipeline.</summary>
        public bool IsEngine;
    }

    /// <summary>The form of <see cref="NativeCall.Direct"/>: how many numbers it takes and whether it returns one.</summary>
    internal enum DirectShape : byte
    {
        None,
        Number0, Number1, Number2, Number3, Number4,
        Void1, Void2, Void3, Void4,
    }

    /// <summary>A method's parameters as argument conversion needs them, read by reflection once.</summary>
    internal sealed class MethodPlan
    {
        public ParameterInfo[] Parameters;
        public bool[] IsParams;
        public Type[] ParamsElementTypes;
        /// <summary>A params parameter marked <see cref="Genesis.Shared.Scripting.ParamsNotKeptAttribute"/>.</summary>
        public bool[] ParamsReusable;
        public MethodInfo Method;
        private Func<object[], object> _invoke;

        /// <summary>
        /// The call through an argument array, made the first time a call needs it: most commands
        /// are called through their typed call, and compiling this one too for every command a game
        /// used was a sixth of GenesisCraft's first Create event.
        /// </summary>
        public Func<object[], object> Invoke => _invoke ??= Invokers.GetOrAdd(Method, CompileMethod);
    }

    // One for each method, whichever bridge (the game's or a worker's) first needs it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodInfo, Func<object[], object>> Invokers = new();

    // Per-thread params arrays by element type and length, for commands that do not keep them
    // (job scripts call commands on worker threads, hence per thread).
    [ThreadStatic] private static Dictionary<(Type, int), Array> _paramsScratch;

    private static Array ParamsArray(Type elementType, int count, bool reusable)
    {
        if (!reusable || count == 0 || count > 32) return Array.CreateInstance(elementType, count);
        _paramsScratch ??= new Dictionary<(Type, int), Array>();
        if (!_paramsScratch.TryGetValue((elementType, count), out Array array))
            _paramsScratch[(elementType, count)] = array = Array.CreateInstance(elementType, count);
        return array;
    }

    private NativeCall[] _nativeCalls = Array.Empty<NativeCall>();
    private readonly Dictionary<MethodInfo, MethodPlan> _planCache = new();

    /// <summary>Generation of the command table: what the VM remembers about a name (not a property) holds for one generation.</summary>
    internal int Generation { get; private set; }

    internal NativeCall GetNativeCall(int id, int argumentCount)
    {
        NativeCall[] calls = _nativeCalls;
        if ((uint)id < (uint)calls.Length && calls[id] is { } cached && cached.ArgumentCount == argumentCount)
            return cached;
        return CreateNativeCall(id, argumentCount);
    }

    private NativeCall CreateNativeCall(int id, int argumentCount)
    {
        var call = new NativeCall { ArgumentCount = argumentCount, Entry = _nativeTable[id], PushesNothing = IsNativeVoid(id) };
        CommandDef def = _nativeDefs[id];
        // A command a worker job may not run keeps only its entry, which says so.
        if (def.EngineMethod != null)
        {
            // Typed straight to the Engine method, its time recorded as the pipeline records it.
            // While anything hooks the pipeline (gating, auditing), every call goes through it.
            call.IsEngine = true;
            if ((_workerAllows == null || _workerAllows[id]) && !EngineCommandPipeline.HasHooks)
            {
                call.Method = def.EngineMethod;
                call.Plan = GetPlan(call.Method);
                CompileTypedCall(call, def.EngineName);
            }
        }
        else if (!def.IsProperty && (_workerAllows == null || _workerAllows[id]))
        {
            // The same overload CallMethod picks for this many arguments.
            call.Method = ResolveMethod(def.CSharpMember, argumentCount);
            if (call.Method != null)
            {
                call.Plan = GetPlan(call.Method);
                CompileTypedCall(call);
            }
        }
        if ((uint)id < (uint)_nativeCalls.Length) _nativeCalls[id] = call;
        return call;
    }

    // Commands whose parameters are all numbers (double, float, int), bools, text or plain objects,
    // called with exactly that many: a compiled call over reusable arrays (no object[] of boxed
    // numbers, no per-argument conversion, no boxed result). It does what ConvertArg does for those
    // values: a number given for a float is narrowed, for an int rounded (Convert.ToInt32), for a bool
    // true when not 0; text is passed as is, and so is any value given for an object parameter
    // (PgListSize(list), DsMapGet(map, key)...). Any other value (text for a number, a number too big
    // for an int...) takes the general path, which converts it or says why it cannot.
    private static void CompileTypedCall(NativeCall call, string engineName = null)
    {
        ParameterInfo[] parameters = call.Plan.Parameters;
        // Fewer arguments than parameters is the same typed call when the rest have defaults
        // (DrawText(x, y, text) for a command with an optional scale): those calls took the general
        // path and boxed their results.
        int given = call.ArgumentCount;
        if (given > parameters.Length) return;
        for (int i = given; i < parameters.Length; i++)
            if (!parameters[i].HasDefaultValue || call.Plan.IsParams[i]) return;
        var kinds = new ArgumentKind[given];
        for (int i = 0; i < given; i++)
        {
            Type type = parameters[i].ParameterType;
            if (call.Plan.IsParams[i]) return;
            if (type == typeof(double)) kinds[i] = ArgumentKind.Number;
            else if (type == typeof(float)) kinds[i] = ArgumentKind.Single;
            else if (type == typeof(bool)) kinds[i] = ArgumentKind.Truth;
            else if (type == typeof(string)) kinds[i] = ArgumentKind.Text;
            else if (type == typeof(int)) kinds[i] = ArgumentKind.Integer;
            else if (type == typeof(object)) kinds[i] = ArgumentKind.Value;
            else return;
        }

        var numbers = Expression.Parameter(typeof(double[]), "numbers");
        var texts = Expression.Parameter(typeof(object[]), "texts");
        var arguments = new Expression[parameters.Length];
        for (int i = given; i < parameters.Length; i++)
            arguments[i] = DefaultArgument(parameters[i]);
        for (int i = 0; i < given; i++)
        {
            Expression index = Expression.Constant(i);
            arguments[i] = kinds[i] switch
            {
                ArgumentKind.Single => Expression.Convert(Expression.ArrayIndex(numbers, index), typeof(float)),
                ArgumentKind.Truth => Expression.NotEqual(Expression.ArrayIndex(numbers, index), Expression.Constant(0.0)),
                ArgumentKind.Text => Expression.Convert(Expression.ArrayIndex(texts, index), typeof(string)),
                ArgumentKind.Integer => Expression.Call(ToInt32FromDouble, Expression.ArrayIndex(numbers, index)),
                ArgumentKind.Value => Expression.ArrayIndex(texts, index),
                _ => Expression.ArrayIndex(numbers, index),
            };
        }
        Expression body = Expression.Call(null, call.Method, arguments);
        Type returns = call.Method.ReturnType;
        if (engineName != null)
        {
            // started = Stopwatch.GetTimestamp(); result = Method(...); RecordElapsed(name, started); result
            ParameterExpression started = Expression.Variable(typeof(long), "started");
            Expression record = Expression.Call(RecordElapsedMethod, Expression.Constant(engineName), started);
            Expression start = Expression.Assign(started, Expression.Call(GetTimestampMethod));
            if (returns == typeof(void)) body = Expression.Block([started], start, body, record);
            else
            {
                ParameterExpression result = Expression.Variable(returns, "result");
                body = Expression.Block(returns, [started, result], start, Expression.Assign(result, body), record, result);
            }
        }
        if (returns == typeof(double)) call.NumberCall = Expression.Lambda<Func<double[], object[], double>>(body, numbers, texts).Compile();
        else if (returns == typeof(bool)) call.BoolCall = Expression.Lambda<Func<double[], object[], bool>>(body, numbers, texts).Compile();
        else if (returns == typeof(void)) call.VoidCall = Expression.Lambda<Action<double[], object[]>>(body, numbers, texts).Compile();
        else call.ObjectCall = Expression.Lambda<Func<double[], object[], object>>(Expression.Convert(body, typeof(object)), numbers, texts).Compile();
        call.Kinds = kinds;
        call.HasText = Array.IndexOf(kinds, ArgumentKind.Text) >= 0 || Array.IndexOf(kinds, ArgumentKind.Value) >= 0;
        // An Engine command keeps its timed typed call: a direct delegate would skip the record.
        if (engineName == null) CreateDirectCall(call, kinds);
    }

    private static readonly MethodInfo ToInt32FromDouble = typeof(Convert).GetMethod(nameof(Convert.ToInt32), new[] { typeof(double) })!;
    private static readonly MethodInfo RecordElapsedMethod = typeof(EngineCommandPipeline).GetMethod(nameof(EngineCommandPipeline.RecordElapsed))!;
    private static readonly MethodInfo GetTimestampMethod = typeof(System.Diagnostics.Stopwatch).GetMethod(nameof(System.Diagnostics.Stopwatch.GetTimestamp))!;

    // A parameter's declared default as a constant of its own type (an enum's comes as its number).
    private static Expression DefaultArgument(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;
        object value = parameter.DefaultValue;
        if (value == null) return Expression.Default(type);
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum) value = Enum.ToObject(underlying, value);
        else if (value.GetType() != underlying) value = Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture);
        return Expression.Constant(value, type);
    }

    // The same method as the typed call, bound as a plain delegate when it takes only doubles.
    private static void CreateDirectCall(NativeCall call, ArgumentKind[] kinds)
    {
        if (!ScriptingDebugSettings.VmFastCalls || kinds.Length > 4 || Array.Exists(kinds, kind => kind != ArgumentKind.Number)
            || call.Plan.Parameters.Length != kinds.Length) return; // defaults left to the typed call
        Type returns = call.Method.ReturnType;
        (Type type, DirectShape shape) = (returns == typeof(double), kinds.Length) switch
        {
            (true, 0) => (typeof(Func<double>), DirectShape.Number0),
            (true, 1) => (typeof(Func<double, double>), DirectShape.Number1),
            (true, 2) => (typeof(Func<double, double, double>), DirectShape.Number2),
            (true, 3) => (typeof(Func<double, double, double, double>), DirectShape.Number3),
            (true, 4) => (typeof(Func<double, double, double, double, double>), DirectShape.Number4),
            (false, 1) when returns == typeof(void) => (typeof(Action<double>), DirectShape.Void1),
            (false, 2) when returns == typeof(void) => (typeof(Action<double, double>), DirectShape.Void2),
            (false, 3) when returns == typeof(void) => (typeof(Action<double, double, double>), DirectShape.Void3),
            (false, 4) when returns == typeof(void) => (typeof(Action<double, double, double, double>), DirectShape.Void4),
            _ => (null, DirectShape.None),
        };
        if (type == null) return;
        call.Direct = call.Method.CreateDelegate(type);
        call.Shape = shape;
    }

    /// <summary>Calls a command through its cached plan: the same conversions and errors as <see cref="InvokeNative"/>.</summary>
    internal object InvokeNative(NativeCall call, object[] args) =>
        call.Method == null || call.IsEngine ? call.Entry(args) : CallMethod(call.Method, call.Plan, args);

    public void BuildNativeCallTable()
    {
        // One id per command: its other names (Engine.Physics.ApplyForce, a namespaced command's
        // bare name) share it, so a call by any of its names is the same call.
        var ids = new Dictionary<CommandDef, int>();
        var defs = new List<CommandDef>(_commandDefs.Count);
        var keys = new List<string>(_commandDefs.Count);
        _nativeIdMap = new Dictionary<string, int>(_commandDefs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, CommandDef> pair in _commandDefs)
        {
            if (!ids.TryGetValue(pair.Value, out int id))
            {
                id = defs.Count;
                ids.Add(pair.Value, id);
                defs.Add(pair.Value);
                keys.Add(pair.Key);
            }
            _nativeIdMap[pair.Key] = id;
        }
        _nativeTable = new Func<object[], object>[defs.Count];
        _nativeIsVoid = new bool[defs.Count];
        _nativeNames = new string[defs.Count];
        _nativeDefs = new CommandDef[defs.Count];
        _nativeCalls = new NativeCall[defs.Count];
        Generation++;

        for (int i = 0; i < defs.Count; i++)
        {
            var def = defs[i];
            _nativeNames[i] = def.Name ?? keys[i];
            _nativeDefs[i] = def;

            if (def.EngineMethod != null)
            {
                string engineCommand = def.Name;
                _nativeTable[i] = args => PgslNamespaceResolver.InvokeEngineCommand(engineCommand, args);
                _nativeIsVoid[i] = def.EngineMethod.ReturnType == typeof(void);
            }
            else if (def.IsProperty)
            {
                var captured = def;
                _nativeTable[i] = args =>
                {
                    if (args.Length == 0) return ReadProperty(captured);
                    WriteProperty(captured, args[0]);
                    return null;
                };
                _nativeIsVoid[i] = false;
            }
            else
            {
                var captured = def;
                _nativeTable[i] = args => CallMethod(captured.CSharpMember, args);
                var method = ResolveMethod(captured.CSharpMember, 0);
                _nativeIsVoid[i] = method != null && method.ReturnType == typeof(void);
            }
        }
    }

    public void SetContext(PgslContext context)
    {
        _context = context;
        InitializeContextPropCache();
        PgslCommands.SetContext(context);
    }

    public PgslContext GetContext() => _context;

    public IEnumerable<object> FindObjects(string objName) => _workerAllows != null
        ? throw new PgslWorkerJobException("with is not available in a worker job: a job has no instances.")
        : Genesis.Runtime.Scripting.PgslBehavior.FindTargets(objName);

    // ── Worker jobs ─────────────────────────────────────────────────────────────
    // A bridge of its own for a VM that runs a script function on a worker thread: the same
    // command ids, so bytecode compiled for the game runs unchanged; caches of its own, so nothing
    // the game's bridge holds is written from another thread; and only the commands a worker may
    // run (worked out once per command). Any other command, property or Script raises an error.

    private bool[] _workerAllows;
    private Func<string, string, bool> _workerPredicate;

    /// <summary>A worker bridge's VM, reused by each job the bridge serves (one at a time).</summary>
    internal PgslVm WorkerVm;

    /// <summary>A worker job's bridge (only the commands a job may run).</summary>
    internal bool IsWorker => _workerAllows != null;

    /// <summary>Whether a name is a property command (an assignment to it sets the property, not a variable).</summary>
    internal bool IsDeclaredProperty(string name) => name != null && _commandDefs.TryGetValue(name, out CommandDef def) && def.IsProperty;

    /// <summary>
    /// A copy of a built bridge's command table for worker jobs. It may be made on a worker thread:
    /// it only reads the game's tables, which are not changed once built (a rebuild replaces them).
    /// </summary>
    internal static PgslEngineBridge CreateWorker(PgslEngineBridge game, Func<string, string, bool> allows)
    {
        var worker = new PgslEngineBridge { _workerPredicate = allows };
        foreach (KeyValuePair<string, CommandDef> pair in game._commandDefs) worker._commandDefs[pair.Key] = pair.Value;
        // The game's tables are replaced, never changed, when it rebuilds them: these stay as they are.
        worker._nativeIdMap = game._nativeIdMap;
        worker._nativeNames = game._nativeNames;
        worker._nativeIsVoid = game._nativeIsVoid;
        worker._nativeDefs = game._nativeDefs;
        int count = game._nativeDefs.Length;
        worker._nativeCalls = new NativeCall[count];
        worker._nativeTable = new Func<object[], object>[count];
        worker._workerAllows = new bool[count];
        for (int i = 0; i < count; i++)
        {
            CommandDef def = game._nativeDefs[i];
            bool allowed = def != null && allows(def.Name, def.Category);
            worker._workerAllows[i] = allowed;
            if (def == null) continue;
            if (!allowed)
            {
                string name = def.Name;
                worker._nativeTable[i] = _ => throw NotInWorker(name);
            }
            else if (def.IsProperty)
            {
                worker._nativeTable[i] = args =>
                {
                    if (args.Length == 0) return worker.ReadProperty(def);
                    worker.WriteProperty(def, args[0]);
                    return null;
                };
            }
            else if (def.EngineMethod != null)
            {
                string engineCommand = def.Name;
                worker._nativeTable[i] = args => PgslNamespaceResolver.InvokeEngineCommand(engineCommand, args);
            }
            else
            {
                worker._nativeTable[i] = args => worker.CallMethod(def.CSharpMember, args);
            }
        }
        return worker;
    }

    private static PgslWorkerJobException NotInWorker(string name) =>
        new($"{name} is not available in a worker job: a job runs maths, text, noise and its own grids, lists and maps only.");

    private bool WorkerAllows(CommandDef def) => _workerPredicate(def.Name, def.Category);

    private object InvokeInWorker(string name, object[] args)
    {
        args ??= Array.Empty<object>();
        if (!_commandDefs.TryGetValue(name, out CommandDef def))
        {
            if (args.Length == 0)
                throw new PgslWorkerJobException(
                    $"'{name}' has no value in this job: a worker job sees only its arguments, what it was given (JobScriptVariable, JobScriptShareAll) and what it sets itself.");
            if (name.Contains('.')) throw NotInWorker(name);
            if (Genesis.Runtime.Scripting.ScriptAssetRegistry.IsLoaded(name))
                throw new PgslWorkerJobException(
                    $"'{name}' is a Script: a worker job runs functions of the project's Scripts, not a Script's own code. Put the work in a function and call that.");
            throw new InvalidOperationException($"Unknown command: {name}");
        }
        if (!WorkerAllows(def)) throw NotInWorker(def.Name);
        if (def.EngineMethod != null) return PgslNamespaceResolver.InvokeEngineCommand(def.Name, args);
        if (def.IsProperty)
        {
            if (args.Length == 0) return ReadProperty(def);
            WriteProperty(def, args[0]);
            return null;
        }
        return CallMethod(def.CSharpMember, args);
    }

    public bool IsVoid(string name, int argCount)
    {
        if (_workerAllows == null && ScriptAssetRegistry.TryGet(name?.Trim() ?? "", out _))
            return false;

        if (_commandDefs.TryGetValue(name, out var def))
        {
            if (def.IsProperty) return false;
            if (def.EngineMethod != null) return def.EngineMethod.ReturnType == typeof(void);
            var method = GetOrCacheMethod(def.CSharpMember, argCount);
            return method != null && method.ReturnType == typeof(void);
        }

        if (PgslNamespaceResolver.TryResolveEngineCommand(name, out _, out var engineMethod))
            return engineMethod.ReturnType == typeof(void);

        var fallback = GetOrCacheMethod("PgslCommands." + name, argCount);
        return fallback != null && fallback.ReturnType == typeof(void);
    }

    public object Invoke(string name, object[] args)
    {
        if (TryInvokeKnown(name, args, out object result))
            return result;
        VMLogger.LogWarn1D($"Unknown PGSL command: {name}");
        // A Script that did not compile (or a library whose function this is) is unknown for
        // that reason, and the error says which and where.
        throw new InvalidOperationException(Genesis.Runtime.Scripting.ScriptAssetRegistry.DescribeUnknown(name));
    }

    /// <summary>
    /// <see cref="Invoke"/> for a name that may be nothing: false instead of an exception when no
    /// Script, command, property or context value has that name. A variable read before anything
    /// set it threw and caught an exception on every read (GenesisCraft's title, every frame).
    /// Errors inside a command still throw, as from <see cref="Invoke"/>.
    /// </summary>
    public bool TryInvokeKnown(string name, object[] args, out object result)
    {
        result = null;
        if (_workerAllows != null)
        {
            result = InvokeInWorker(name, args);
            return true;
        }

        if (TryInvokeScriptAsset(name, args, out var scriptResult))
        {
            result = scriptResult;
            return true;
        }

        if (!_commandDefs.TryGetValue(name, out var def))
        {
            if (PgslNamespaceResolver.TryResolveEngineCommand(name, out _, out _))
            {
                result = PgslNamespaceResolver.InvokeEngineCommand(name, args);
                return true;
            }

            var propRef = GetOrCacheProperty(name);
            if (propRef != null)
            {
                if (args.Length == 0) result = propRef.GetValue(null);
                else propRef.SetValue(null, ConvertArg(args[0], propRef.PropertyType));
                return true;
            }

            var methodRef = GetOrCacheMethod("PgslCommands." + name, args?.Length ?? 0);
            if (methodRef != null)
            {
                result = CallMethod("PgslCommands." + name, args);
                return true;
            }

            if (_context != null)
            {
                var prop = ResolveContextProperty(name);
                if (prop != null)
                {
                    if (args.Length == 0)
                    {
                        result = prop.GetValue(_context);
                        return true;
                    }
                    if (args.Length == 1 && prop.CanWrite)
                    {
                        prop.SetValue(_context, ConvertArg(args[0], prop.PropertyType));
                        return true;
                    }
                }
            }
            return false;
        }

        if (def.EngineMethod != null)
        {
            result = PgslNamespaceResolver.InvokeEngineCommand(def.Name, args);
            return true;
        }

        if (def.IsProperty)
        {
            if (args.Length == 0) result = ReadProperty(def);
            else WriteProperty(def, args[0]);
            return true;
        }

        result = CallMethod(def.CSharpMember, args);
        return true;
    }

    public bool TrySetProperty(string name, object value)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !_commandDefs.TryGetValue(name, out CommandDef def)
            || !def.IsProperty)
        {
            return false;
        }
        if (_workerAllows != null && !WorkerAllows(def)) throw NotInWorker(def.Name);
        WriteProperty(def, value);
        return true;
    }

    internal bool TrySetTypedProperty(string name, VmValue value)
    {
        if (string.IsNullOrWhiteSpace(name) || !_commandDefs.TryGetValue(name, out CommandDef def) || !def.IsProperty)
            return false;
        if (_workerAllows != null && !WorkerAllows(def)) throw NotInWorker(def.Name);
        WriteProperty(def, value.ToObject()); return true;
    }

    // A property's value: its C# property, or the commands of a Get/Set pair (PgslApi: the value
    // Engine.Physics.AirDrag is read by PhysicsGetAirDrag and set by PhysicsSetAirDrag).
    private object ReadProperty(CommandDef def)
    {
        if (def.Property != null)
        {
            if (!def.Property.CanRead) throw new InvalidOperationException($"{def.Name} can be set but not read.");
            return def.Property.GetValue(null);
        }
        if (def.GetterMember != null) return CallMethod(def.GetterMember, Array.Empty<object>());
        if (def.SetterMember != null) throw new InvalidOperationException($"{def.Name} can be set but not read.");
        return GetOrCacheProperty(def.Name)?.GetValue(null);
    }

    private void WriteProperty(CommandDef def, object value)
    {
        if (def.Property != null)
        {
            if (!def.Property.CanWrite) throw new InvalidOperationException($"{def.Name} can be read but not set.");
            def.Property.SetValue(null, ConvertArg(value, def.Property.PropertyType));
            return;
        }
        if (def.SetterMember != null) { CallMethod(def.SetterMember, new[] { value }); return; }
        if (def.GetterMember != null) throw new InvalidOperationException($"{def.Name} can be read but not set.");
        PropertyInfo property = GetOrCacheProperty(def.Name);
        property?.SetValue(null, ConvertArg(value, property.PropertyType));
    }

    private sealed class CommandDef
    {
        public string Name;
        public string CSharpMember;
        public string Category;
        public string Description;
        public bool IsProperty;
        public bool IsImplemented;
        /// <summary>For an Engine command: its method on <see cref="Engine"/> and its bare name in the pipeline.</summary>
        public MethodInfo EngineMethod;
        public string EngineName;
        /// <summary>A property command's C# property.</summary>
        public PropertyInfo Property;
        /// <summary>A property made from a Get/Set pair (PgslApi): the commands that read and set it.</summary>
        public string GetterMember, SetterMember;
    }

    // A context value read by name (TryInvokeKnown's last step), as a getter made once per name: each
    // read had normalised the name into two new strings, looked the property up and boxed its value.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<object, VmValue>> ContextReads = new(StringComparer.Ordinal);
    private static readonly Func<object, VmValue> NoContextRead = _ => default;

    /// <summary>
    /// Reads a context value as <see cref="TryInvokeKnown"/> would (when nothing earlier in its order
    /// has the name), without boxing; false leaves the read to it.
    /// </summary>
    internal bool TryReadContextValue(string name, out VmValue value)
    {
        value = default;
        if (_workerAllows != null || _context == null || string.IsNullOrWhiteSpace(name)) return false;
        if (!ContextReads.TryGetValue(name, out Func<object, VmValue> read))
        {
            read = BuildContextRead(name) ?? NoContextRead;
            if (ContextReads.Count < 4096) ContextReads[name] = read;
        }
        // A Script of that name made since comes first, as in TryInvokeKnown.
        if (ReferenceEquals(read, NoContextRead) || ScriptAssetRegistry.TryGet(name.Trim(), out _)) return false;
        value = read(_context);
        return true;
    }

    private Func<object, VmValue> BuildContextRead(string name)
    {
        if (ScriptAssetRegistry.TryGet(name.Trim(), out _) || name.StartsWith("scr_", StringComparison.OrdinalIgnoreCase)
            || _commandDefs.ContainsKey(name) || PgslNamespaceResolver.TryResolveEngineCommand(name, out _, out _)
            || GetOrCacheProperty(name) != null || GetOrCacheMethod("PgslCommands." + name, 0) != null)
            return null;
        PropertyInfo property = ResolveContextProperty(name);
        if (property?.GetMethod is not { IsStatic: false } || property.GetIndexParameters().Length > 0) return null;
        Type type = property.PropertyType;
        var context = Expression.Parameter(typeof(object), "context");
        Expression read = Expression.Property(Expression.Convert(context, property.DeclaringType!), property);
        if (type == typeof(float) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) read = Expression.Convert(read, typeof(double));
        else if (type != typeof(double) && type != typeof(int) && type != typeof(bool) && type != typeof(string)) return null;
        return Expression.Lambda<Func<object, VmValue>>(Expression.Convert(read, typeof(VmValue)), context).Compile();
    }

    private void InitializeContextPropCache()
    {
        if (_staticContextPropCache != null) return;
        var cache = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in typeof(PgslContext).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            cache[prop.Name.Replace("_", "").ToLowerInvariant()] = prop;
        _staticContextPropCache = cache;
    }

    private PropertyInfo ResolveContextProperty(string name)
    {
        var key = name.Replace("_", "").ToLowerInvariant();
        return _staticContextPropCache != null && _staticContextPropCache.TryGetValue(key, out var prop) ? prop : null;
    }

    private object CallMethod(string csharpMember, object[] args)
    {
        args ??= Array.Empty<object>();
        var method = ResolveMethod(csharpMember, args.Length);
        if (method == null)
            throw new InvalidOperationException($"Method not found: {csharpMember} with {args.Length} arguments");
        return CallMethod(method, GetPlan(method), args);
    }

    private MethodPlan GetPlan(MethodInfo method)
    {
        if (_planCache.TryGetValue(method, out MethodPlan plan)) return plan;
        ParameterInfo[] parameters = method.GetParameters();
        plan = new MethodPlan
        {
            Parameters = parameters,
            IsParams = new bool[parameters.Length],
            ParamsElementTypes = new Type[parameters.Length],
            ParamsReusable = new bool[parameters.Length],
            Method = method,
        };
        for (int i = 0; i < parameters.Length; i++)
        {
            plan.IsParams[i] = parameters[i].IsDefined(typeof(ParamArrayAttribute), false);
            if (plan.IsParams[i]) plan.ParamsElementTypes[i] = parameters[i].ParameterType.GetElementType() ?? typeof(object);
            plan.ParamsReusable[i] = plan.IsParams[i] && parameters[i].IsDefined(typeof(Genesis.Shared.Scripting.ParamsNotKeptAttribute), false);
        }
        _planCache[method] = plan;
        return plan;
    }

    private static object CallMethod(MethodInfo method, MethodPlan plan, object[] args)
    {
        args ??= Array.Empty<object>();
        ParameterInfo[] parameters = plan.Parameters;
        if (parameters.Length == 0) return plan.Invoke(Array.Empty<object>());

        object[] callArgs = ArrayPool<object>.Shared.Rent(parameters.Length);
        try
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                if (plan.IsParams[i])
                {
                    Type elementType = plan.ParamsElementTypes[i];
                    int count = Math.Max(0, args.Length - i);
                    Array tail = ParamsArray(elementType, count, plan.ParamsReusable[i]);
                    for (int j = 0; j < count; j++) tail.SetValue(ConvertArg(args[i + j], elementType), j);
                    callArgs[i] = tail;
                }
                else if (i < args.Length)
                    callArgs[i] = ConvertArgument(method, parameters[i], i, args[i]);
                else if (parameters[i].HasDefaultValue && parameters[i].DefaultValue != DBNull.Value)
                    callArgs[i] = parameters[i].DefaultValue;
                else
                {
                    Genesis.Runtime.Scripting.PgslRuntimeDiagnostics.Note(Genesis.Runtime.Scripting.PgslNoteKind.MissingArgument, method.Name,
                        $"{method.Name} takes {parameters.Length} arguments and was given {args.Length}; '{parameters[i].Name}' was filled with 0.");
                    callArgs[i] = parameters[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(parameters[i].ParameterType)
                        : null;
                }
            }
            return plan.Invoke(callArgs);
        }
        finally
        {
            // A reused params array lets go of this call's values.
            for (int i = 0; i < parameters.Length; i++)
                if (plan.ParamsReusable[i] && callArgs[i] is Array reused) Array.Clear(reused);
            Array.Clear(callArgs, 0, parameters.Length);
            ArrayPool<object>.Shared.Return(callArgs);
        }
    }

    private static Func<object[], object> CompileMethod(MethodInfo method)
    {
        var paramsParameter = Expression.Parameter(typeof(object[]), "args");
        var parameterExpressions = new List<Expression>();
        var parameters = method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            var indexExpression = Expression.ArrayIndex(paramsParameter, Expression.Constant(i));
            parameterExpressions.Add(Expression.Convert(indexExpression, parameters[i].ParameterType));
        }

        Expression callExpression = Expression.Call(null, method, parameterExpressions);
        if (method.ReturnType == typeof(void))
        {
            var executeBlock = Expression.Block(callExpression, Expression.Constant(null, typeof(object)));
            return Expression.Lambda<Func<object[], object>>(executeBlock, paramsParameter).Compile();
        }

        return Expression.Lambda<Func<object[], object>>(
            Expression.Convert(callExpression, typeof(object)), paramsParameter).Compile();
    }

    // A value of the wrong kind names the command and the argument, and the command for text
    // when there is one (DsListAdd with text: DsListAddString).
    private static object ConvertArgument(MethodInfo method, ParameterInfo parameter, int index, object value)
    {
        try { return ConvertArg(value, parameter.ParameterType); }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            string kind = parameter.ParameterType == typeof(string) ? "text"
                : parameter.ParameterType == typeof(bool) ? "true or false" : "a number";
            string given = value is string text ? $"the text \"{text}\"" : $"'{value}'";
            string hint = value is string && method.DeclaringType?.GetMethod(method.Name + "String") != null
                ? $" Use {method.Name}String for text." : string.Empty;
            throw new InvalidOperationException(
                $"{method.Name}: argument {index + 1} ({parameter.Name}) must be {kind}, but was given {given}.{hint}", exception);
        }
    }

    private static object ConvertArg(object value, Type targetType)
    {
        if (value == null) return null;
        if (targetType.IsInstanceOfType(value)) return value;
        if (value is Color c)
        {
            if (targetType == typeof(double)) return (double)c.ToArgb();
            if (targetType == typeof(int)) return c.ToArgb();
            if (targetType == typeof(string)) return c.Name;
        }
        // Text holding a number reads the same on every machine, whatever its language settings.
        if (targetType == typeof(double)) return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        if (targetType == typeof(float)) return (float)Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        if (targetType == typeof(int)) return Convert.ToInt32(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
        if (targetType == typeof(bool)) return Convert.ToBoolean(value);
        if (targetType == typeof(string)) return value.ToString();
        if (targetType == typeof(Color))
        {
            try { return Color.FromArgb(Convert.ToInt32(Convert.ToDouble(value))); }
            catch { }
        }
        return value;
    }

    private MethodInfo GetOrCacheMethod(string csharpMember, int argCount) =>
        ResolveMethod(csharpMember, argCount);

    private MethodInfo ResolveMethod(string csharpMember, int suppliedArgCount)
    {
        var key = (csharpMember, suppliedArgCount);
        if (_methodCache.TryGetValue(key, out var cached)) return cached;

        string methodName = csharpMember.Contains('.')
            ? csharpMember[(csharpMember.LastIndexOf('.') + 1)..]
            : csharpMember;

        MethodInfo exact = null;
        MethodInfo best = null;
        int bestParamCount = -1;

        foreach (var m in _hostType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!m.Name.Equals(methodName, StringComparison.Ordinal)) continue;
            var parameters = m.GetParameters();
            if (suppliedArgCount > parameters.Length && !parameters.LastOrDefault()?.IsDefined(typeof(ParamArrayAttribute), false) == true)
                continue;
            if (parameters.Length == suppliedArgCount) { exact = m; break; }
            if (parameters.Length > bestParamCount) { best = m; bestParamCount = parameters.Length; }
        }

        var resolved = exact ?? best;
        _methodCache[key] = resolved;
        return resolved;
    }

    // Typed getters for the engine's number, whole-number and true/false properties: DeltaTime and
    // the like, read by most scripts every frame, were boxed by the reflection call on every read
    // (Kinfield).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodInfo, Func<VmValue>> TypedGetters = new();

    /// <summary>
    /// Reads an engine property as <see cref="TryInvokeKnown"/> would, without boxing it, when the
    /// name is a plain number, whole-number or true/false property; false leaves the read to
    /// <see cref="TryInvokeKnown"/>.
    /// </summary>
    internal bool TryReadTypedProperty(string name, out VmValue value)
    {
        value = default;
        // In TryInvokeKnown's order: a worker's reads and Script resources keep their own way; then
        // a registered property command (DeltaTime), or a plain property no command or namespaced
        // name claims.
        if (_workerAllows != null || string.IsNullOrWhiteSpace(name) || NotTypedProperties.ContainsKey(name)
            || name.StartsWith("scr_", StringComparison.OrdinalIgnoreCase) || ScriptAssetRegistry.TryGet(name.Trim(), out _))
            return false;
        MethodInfo getter;
        if (_commandDefs.TryGetValue(name, out CommandDef def))
        {
            if (!def.IsProperty) return NotTyped(name);
            // A property of a Get/Set pair reads through its getter command, when that takes nothing.
            getter = def.Property != null ? def.Property.GetMethod
                : def.GetterMember != null ? ResolveMethod(def.GetterMember, 0)
                : GetOrCacheProperty(def.Name)?.GetMethod;
        }
        else if (PgslNamespaceResolver.TryResolveEngineCommand(name, out _, out _))
            return NotTyped(name);
        else getter = GetOrCacheProperty(name)?.GetMethod;
        if (getter is not { IsStatic: true } || getter.GetParameters().Length != 0) return NotTyped(name);
        Func<VmValue> read = TypedGetters.GetOrAdd(getter, static method =>
        {
            if (method.ReturnType == typeof(double)) { var number = method.CreateDelegate<Func<double>>(); return () => number(); }
            if (method.ReturnType == typeof(float)) { var single = method.CreateDelegate<Func<float>>(); return () => single(); }
            if (method.ReturnType == typeof(int)) { var whole = method.CreateDelegate<Func<int>>(); return () => whole(); }
            if (method.ReturnType == typeof(bool)) { var flag = method.CreateDelegate<Func<bool>>(); return () => flag(); }
            return null;
        });
        if (read == null) return NotTyped(name);
        value = read();
        return true;
    }

    // Names found not to be typed engine properties (an instance's x and y, a context value),
    // remembered so each later read goes straight to TryInvokeKnown: every read of x had paid for
    // these lookups before falling back.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NotTypedProperties = new(StringComparer.Ordinal);

    private static bool NotTyped(string name)
    {
        if (NotTypedProperties.Count < 4096) NotTypedProperties.TryAdd(name, true);
        return false;
    }

    private PropertyInfo GetOrCacheProperty(string pgslName)
    {
        if (_propertyCache.TryGetValue(pgslName, out var cached)) return cached;
        foreach (var p in _hostType.GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            foreach (var attr in p.GetCustomAttributes<PgslCommandAttribute>())
            {
                if (attr.Name == pgslName) { _propertyCache[pgslName] = p; return p; }
            }
        }
        _propertyCache[pgslName] = null;
        return null;
    }

    private bool TryInvokeScriptAsset(string name, object[] args, out object result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string scriptName = name.Trim();
        if (!ScriptAssetRegistry.TryGet(scriptName, out _))
        {
            if (!scriptName.StartsWith("scr_", StringComparison.OrdinalIgnoreCase)) return false;
            scriptName = scriptName.Substring(4);
            if (!ScriptAssetRegistry.TryGet(scriptName, out _)) return false;
        }

        var vm = _context?.ActiveVm as PgslVm;
        if (vm == null)
            throw new InvalidOperationException($"Script '{scriptName}' can only run during play.");

        result = ScriptAssetRegistry.ExecuteWithReturn(vm, scriptName, args ?? Array.Empty<object>()) ?? 0;
        return true;
    }
}
