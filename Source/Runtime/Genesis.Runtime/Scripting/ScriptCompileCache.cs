#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Large Scripts compiled on an earlier run, kept on disk so a game whose Scripts have not changed
/// starts without preparing and compiling them again (GenesisCraft's generated data Scripts: 44 MB,
/// most of its compile at every start). Only Scripts of <see cref="SmallestCached"/> characters or
/// more are kept: a small one compiles faster than it is read back.
/// </summary>
/// <remarks>
/// An entry is found by a key made of everything a compile depends on: the source, the names of the
/// Scripts before it (calls written without brackets), the engine's command table and the engine's
/// own build. Entries live in <c>%LOCALAPPDATA%\GenesisRuntime\ScriptCache\&lt;engine build&gt;</c>
/// (<c>GENESIS_SCRIPT_CACHE</c>: another folder, or 0 to turn it off); the folders of all but the
/// three newest engine builds are deleted. The cache is only an optimisation: an entry that cannot be
/// read, written or understood is compiled as before.
/// </remarks>
internal static class ScriptCompileCache
{
    public const string EnvironmentVariable = "GENESIS_SCRIPT_CACHE";
    public const int SmallestCached = 64 * 1024;
    private const string Version = "genesis-script-cache-v1";
    private const int Magic = 0x43534750; // "PGSC"
    private const int KeptEngineBuilds = 3;

    /// <summary>Tests point the cache at a folder of their own; null means the usual one.</summary>
    internal static string RootOverride;

    private static int _hits, _misses, _writes, _pruned;
    public static int Hits => Volatile.Read(ref _hits);
    public static int Misses => Volatile.Read(ref _misses);
    public static int Writes => Volatile.Read(ref _writes);

    private static readonly string EngineBuild = typeof(PgslParser).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    /// <summary>The engine build's name, the subfolder its entries live in.</summary>
    public static string EngineBuildName => EngineBuild;

    /// <summary>
    /// The folder an exported game ships its entries in, beside its executable: the export writes the
    /// project's large Scripts compiled, so the first launch reads them back instead of compiling
    /// (GC4: a cold first launch took about 30 s against 10 s after). Read here, never written: the
    /// export may sit in a read-only folder, and what a launch compiles goes to <see cref="Folder"/>.
    /// </summary>
    public const string ShippedFolderName = "ScriptCache";

    private static string _shipped;
    private static bool _shippedLooked;

