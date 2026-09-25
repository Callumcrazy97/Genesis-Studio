#nullable disable
using System;
using System.Security.Cryptography;
using System.Text;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Runtime.Scripting.VM;

public static class VMEngine
{
    private static PgslEngineBridge _bridge;
    private static PgslCompiler _compiler;
    private static bool _initialized;
    private static readonly System.Collections.Generic.Dictionary<string, CompileResult> _compiledCache = new();

    public static void Initialize()
    {
        if (_initialized) return;
        _bridge = new PgslEngineBridge();
        _bridge.LoadFromRegistry();
        _compiler = new PgslCompiler(_bridge.NativeIdMap);
        _initialized = true;
        VMLogger.Log($"VM Engine initialized with {_bridge.CommandCount} commands");
    }

    public static PgslEngineBridge Bridge => _bridge;
    public static PgslCompiler Compiler => _compiler;
    public static bool IsInitialized => _initialized;

    public static void ClearCompileCache() => _compiledCache.Clear();

    public static PgslVm CreateVm(bool debug = false)
    {
        if (!_initialized) Initialize();
        return new PgslVm(_bridge, debug);
    }

    public static CompileResult Compile(string source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        if (!_initialized) Initialize();

        string processed = PgslCommands.PreProcessScript(source, true, PgslCommands.ProjectPath);
        string cacheKey = ScriptingDebugSettings.VmBytecodeCacheSha256
            ? ComputeSha256Hex(processed)
            : processed;

        if (_compiledCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var (instructions, constants, userFunctions) = _compiler.Compile(processed);
        var result = new CompileResult(instructions, constants, userFunctions);
        _compiledCache[cacheKey] = result;
        return result;
    }

    private static string ComputeSha256Hex(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }
}
