#nullable disable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Runtime.Scripting.VM;

public static class VMEngine
{
    private static PgslEngineBridge _bridge;
    private static PgslCompiler _compiler;
    private static bool _initialized;
    private sealed record CacheEntry(string Key, CompileResult Result, long Bytes);
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> _compiledCache = new();
    private static readonly LinkedList<CacheEntry> _recency = new();
    private static readonly object CacheGate = new();
    private static long _cacheBytes;
    private static string _cacheProject;
    public const int MaximumCompileCacheEntries = 256;
    public const long MaximumCompileCacheBytes = 8 * 1024 * 1024;
    public static int CompileCacheEntries { get { lock (CacheGate) return _compiledCache.Count; } }
    public static long CompileCacheEstimatedBytes { get { lock (CacheGate) return _cacheBytes; } }

    public static void Initialize()
    {
        lock (CacheGate)
        {
            if (_initialized) return;
            _bridge = new PgslEngineBridge();
            _bridge.LoadFromRegistry();
            _compiler = new PgslCompiler(_bridge.NativeIdMap);
            _initialized = true;
            VMLogger.Log($"VM Engine initialized with {_bridge.CommandCount} commands");
        }
    }

    public static PgslEngineBridge Bridge => _bridge;
    public static PgslCompiler Compiler => _compiler;
    public static bool IsInitialized => _initialized;

    public static void ClearCompileCache()
    {
        lock (CacheGate) { _compiledCache.Clear(); _recency.Clear(); _cacheBytes = 0; }
    }

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

        // Preprocessing may load modules; keep that outside the cache/compiler lock to avoid
        // reversing the registry's lock order. Native jobs never compile or execute PGSL.
        lock (CacheGate)
        {
            if (!string.Equals(_cacheProject, PgslCommands.ProjectPath, StringComparison.OrdinalIgnoreCase))
            { ClearCompileCache(); _cacheProject = PgslCommands.ProjectPath; }
            if (_compiledCache.TryGetValue(cacheKey, out var cached))
            {
                _recency.Remove(cached); _recency.AddFirst(cached);
                return cached.Value.Result;
            }
            var (instructions, constants, userFunctions) = _compiler.Compile(processed);
            var result = new CompileResult(instructions, constants, userFunctions);
            long bytes = 256 + cacheKey.Length * 2L + processed.Length * 2L + Estimate(instructions, constants);
            foreach (UserFunction function in userFunctions.Values) bytes += Estimate(function.Bytecode, function.Constants);
            // Oversized programs remain usable without pinning them in the cache.
            if (bytes <= MaximumCompileCacheBytes)
            {
                while (_compiledCache.Count >= MaximumCompileCacheEntries || _cacheBytes + bytes > MaximumCompileCacheBytes)
                {
                    var oldest = _recency.Last;
                    _cacheBytes -= oldest.Value.Bytes; _compiledCache.Remove(oldest.Value.Key); _recency.RemoveLast();
                }
                var node = _recency.AddFirst(new CacheEntry(cacheKey, result, bytes));
                _compiledCache.Add(cacheKey, node); _cacheBytes += bytes;
            }
            return result;
        }
    }

    private static long Estimate(List<Instruction> instructions, List<object> constants)
    {
        long bytes = instructions.Count * 80L + constants.Count * 32L;
        foreach (Instruction instruction in instructions) if (instruction.Operand is string text) bytes += text.Length * 2L;
        foreach (object constant in constants) if (constant is string text) bytes += text.Length * 2L;
        return bytes;
    }

    private static string ComputeSha256Hex(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }
}