    /// <summary>The shipped entries' folder for this engine build, or null when there is none.</summary>
    public static string ShippedFolder()
    {
        if (_shippedLooked) return _shipped;
        string candidate = null;
        try
        {
            string folder = Path.Combine(AppContext.BaseDirectory, ShippedFolderName, EngineBuild);
            if (Directory.Exists(folder)) candidate = folder;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        _shipped = candidate;
        _shippedLooked = true;
        return candidate;
    }

    /// <summary>This engine build's folder, or null when the cache is off.</summary>
    public static string Folder()
    {
        string root = RootOverride;
        if (string.IsNullOrWhiteSpace(root))
        {
            string environment = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (environment == "0") return null;
            if (!string.IsNullOrWhiteSpace(environment)) root = environment;
            else
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local)) return null;
                root = Path.Combine(local, "GenesisRuntime", "ScriptCache");
            }
        }
        return Path.Combine(root, EngineBuild);
    }

    /// <summary>What the command table makes of a call by name: the native ids are in the bytecode.</summary>
    public static string TableIdentity(IReadOnlyDictionary<string, int> nativeIds)
    {
        if (nativeIds == null) return "no-table";
        var text = new StringBuilder(nativeIds.Count * 24);
        foreach (KeyValuePair<string, int> pair in nativeIds.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
            text.Append(pair.Value).Append('=').Append(pair.Key).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>The key of a Script's compile: its source and what it is compiled against.</summary>
    public static string Key(string sourceHash, IEnumerable<string> namesBefore, string tableIdentity)
    {
        var material = new StringBuilder(4096);
        material.Append(Version).Append('\n').Append(EngineBuild).Append('\n').Append(tableIdentity).Append('\n').Append(sourceHash).Append('\n');
        foreach (string name in namesBefore) material.Append(name).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
    }

    public static CompiledScriptAsset TryRead(string folder, string key, string name)
    {
        if (folder == null) return null;
        string path = Path.Combine(folder, key + ".pgslc");
        try
        {
            if (!File.Exists(path))
            {
                // Not compiled on this machine yet: an exported game ships its entries beside itself.
                string shipped = ShippedFolder();
                if (shipped == null || !File.Exists(path = Path.Combine(shipped, key + ".pgslc"))) { Interlocked.Increment(ref _misses); return null; }
            }
            using var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), Encoding.UTF8);
            CompiledScriptAsset asset = Read(reader, name);
            Interlocked.Increment(ref _hits);
            return asset;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or EndOfStreamException or ArgumentException or FormatException or OverflowException or IndexOutOfRangeException)
        {
            // Compiled again below, and written again: an entry that cannot be read is replaced.
            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    /// <summary>Writes the entries on a thread of its own at below-normal priority; a game that ends first writes fewer.</summary>
    public static void WriteInBackground(string folder, List<(string Key, CompiledScriptAsset Asset)> entries)
    {
        if (folder == null || entries.Count == 0) return;
        var thread = new Thread(() =>
        {
            foreach ((string key, CompiledScriptAsset asset) in entries) TryWrite(folder, key, asset);
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Genesis script cache",
        };
        thread.Start();
        LastBackgroundWrite = thread;
    }

    /// <summary>The last writing thread started, for tests that read an entry back.</summary>
    internal static Thread LastBackgroundWrite;

    /// <summary>Writes the entries now, into <paramref name="folder"/> (an export's shipped cache); how many were written.</summary>
    public static int WriteAll(string folder, List<(string Key, CompiledScriptAsset Asset)> entries)
    {
        if (folder == null || entries == null || entries.Count == 0) return 0;
        int before = Writes;
        foreach ((string key, CompiledScriptAsset asset) in entries) TryWrite(folder, key, asset);
        return Writes - before;
    }

    public static void TryWrite(string folder, string key, CompiledScriptAsset asset)
    {
        if (folder == null || asset?.CompileResult == null) return;
        string path = Path.Combine(folder, key + ".pgslc");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(folder);
            if (Interlocked.Exchange(ref _pruned, 1) == 0) Prune(Path.GetDirectoryName(folder), folder);
            using (var writer = new BinaryWriter(new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16), Encoding.UTF8))
                Write(writer, asset);
            File.Move(temp, path, true);
            Interlocked.Increment(ref _writes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A Script holding a value the format does not know, a read-only disk, or another
            // process writing the same entry: the game goes on, the next start compiles it again.
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    // Keeps the newest few engine builds' folders (an exported game and Studio may run different builds),
    // and lets go of entries a game ended while writing.
    private static void Prune(string root, string current)
    {
        try
        {
            foreach (FileInfo partial in new DirectoryInfo(current).GetFiles("*.tmp"))
                if (partial.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)) partial.Delete();
            DirectoryInfo[] builds = new DirectoryInfo(root).GetDirectories();
            foreach (DirectoryInfo old in builds
                .Where(build => !string.Equals(build.FullName.TrimEnd('\\'), current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(build => build.LastWriteTimeUtc)
                .Skip(KeptEngineBuilds - 1))
                old.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    // ── The format ───────────────────────────────────────────────────────────
    // Names and text are written once, in a table, and referred to by index; operands and
    // constants are tagged. Whatever the compiler makes beyond these kinds is not cached.

    private enum Tag : byte { Null, Integer, Text, NativeCall, Call, Number }

    private static void Write(BinaryWriter writer, CompiledScriptAsset asset)
    {
        var strings = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        int Text(string value)
        {
            if (value == null) throw new NotSupportedException("A null name.");
            if (!strings.TryGetValue(value, out int index)) { strings.Add(value, index = order.Count); order.Add(value); }
            return index;
        }

        // The body first, into memory, so the string table can be written before it.
        using var body = new MemoryStream();
        using (var bodyWriter = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            CompileResult result = asset.CompileResult;
            WriteProgram(bodyWriter, result.Instructions, result.Constants, Text);
            bodyWriter.Write7BitEncodedInt(result.UserFunctions?.Count ?? 0);
            if (result.UserFunctions != null)
                foreach (KeyValuePair<string, UserFunction> pair in result.UserFunctions)
                {
                    UserFunction function = pair.Value;
                    bodyWriter.Write7BitEncodedInt(Text(pair.Key));
                    bodyWriter.Write7BitEncodedInt(Text(function.Name));
                    bodyWriter.Write7BitEncodedInt(function.Parameters?.Count ?? 0);
                    if (function.Parameters != null) foreach (string parameter in function.Parameters) bodyWriter.Write7BitEncodedInt(Text(parameter));
                    WriteProgram(bodyWriter, function.Bytecode, function.Constants, Text);
                }
        }

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(asset.SourceHash ?? "");
        writer.Write7BitEncodedInt(order.Count);
        foreach (string value in order) writer.Write(value);
        writer.Write(body.GetBuffer(), 0, (int)body.Length);
    }

    private static void WriteProgram(BinaryWriter writer, List<Instruction> instructions, List<object> constants, Func<string, int> text)
    {
        writer.Write7BitEncodedInt(instructions.Count);
        foreach (Instruction instruction in instructions)
        {
            writer.Write7BitEncodedInt((int)instruction.Opcode);
            writer.Write7BitEncodedInt(instruction.LineNumber);
            switch (instruction.Operand)
            {
                case null: writer.Write((byte)Tag.Null); break;
                case int value: writer.Write((byte)Tag.Integer); writer.Write(value); break;
                case string value: writer.Write((byte)Tag.Text); writer.Write7BitEncodedInt(text(value)); break;
                case ValueTuple<int, int> call: writer.Write((byte)Tag.NativeCall); writer.Write(call.Item1); writer.Write(call.Item2); break;
                case ValueTuple<string, int> call: writer.Write((byte)Tag.Call); writer.Write7BitEncodedInt(text(call.Item1)); writer.Write(call.Item2); break;
                default: throw new NotSupportedException("An operand of a kind the cache does not keep: " + instruction.Operand.GetType().Name);
            }
        }
        writer.Write7BitEncodedInt(constants?.Count ?? 0);
        if (constants == null) return;
        foreach (object constant in constants)
            switch (constant)
            {
                case double value: writer.Write((byte)Tag.Number); writer.Write(value); break;
                case int value: writer.Write((byte)Tag.Integer); writer.Write(value); break;
                case string value: writer.Write((byte)Tag.Text); writer.Write7BitEncodedInt(text(value)); break;
                default: throw new NotSupportedException("A constant of a kind the cache does not keep: " + (constant?.GetType().Name ?? "null"));
            }
    }

    private static CompiledScriptAsset Read(BinaryReader reader, string name)
    {
        if (reader.ReadInt32() != Magic || reader.ReadString() != Version) throw new InvalidDataException("Not a script cache entry.");
        string sourceHash = reader.ReadString();
        var strings = new string[reader.ReadInt32Checked()];
        for (int i = 0; i < strings.Length; i++) strings[i] = reader.ReadString();
        var boxes = new Boxes();
        (List<Instruction> instructions, List<object> constants) = ReadProgram(reader, strings, boxes);
        int functionCount = reader.ReadInt32Checked();
        var functions = new Dictionary<string, UserFunction>(functionCount);
        for (int f = 0; f < functionCount; f++)
        {
            string key = strings[reader.Read7BitEncodedInt()];
            string functionName = strings[reader.Read7BitEncodedInt()];
            var parameters = new List<string>(reader.ReadInt32Checked());
            for (int p = parameters.Capacity; p > 0; p--) parameters.Add(strings[reader.Read7BitEncodedInt()]);
            (List<Instruction> bytecode, List<object> functionConstants) = ReadProgram(reader, strings, boxes);
            functions.Add(key, new UserFunction(functionName, parameters) { Bytecode = bytecode, Constants = functionConstants });
        }
        return new CompiledScriptAsset
        {
            Name = name,
            SourceHash = sourceHash,
            CompileResult = new CompileResult(instructions, constants, functions),
        };
    }

    // The boxes one entry's operands and numbers share, as the parser shares them.
    private sealed class Boxes
    {
        public readonly Dictionary<(int, int), object> NativeCalls = new();
        public readonly Dictionary<(int, int), object> Calls = new();
        public readonly Dictionary<long, object> Numbers = new();
    }

    private static int ReadInt32Checked(this BinaryReader reader)
    {
        int value = reader.Read7BitEncodedInt();
        if (value < 0 || value > 1 << 26) throw new InvalidDataException("A count out of range.");
        return value;
    }

    private static (List<Instruction>, List<object>) ReadProgram(BinaryReader reader, string[] strings, Boxes boxes)
    {
        int count = reader.ReadInt32Checked();
        var instructions = new List<Instruction>(count);
        for (int i = 0; i < count; i++)
        {
            var opcode = (Opcode)reader.Read7BitEncodedInt();
            int line = reader.Read7BitEncodedInt();
            object operand;
            switch ((Tag)reader.ReadByte())
            {
                case Tag.Null: operand = null; break;
                case Tag.Integer: operand = BoxedNumbers.Of(reader.ReadInt32()); break;
                case Tag.Text: operand = strings[reader.Read7BitEncodedInt()]; break;
                case Tag.NativeCall:
                {
                    int id = reader.ReadInt32(), arguments = reader.ReadInt32();
                    if (!boxes.NativeCalls.TryGetValue((id, arguments), out operand)) boxes.NativeCalls.Add((id, arguments), operand = (id, arguments));
                    break;
                }
                case Tag.Call:
                {
                    int index = reader.Read7BitEncodedInt(), arguments = reader.ReadInt32();
                    if (!boxes.Calls.TryGetValue((index, arguments), out operand)) boxes.Calls.Add((index, arguments), operand = (strings[index], arguments));
                    break;
                }
                default: throw new InvalidDataException("An operand of an unknown kind.");
            }
            instructions.Add(new Instruction(opcode, operand, line));
        }
        int constantCount = reader.ReadInt32Checked();
        var constants = new List<object>(constantCount);
        for (int i = 0; i < constantCount; i++)
            switch ((Tag)reader.ReadByte())
            {
                case Tag.Number:
                {
                    double value = reader.ReadDouble();
                    long bits = BitConverter.DoubleToInt64Bits(value);
                    if (!boxes.Numbers.TryGetValue(bits, out object box)) boxes.Numbers.Add(bits, box = value);
                    constants.Add(box);
                    break;
                }
                case Tag.Integer: constants.Add(reader.ReadInt32()); break;
                case Tag.Text: constants.Add(strings[reader.Read7BitEncodedInt()]); break;
                default: throw new InvalidDataException("A constant of an unknown kind.");
            }
        return (instructions, constants);
    }
}
