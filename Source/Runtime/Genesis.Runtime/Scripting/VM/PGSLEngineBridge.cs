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
    private readonly Dictionary<MethodInfo, ParameterInfo[]> _parameterCache = new();
    private readonly Type _hostType = typeof(PgslCommands);
    private PgslContext _context;

    private Func<object[], object>[] _nativeTable;
    private bool[] _nativeIsVoid;
    private string[] _nativeNames;
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

    public void BuildNativeCallTable()
    {
        var entries = new List<KeyValuePair<string, CommandDef>>(_commandDefs);
        _nativeTable = new Func<object[], object>[entries.Count];
        _nativeIsVoid = new bool[entries.Count];
        _nativeNames = new string[entries.Count];
        _nativeIdMap = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < entries.Count; i++)
        {
            var def = entries[i].Value;
            _nativeIdMap[entries[i].Key] = i;
            _nativeNames[i] = def.Name ?? entries[i].Key;

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

    public IEnumerable<object> FindObjects(string objName) => Array.Empty<object>();

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
            throw new InvalidOperationException($"Unknown command: {name}");
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

        if (!_parameterCache.TryGetValue(method, out ParameterInfo[] parameters))
        {
            parameters = method.GetParameters();
            _parameterCache[method] = parameters;
        }
        if (parameters.Length == 0) return GetOrCreateDelegate(method)(Array.Empty<object>());

        object[] callArgs = ArrayPool<object>.Shared.Rent(parameters.Length);
        try
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsDefined(typeof(ParamArrayAttribute), false))
                {
                    Type elementType = parameters[i].ParameterType.GetElementType() ?? typeof(object);
                    int count = Math.Max(0, args.Length - i);
                    Array tail = Array.CreateInstance(elementType, count);
                    for (int j = 0; j < count; j++) tail.SetValue(ConvertArg(args[i + j], elementType), j);
                    callArgs[i] = tail;
                }
                else if (i < args.Length)
                    callArgs[i] = ConvertArg(args[i], parameters[i].ParameterType);
                else if (parameters[i].HasDefaultValue && parameters[i].DefaultValue != DBNull.Value)
                    callArgs[i] = parameters[i].DefaultValue;
                else
                    callArgs[i] = parameters[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(parameters[i].ParameterType)
                        : null;
            }
            return GetOrCreateDelegate(method)(callArgs);
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
        if (targetType == typeof(double)) return Convert.ToDouble(value);
        if (targetType == typeof(float)) return (float)Convert.ToDouble(value);
        if (targetType == typeof(int)) return Convert.ToInt32(Convert.ToDouble(value));
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
