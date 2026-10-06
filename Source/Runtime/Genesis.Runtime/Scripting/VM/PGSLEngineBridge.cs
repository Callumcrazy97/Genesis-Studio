#nullable disable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Linq.Expressions;

using System.Reflection;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>Reflects <see cref="PgslCommands"/> into native VM dispatch table (no JSON).</summary>
public sealed class PgslEngineBridge : IPgslEngineBridge
{
    private readonly Dictionary<string, CommandDef> _commandDefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Member, int ArgumentCount), MethodInfo> _methodCache = new();
    private readonly Dictionary<string, PropertyInfo> _propertyCache = new();
    private static Dictionary<string, PropertyInfo> _staticContextPropCache;
    private readonly Dictionary<MethodInfo, Func<object[], object>> _delegateCache = new();
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
            if (string.IsNullOrWhiteSpace(info.Namespace))
            {
                _commandDefs[info.Name] = def;
            }
            else
            {
                _commandDefs[info.QualifiedName] = def;
                if (!_commandDefs.ContainsKey(info.Name))
                    _commandDefs[info.Name] = def;
            }

            if (!string.IsNullOrEmpty(info.CSharpMember) && info.CSharpMember.Contains('.'))
            {
                string csharpName = info.CSharpMember.Substring(info.CSharpMember.LastIndexOf('.') + 1);
                if (!_commandDefs.ContainsKey(csharpName))
                    _commandDefs[csharpName] = def;
            }
        }
        BuildNativeCallTable();
    }

    public object InvokeNative(int id, object[] args) => _nativeTable[id](args);
    public bool IsNativeVoid(int id) => id >= 0 && id < _nativeIsVoid?.Length && _nativeIsVoid[id];

    /// <summary>
    /// How a command is called with a given number of arguments, worked out once: the overload,
    /// its parameters, and, when every parameter is a number, a compiled call that takes the
    /// numbers unboxed. The VM calls through this instead of resolving the command on every call.
    /// </summary>
    internal sealed class NativeCall
    {
        public int ArgumentCount;
        /// <summary>The command's own entry (a property, or a method resolved by name per call).</summary>
        public Func<object[], object> Entry;
        public MethodInfo Method;
        public MethodPlan Plan;
        public Func<double[], double> NumberCall;
        public Func<double[], bool> BoolCall;
        public Action<double[]> VoidCall;
        public Func<double[], object> ObjectCall;
        public bool Numeric => NumberCall != null || BoolCall != null || VoidCall != null || ObjectCall != null;
    }

    /// <summary>A method's parameters as argument conversion needs them, read by reflection once.</summary>
    internal sealed class MethodPlan
    {
        public ParameterInfo[] Parameters;
        public bool[] IsParams;
        public Type[] ParamsElementTypes;
        public Func<object[], object> Invoke;
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
        var call = new NativeCall { ArgumentCount = argumentCount, Entry = _nativeTable[id] };
        CommandDef def = _nativeDefs[id];
        if (!def.IsProperty)
        {
            // The same overload CallMethod picks for this many arguments.
            call.Method = ResolveMethod(def.CSharpMember, argumentCount);
            if (call.Method != null)
            {
                call.Plan = GetPlan(call.Method);
                CompileNumericCall(call);
            }
        }
        if ((uint)id < (uint)_nativeCalls.Length) _nativeCalls[id] = call;
        return call;
    }

    // Commands whose every parameter is a number, called with exactly that many: a compiled call
    // over a reusable double[] (no object[] of boxed numbers, no per-argument conversion).
    private static void CompileNumericCall(NativeCall call)
    {
        ParameterInfo[] parameters = call.Plan.Parameters;
        if (parameters.Length != call.ArgumentCount) return;
        for (int i = 0; i < parameters.Length; i++)
            if (parameters[i].ParameterType != typeof(double) || call.Plan.IsParams[i]) return;

        var values = Expression.Parameter(typeof(double[]), "values");
        var arguments = new Expression[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            arguments[i] = Expression.ArrayIndex(values, Expression.Constant(i));
        Expression body = Expression.Call(null, call.Method, arguments);
        Type returns = call.Method.ReturnType;
        if (returns == typeof(double)) call.NumberCall = Expression.Lambda<Func<double[], double>>(body, values).Compile();
        else if (returns == typeof(bool)) call.BoolCall = Expression.Lambda<Func<double[], bool>>(body, values).Compile();
        else if (returns == typeof(void)) call.VoidCall = Expression.Lambda<Action<double[]>>(body, values).Compile();
        else call.ObjectCall = Expression.Lambda<Func<double[], object>>(Expression.Convert(body, typeof(object)), values).Compile();
    }

    /// <summary>Calls a command through its cached plan: the same conversions and errors as <see cref="InvokeNative"/>.</summary>
    internal object InvokeNative(NativeCall call, object[] args) =>
        call.Method == null ? call.Entry(args) : CallMethod(call.Method, call.Plan, args);

    public void BuildNativeCallTable()
    {
        var entries = new List<KeyValuePair<string, CommandDef>>(_commandDefs);
        _nativeTable = new Func<object[], object>[entries.Count];
        _nativeIsVoid = new bool[entries.Count];
        _nativeNames = new string[entries.Count];
        _nativeDefs = new CommandDef[entries.Count];
        _nativeCalls = new NativeCall[entries.Count];
        _nativeIdMap = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
        Generation++;

        for (int i = 0; i < entries.Count; i++)
        {
            var def = entries[i].Value;
            _nativeIdMap[entries[i].Key] = i;
            _nativeNames[i] = def.Name ?? entries[i].Key;
            _nativeDefs[i] = def;

            if (def.IsProperty)
            {
                var captured = def;
                _nativeTable[i] = args =>
                {
                    var prop = GetOrCacheProperty(captured.Name);
                    if (prop == null) return null;
                    if (args.Length == 0) return prop.GetValue(null);
                    prop.SetValue(null, ConvertArg(args[0], prop.PropertyType));
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

    public IEnumerable<object> FindObjects(string objName) => Genesis.Runtime.Scripting.PgslBehavior.FindTargets(objName);

    public bool IsVoid(string name, int argCount)
    {
        if (ScriptAssetRegistry.TryGet(name?.Trim() ?? "", out _))
            return false;

        if (_commandDefs.TryGetValue(name, out var def))
        {
            if (def.IsProperty) return false;
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
        if (TryInvokeScriptAsset(name, args, out var scriptResult))
            return scriptResult;

        if (!_commandDefs.TryGetValue(name, out var def))
        {
            if (PgslNamespaceResolver.TryResolveEngineCommand(name, out _, out _))
                return PgslNamespaceResolver.InvokeEngineCommand(name, args);

            var propRef = GetOrCacheProperty(name);
            if (propRef != null)
            {
                if (args.Length == 0) return propRef.GetValue(null);
                propRef.SetValue(null, ConvertArg(args[0], propRef.PropertyType));
                return null;
            }

            var methodRef = GetOrCacheMethod("PgslCommands." + name, args?.Length ?? 0);
            if (methodRef != null)
                return CallMethod("PgslCommands." + name, args);

            if (_context != null)
            {
                var prop = ResolveContextProperty(name);
                if (prop != null)
                {
                    if (args.Length == 0) return prop.GetValue(_context);
                    if (args.Length == 1 && prop.CanWrite)
                    {
                        prop.SetValue(_context, ConvertArg(args[0], prop.PropertyType));
                        return null;
                    }
                }
            }

            VMLogger.LogWarn1D($"Unknown PGSL command: {name}");
            // A function of a library Script that did not compile is unknown for that reason.
            IReadOnlyDictionary<string, string> failed = Genesis.Runtime.Scripting.ScriptAssetRegistry.LoadErrors;
            throw new InvalidOperationException(failed.Count == 0
                ? $"Unknown command: {name}"
                : $"Unknown command: {name}. A Script that did not compile may define it: {string.Join("; ", failed.Values)}");
        }

        if (def.IsProperty)
        {
            if (args.Length == 0) return GetProperty(def.Name);
            SetProperty(def.Name, args[0]);
            return null;
        }

        return CallMethod(def.CSharpMember, args);
    }

    public bool TrySetProperty(string name, object value)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !_commandDefs.TryGetValue(name, out CommandDef def)
            || !def.IsProperty)
        {
            return false;
        }
        SetProperty(def.Name, value);
        return true;
    }

    internal bool TrySetTypedProperty(string name, VmValue value)
    {
        if (string.IsNullOrWhiteSpace(name) || !_commandDefs.TryGetValue(name, out CommandDef def) || !def.IsProperty)
            return false;
        SetProperty(def.Name, value.ToObject()); return true;
    }

    private sealed class CommandDef
    {
        public string Name;
        public string CSharpMember;
        public string Category;
        public string Description;
        public bool IsProperty;
        public bool IsImplemented;
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

    private object GetProperty(string pgslName)
    {
        var prop = GetOrCacheProperty(pgslName);
        if (prop == null) throw new InvalidOperationException($"Property not found: {pgslName}");
        return prop.GetValue(null);
    }

    private void SetProperty(string pgslName, object value)
    {
        var prop = GetOrCacheProperty(pgslName);
        if (prop == null) throw new InvalidOperationException($"Property not found: {pgslName}");
        prop.SetValue(null, ConvertArg(value, prop.PropertyType));
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
            Invoke = GetOrCreateDelegate(method),
        };
        for (int i = 0; i < parameters.Length; i++)
        {
            plan.IsParams[i] = parameters[i].IsDefined(typeof(ParamArrayAttribute), false);
            if (plan.IsParams[i]) plan.ParamsElementTypes[i] = parameters[i].ParameterType.GetElementType() ?? typeof(object);
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
                    Array tail = Array.CreateInstance(elementType, count);
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
            Array.Clear(callArgs, 0, parameters.Length);
            ArrayPool<object>.Shared.Return(callArgs);
        }
    }

    private Func<object[], object> GetOrCreateDelegate(MethodInfo method)
    {
        if (_delegateCache.TryGetValue(method, out var del)) return del;
        del = CompileMethod(method);
        _delegateCache[method] = del;
        return del;
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
