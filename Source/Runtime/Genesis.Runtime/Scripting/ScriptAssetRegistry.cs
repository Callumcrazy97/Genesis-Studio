using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Runtime.Scripting
{
  /// <summary>
  /// Cached PGSL script modules (SHA256 of preprocessed source). Used by scr_* / ScrExecute (W7).
  /// </summary>
  public static class ScriptAssetRegistry
  {
    private static readonly Dictionary<string, (long Generation, long NextCheck)> _missing = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CompiledScriptAsset> _byName =
      new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CompiledScriptAsset> _byHash =
      new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The source last registered under each name.</summary>
    private static readonly Dictionary<string, SourceStamp> _sourceByName =
      new(StringComparer.OrdinalIgnoreCase);

    // A Script longer than this keeps its length and SHA-256, not its text, to tell whether its source
    // changed, and lets go of its prepared text once registered: GenesisCraft's 44 MB of generated
    // data Scripts, kept as text twice, were about 170 MB of its memory.
    private const int KeepTextUpTo = 256 * 1024;

    /// <summary>What a Script's source was: the text itself, or for a large one its length and hash.</summary>
    private readonly struct SourceStamp
    {
      private readonly string _text;
      private readonly int _length;
      private readonly string _hash;

      private SourceStamp(string text, int length, string hash) { _text = text; _length = length; _hash = hash; }

      /// <param name="hash">The source's <see cref="Hash"/> when already known.</param>
      /// <summary>How many characters the source had.</summary>
      public int Length => _length;

      public static SourceStamp Of(string source, string hash = null) => source == null || source.Length <= KeepTextUpTo
        ? new SourceStamp(source, source?.Length ?? 0, null)
        : new SourceStamp(null, source.Length, hash ?? Hash(source));

      public bool Matches(string source) => _hash == null
        ? string.Equals(_text, source, StringComparison.Ordinal)
        : source != null && source.Length == _length && string.Equals(_hash, Hash(source), StringComparison.Ordinal);

      public static string Hash(string source) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(source.AsSpan())));
    }
    private static readonly object _lock = new();

    // The debug screen's Resources tab lists the loaded Scripts: their functions, size and any error.
    static ScriptAssetRegistry() =>
      Genesis.Shared.Diagnostics.DebugResourceCatalog.Register("scripts", _lock, Describe);

    private static List<Genesis.Shared.Diagnostics.DebugResourceRow> Describe()
    {
      lock (_lock)
      {
        var rows = new List<Genesis.Shared.Diagnostics.DebugResourceRow>(_byName.Count + _loadErrors.Count);
        foreach ((string name, CompiledScriptAsset asset) in _byName)
        {
          long bytes = _sourceByName.TryGetValue(name, out SourceStamp source) ? source.Length * 2L : 0;
          rows.Add(new Genesis.Shared.Diagnostics.DebugResourceRow("Script", name, asset.CompileResult?.UserFunctions?.Count ?? 0, bytes,
            $"{asset.CompileResult?.Instructions?.Count ?? 0} instructions"));
        }
        foreach ((string name, string error) in _loadErrors)
          rows.Add(new Genesis.Shared.Diagnostics.DebugResourceRow("Script", name, 0, 0, "did not compile: " + error));
        return rows;
      }
    }
    private static string _projectPath;
    private static int _callDepth;
    private const int MaxCallDepth = 64;
    // Scripts that did not compile, by name: their file, line and message. A call to one of their
    // functions then says why it is unknown instead of only that it is.
    // Every function the loaded Scripts define, built on first use and dropped when one changes.
    private static Dictionary<string, UserFunction> _functions;
    private static readonly Dictionary<string, string> _loadErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Script resources that failed to compile when loaded: name to "file, line N: message".</summary>
    public static IReadOnlyDictionary<string, string> LoadErrors
    {
      get { lock (_lock) return new Dictionary<string, string>(_loadErrors, StringComparer.OrdinalIgnoreCase); }
    }

    private static void RecordLoadError(string name, string file, Exception ex)
    {
      int line = ex.Data["Line"] is int number ? number : 0;
      string where = _projectPath != null && file.StartsWith(_projectPath, StringComparison.OrdinalIgnoreCase)
        ? Path.GetRelativePath(_projectPath, file) : file;
      string message = line > 0 ? $"{where}, line {line}: {ex.Message}" : $"{where}: {ex.Message}";
      lock (_lock) _loadErrors[name] = message;
      Genesis.Rendering.Diagnostics.RenderLog.Line("PGSL script failed to compile: " + message);
      VMLogger.LogError(name, "Script", "load", file, ex.Message, line);
    }

    /// <summary>Loads Scripts/*.pgsl once per project path (safe to call every frame).</summary>
    public static void EnsureProjectLoaded(string projectPath)
    {
      if (string.IsNullOrEmpty(projectPath))
        return;
      lock (_lock)
      {
        if (string.Equals(_projectPath, projectPath, StringComparison.OrdinalIgnoreCase))
          return;
      }
      LoadFromProject(projectPath);
    }

    /// <summary>Set to 0 to have a game read and compile its Scripts when its first PGSL event needs them, not while it starts.</summary>
    public const string BackgroundLoadEnvironmentVariable = "GENESIS_SCRIPT_WARMUP";

    /// <summary>
    /// Starts reading and compiling the project's Scripts on a thread of its own, so the game's
    /// first PGSL event finds them compiled instead of compiling them all itself. That event waits
    /// for this load (it takes the same lock) when it is not done yet. A load that fails is
    /// forgotten, and the game's own first load meets the failure and reports it as before.
    /// </summary>
    /// <remarks>
    /// The caller sets <see cref="PgslCommands.ProjectPath"/> to the same path first: Scripts are
    /// compiled against it (calls written without brackets) exactly as the game would compile them.
    /// </remarks>
    public static void LoadInBackground(string projectPath)
    {
      if (string.IsNullOrEmpty(projectPath) || Environment.GetEnvironmentVariable(BackgroundLoadEnvironmentVariable) == "0") return;
      lock (_lock)
      {
        if (string.Equals(_projectPath, projectPath, StringComparison.OrdinalIgnoreCase)) return;
      }

      var thread = new System.Threading.Thread(() =>
      {
        try
        {
          VMEngine.Initialize();
          // Checked and loaded under one hold of the lock, so the game never loads them a second time.
          lock (_lock)
          {
            if (!string.Equals(_projectPath, projectPath, StringComparison.OrdinalIgnoreCase)) LoadFromProject(projectPath);
          }
        }
        catch (Exception)
        {
          // Met again, and reported, by the game's own load.
          lock (_lock)
          {
            if (string.Equals(_projectPath, projectPath, StringComparison.OrdinalIgnoreCase)) _projectPath = null;
          }
        }
      })
      {
        IsBackground = true,
        Name = "Genesis script compile",
      };
      thread.Start();
    }

    public static void LoadFromProject(string projectPath)
    {
      using Genesis.Shared.Diagnostics.LoadProfile.Span profile = Genesis.Shared.Diagnostics.LoadProfile.Begin("read and compile the project's Scripts");
      lock (_lock)
      {
        _projectPath = projectPath;
        _byName.Clear(); _missing.Clear();
        _functions = null;
        _byHash.Clear();
        _sourceByName.Clear();
        _loadErrors.Clear();
        VMEngine.ClearCompileCache();
        if (string.IsNullOrEmpty(projectPath)) return;
        ResourceNames.Invalidate(projectPath);

        // Index public Script resources only. Object event programs are private implementation,
        // not globally visible modules that can shadow a named Script.
        _libraryNames.Clear();
        var files = new List<string>();
        var names = new List<string>();
        string duplicate = null;
        foreach (string file in EnumerateScriptFiles(projectPath))
        {
          string name = ResourceNames.Name(projectPath, file, ResourceType.Script);
          if (_libraryNames.Contains(name)) { duplicate = name; break; }
          _libraryNames.Add(name);
          files.Add(file);
          names.Add(name);
        }

        // Read and compiled on several threads, the largest first, then registered in turn: one
        // after another, GenesisCraft's 195 Scripts (44 MB, most of it generated data) held its
        // first room's Create events for over four seconds. Each Script still sees the names of the
        // Scripts before it, as when they were compiled in turn (a call written without brackets).
        var sources = new string[files.Count];
        var stamps = new SourceStamp[files.Count];
        var compiled = new CompiledScriptAsset[files.Count];
        var failures = new Exception[files.Count];
        var order = new int[files.Count];
        var sizes = new long[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
          order[i] = i;
          try { sizes[i] = new FileInfo(files[i]).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Array.Sort(sizes, order);
        Array.Reverse(order);
        string[] earlier = names.ToArray();
        // A large Script unchanged since an earlier run is read back compiled (ScriptCompileCache).
        string cacheFolder = files.Count > 0 ? ScriptCompileCache.Folder() : null;
        if (cacheFolder != null && !VMEngine.IsInitialized) VMEngine.Initialize();
        string table = cacheFolder != null ? ScriptCompileCache.TableIdentity(VMEngine.Bridge?.NativeIdMap) : null;
        var toCache = new List<(string Key, CompiledScriptAsset Asset)>();
        Genesis.Shared.Diagnostics.StartupWorkers.For(files.Count, slot =>
        {
          int i = order[slot];
          IEnumerable<string> outer = _compileNames;
          var before = new ArraySegment<string>(earlier, 0, i);
          _compileNames = before;
          try
          {
            string source;
            using (Genesis.Shared.Diagnostics.LoadProfile.Begin("read a Script's file"))
              source = sources[i] = File.ReadAllText(files[i]);
            bool cached = cacheFolder != null && source.Length >= ScriptCompileCache.SmallestCached;
            string hash = null;
            if (cached || source.Length > KeepTextUpTo)
              using (Genesis.Shared.Diagnostics.LoadProfile.Begin("hash a Script's source"))
                hash = SourceStamp.Hash(source);
            stamps[i] = SourceStamp.Of(source, hash);
            string key = cached ? ScriptCompileCache.Key(hash, before, table) : null;
            CompiledScriptAsset asset = null;
            if (cached)
              using (Genesis.Shared.Diagnostics.LoadProfile.Begin("read a compiled Script back"))
                asset = ScriptCompileCache.TryRead(cacheFolder, key, names[i]);
            if (asset != null)
              ScriptAssetCompiler.DecodeLargeFunctions(asset.CompileResult);
            else
            {
              asset = ScriptAssetCompiler.Compile(names[i], source);
              if (cached) lock (toCache) toCache.Add((key, asset));
            }
            compiled[i] = asset;
          }
          catch (Exception ex)
          {
            failures[i] = ex;
          }
          finally
          {
            _compileNames = outer;
          }
        }, name: "Genesis script compile");
        // Written behind the game, not in its start: the next run reads them.
        if (toCache.Count > 0) ScriptCompileCache.WriteInBackground(cacheFolder, toCache);

        // A Script that failed is not among the names the ones after it see: one whose text names
        // it is compiled again, here, against the names registered before it.
        List<string> failed = null;
        for (int i = 0; i < files.Count; i++)
        {
          try
          {
            if (failures[i] != null) throw failures[i];
            if (failed != null && failed.Any(name => sources[i].Contains(name, StringComparison.Ordinal)))
              Register(names[i], sources[i]);
            else
              RegisterCompiled(names[i], stamps[i], compiled[i]);
          }
          catch (Exception ex)
          {
            (failed ??= new List<string>()).Add(names[i]);
            RecordLoadError(names[i], files[i], ex);
          }
        }
        if (duplicate != null) throw new InvalidDataException("Duplicate script resource name: " + duplicate);
      }
    }

    // Set on a thread compiling a Script for LoadFromProject: the names its calls without brackets
    // may name, used instead of the registry's (whose lock the loading thread holds).
    [ThreadStatic] private static IEnumerable<string> _compileNames;

    /// <summary>
    /// Reads the project's Scripts again for a game that is running (live reload), without the
    /// gap <see cref="ClearCache"/> leaves: the Scripts are callable before and after, never
    /// unknown in between. A Script that cannot be read or compiled now, as when a tool is still
    /// writing it, keeps the version that was loaded, so its functions go on resolving; the next
    /// change to it tries again. When the Scripts cannot be listed at all (two with one name while
    /// files are being renamed) everything stays as it was and <paramref name="failure"/> says
    /// why. Returns the names of the Scripts that kept their earlier version.
    /// </summary>
    public static IReadOnlyList<string> Refresh(string projectPath, out string failure)
    {
      failure = null;
      if (string.IsNullOrEmpty(projectPath)) return Array.Empty<string>();
      lock (_lock)
      {
        // The same Scripts by name as before: only those whose text changed are compiled again.
        // Compiling every Script of a large game (two hundred and fifty) took seven seconds, a
        // frozen game for each saved file. With a Script added, removed or renamed, every one
        // is compiled again, since a call written without brackets depends on the names.
        if (TryRefreshChanged(projectPath, out IReadOnlyList<string> keptUnchanged))
          return keptUnchanged;

        var previous = new Dictionary<string, CompiledScriptAsset>(_byName, StringComparer.OrdinalIgnoreCase);
        var previousSources = new Dictionary<string, SourceStamp>(_sourceByName, StringComparer.OrdinalIgnoreCase);
        var previousErrors = new Dictionary<string, string>(_loadErrors, StringComparer.OrdinalIgnoreCase);
        string previousProject = _projectPath;
        try
        {
          LoadFromProject(projectPath);
        }
        catch (Exception ex)
        {
          // Put the tables back as they were: a running game keeps the Scripts it had.
          _projectPath = previousProject ?? projectPath;
          _byName.Clear(); _missing.Clear();
          _byHash.Clear();
          _sourceByName.Clear();
          _loadErrors.Clear();
          foreach (var pair in previous) { _byName[pair.Key] = pair.Value; _byHash[pair.Value.SourceHash] = pair.Value; }
          foreach (var pair in previousSources) _sourceByName[pair.Key] = pair.Value;
          foreach (var pair in previousErrors) _loadErrors[pair.Key] = pair.Value;
          _functions = null;
          VMEngine.ClearCompileCache();
          failure = ex.Message;
          Genesis.Rendering.Diagnostics.RenderLog.Line("PGSL Scripts were not read again (" + ex.Message + "); the game keeps the Scripts it had.");
          return Array.Empty<string>();
        }

        var kept = new List<string>();
        foreach (string name in _loadErrors.Keys)
        {
          if (_byName.ContainsKey(name) || !previous.TryGetValue(name, out CompiledScriptAsset earlier)) continue;
          _byName[name] = earlier;
          _byHash[earlier.SourceHash] = earlier;
          if (previousSources.TryGetValue(name, out SourceStamp source)) _sourceByName[name] = source;
          kept.Add(name);
          Genesis.Rendering.Diagnostics.RenderLog.Line($"PGSL script '{name}' keeps the version loaded before until it compiles.");
        }
        _functions = null;
        return kept;
      }
    }

    /// <summary>The Scripts the last <see cref="LoadFromProject"/> found, by name.</summary>
    private static readonly HashSet<string> _libraryNames = new(StringComparer.OrdinalIgnoreCase);

    // Called under _lock. False when the Scripts are not the ones loaded (another project, or one
    // added, removed or renamed): the caller then reads them all again.
    private static bool TryRefreshChanged(string projectPath, out IReadOnlyList<string> kept)
    {
      kept = Array.Empty<string>();
      if (!string.Equals(_projectPath, projectPath, StringComparison.OrdinalIgnoreCase) || _libraryNames.Count == 0) return false;
      var files = new List<(string Name, string File)>();
      try
      {
        ResourceNames.Invalidate(projectPath);
        foreach (string file in EnumerateScriptFiles(projectPath))
          files.Add((ResourceNames.Name(projectPath, file, ResourceType.Script), file));
      }
      catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
      {
        return false;
      }
      if (files.Count != _libraryNames.Count || !files.All(entry => _libraryNames.Contains(entry.Name))) return false;

      var keptNames = new List<string>();
      foreach ((string name, string file) in files)
      {
        string source;
        try { source = File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
          RecordLoadError(name, file, ex);
          if (_byName.ContainsKey(name)) keptNames.Add(name);
          continue;
        }
        if (_byName.ContainsKey(name) && _sourceByName.TryGetValue(name, out SourceStamp known) && known.Matches(source))
          continue;
        try
        {
          // Replaces the Script only once it has compiled.
          Register(name, source);
          _loadErrors.Remove(name);
        }
        catch (Exception ex)
        {
          RecordLoadError(name, file, ex);
          if (_byName.ContainsKey(name))
          {
            keptNames.Add(name);
            Genesis.Rendering.Diagnostics.RenderLog.Line($"PGSL script '{name}' keeps the version loaded before until it compiles.");
          }
        }
      }
      _functions = null;
      kept = keptNames;
      return true;
    }

    /// <summary>
    /// Every .pgsl in the project, deepest-first-stable (sorted by path) so the same project always
    /// resolves the same way. Build/output folders are skipped so a published copy of a script never
    /// shadows the source of truth.
    /// </summary>
    private static IEnumerable<string> EnumerateScriptFiles(string projectPath)
    {
      return ResourceNames.For(projectPath).Entries
        .Where(e => e.Type == ResourceType.Script && e.FullPath.EndsWith(".pgsl", StringComparison.OrdinalIgnoreCase))
        .Select(e => e.FullPath);
    }

    /// <summary>
    /// True when a .pgsl file sits in an object's event folder, identified by a sibling
    /// <c>&lt;FolderName&gt;.object.json</c> — the layout ObjectEventStore writes.
    /// </summary>
    private static bool IsObjectEventFile(string file)
    {
      string folder = Path.GetDirectoryName(file);
      if (string.IsNullOrEmpty(folder)) return false;

      string parent = Path.GetDirectoryName(folder);
      string name = Path.GetFileName(folder);
      if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) return false;

      return File.Exists(Path.Combine(parent, name + ".object.json"));
    }

    public static void Register(string name, string source)
    {
      // Every spawned instance re-registers its object's event scripts. Identical source used to
      // be preprocessed, SHA-256 hashed and compiled again before the hash comparison could
      // discover nothing had changed.
      lock (_lock)
      {
        if (_sourceByName.TryGetValue(name, out SourceStamp known)
            && known.Matches(source)
            && _byName.ContainsKey(name))
          return;
      }
      RegisterCompiled(name, SourceStamp.Of(source), ScriptAssetCompiler.Compile(name, source));
    }

    private static void RegisterCompiled(string name, SourceStamp source, CompiledScriptAsset asset)
    {
      lock (_lock)
      {
        _sourceByName[name] = source;
        if (asset.ProcessedSource?.Length > KeepTextUpTo) asset.ProcessedSource = null;
        if (_byName.TryGetValue(name, out CompiledScriptAsset previous) && previous.SourceHash != asset.SourceHash)
        {
          VMEngine.ClearCompileCache();
          if (!_byName.Any(pair => !string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)
              && pair.Value.SourceHash == previous.SourceHash)) _byHash.Remove(previous.SourceHash);
        }
        _byName[name] = asset;
        _byHash[asset.SourceHash] = asset;
        _functions = null;
      }
    }

    /// <summary>
    /// A function some project Script defines, so an object can call it without running that Script
    /// first. When two Scripts define it, the first by name wins.
    /// </summary>
    public static bool TryFindFunction(string name, out UserFunction function)
    {
      function = null;
      if (string.IsNullOrWhiteSpace(name)) return false;
      lock (_lock)
        return EnsureFunctions().TryGetValue(name, out function);
    }

    /// <summary>
    /// Every function the loaded Scripts define, as they are now. The table is replaced, never
    /// changed, when a Script changes, so a worker job can keep the one it started with.
    /// </summary>
    public static IReadOnlyDictionary<string, UserFunction> Functions()
    {
      lock (_lock)
        return EnsureFunctions();
    }

    private static Dictionary<string, UserFunction> EnsureFunctions()
    {
      if (_functions == null)
      {
        var functions = new Dictionary<string, UserFunction>(StringComparer.OrdinalIgnoreCase);
        foreach (CompiledScriptAsset asset in _byName.Values.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase))
          if (asset.CompileResult?.UserFunctions != null)
            foreach (var pair in asset.CompileResult.UserFunctions) functions[pair.Key] = pair.Value;
        _functions = functions;
      }
      return _functions;
    }

    public static bool TryGet(string name, out CompiledScriptAsset asset)
    {
      if (string.IsNullOrWhiteSpace(name)) { asset = null; return false; }
      lock (_lock)
      {
        if (_byName.TryGetValue(name, out asset))
          return true;
      }

      asset = TryLoadLazy(name);
      return asset != null;
    }

    private static CompiledScriptAsset TryLoadLazy(string name)
    {
      if (string.IsNullOrEmpty(_projectPath) || string.IsNullOrEmpty(name))
        return null;

      // A name that is no Script is remembered until the asset policy says to look again: calls by
      // such a name searched the catalog and the disk every time (Kinfield: a sixth of its allocation).
      long now = Environment.TickCount64;
      long generation = Genesis.Shared.Assets.RuntimeAssetPolicy.Generation;
      lock (_lock)
        if (_missing.TryGetValue(name, out (long Generation, long NextCheck) miss) && miss.Generation == generation && now < miss.NextCheck)
          return null;
      string path = ResourceNames.Resolve(_projectPath, name, ResourceType.Script);
      if (!File.Exists(path) || !path.EndsWith(".pgsl", StringComparison.OrdinalIgnoreCase))
      {
        lock (_lock)
          _missing[name] = (generation, Genesis.Shared.Assets.RuntimeAssetPolicy.NextCheck(now,
            Math.Max(1000, Genesis.Shared.Assets.RuntimeAssetPolicy.FramePathIntervalMilliseconds), name.GetHashCode()));
        return null;
      }

      try
      {
        Register(name, File.ReadAllText(path));
        lock (_lock)
        {
          return _byName.TryGetValue(name, out var a) ? a : null;
        }
      }
      catch (Exception ex)
      {
        RecordLoadError(name, path, ex);
        return null;
      }
    }

    /// <summary>Whether a Script of this name is loaded now (nothing is loaded by asking).</summary>
    public static bool IsLoaded(string name)
    {
      if (string.IsNullOrWhiteSpace(name)) return false;
      lock (_lock) return _byName.ContainsKey(name.Trim());
    }

    /// <summary>
    /// The error for a call to a name that is neither a command nor a loaded Script, with the reason
    /// when there is one: the Script of that name did not compile (its file, line and message), it
    /// is in the project but not loaded, or some other Script that did not compile may define it.
    /// Always starts "Unknown command: name", which the VM matches on.
    /// </summary>
    public static string DescribeUnknown(string name)
    {
      string key = name?.Trim() ?? string.Empty;
      string unknown = "Unknown command: " + key;
      Dictionary<string, string> failed;
      string projectPath;
      lock (_lock)
      {
        if (key.Length > 0 && _loadErrors.TryGetValue(key, out string own))
          return $"{unknown}. The Script '{key}' did not compile: {own}";
        failed = _loadErrors.Count == 0 ? null : new Dictionary<string, string>(_loadErrors, StringComparer.OrdinalIgnoreCase);
        projectPath = _projectPath;
      }
      string file = ScriptFileFor(key, projectPath ?? PgslCommands.ProjectPath, out string lookupError);
      if (lookupError != null)
        return $"{unknown}. The Script '{key}' cannot be found by name: {lookupError}";
      // The Scripts loaded are another folder's than the game's own project.
      string game = PgslCommands.ProjectPath;
      if (file == null && !string.IsNullOrEmpty(projectPath) && !string.IsNullOrEmpty(game) && !SameFolder(projectPath, game)
          && ScriptFileFor(key, game, out _) is { } elsewhere)
        return $"{unknown}. The Script '{key}' ({elsewhere}) is in the game's project ({game}), but the Scripts loaded are those of {projectPath}.";
      if (file != null)
        return string.IsNullOrEmpty(projectPath)
          ? $"{unknown}. The Script '{key}' ({file}) is in the project, but the project's Scripts were not loaded when it was called."
          : $"{unknown}. The Script '{key}' ({file}) is in the project but could not be loaded.";
      return failed is null
        ? unknown
        : $"{unknown}. A Script that did not compile may define it: {string.Join("; ", failed.Values)}";
    }

    /// <summary>
    /// When a Script has the same name as a function the loaded Scripts define (a call by that name
    /// runs the function, never the Script), a sentence saying so; empty otherwise.
    /// </summary>
    public static string DescribeFunctionScriptClash(string name)
    {
      if (string.IsNullOrWhiteSpace(name)) return string.Empty;
      string key = name.Trim();
      string projectPath;
      lock (_lock)
      {
        if (!_byName.ContainsKey(key)) return string.Empty;
        projectPath = _projectPath;
      }
      string file = ScriptFileFor(key, projectPath, out _);
      return $" '{key}' is both a Script{(file != null ? " (" + file + ")" : string.Empty)} and a function of the project's Scripts:"
        + $" a call {key}(...) runs the function, never the Script's own code. Rename the function or the Script.";
    }

    private static bool SameFolder(string a, string b)
    {
      try
      {
        return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
      }
      catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
      {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
      }
    }

    // The project-relative file of a Script resource, or null when the project has none of that name.
    private static string ScriptFileFor(string name, string projectPath, out string error)
    {
      error = null;
      if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(projectPath)) return null;
      try
      {
        string path = ResourceNames.Resolve(projectPath, name, ResourceType.Script);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        return path.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(projectPath, path) : path;
      }
      catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
      {
        // Two resources share the name (a Script and a Model, say): say so when one is a Script.
        try
        {
          if (ResourceNames.For(projectPath).Entries.Any(entry => entry.Type == ResourceType.Script
              && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
            error = ex.Message;
        }
        catch (Exception) { }
        return null;
      }
    }

    public static IReadOnlyList<string> GetScriptNames()
    {
      lock (_lock)
        return _byName.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ApplyScriptCallSyntax(string source)
    {
      if (string.IsNullOrEmpty(source)) return source;
      if (_compileNames != null) return ScriptCallSyntax.Apply(source, _compileNames);
      lock (_lock)
        return ScriptCallSyntax.Apply(source, _byName.Keys);
    }

    public static void Execute(PgslVm vm, string scriptName, object[] args = null)
    {
      ExecuteWithReturn(vm, scriptName, args);
    }

    public static object ExecuteWithReturn(PgslVm vm, string scriptName, object[] args = null)
    {
      if (vm == null) throw new ArgumentNullException(nameof(vm));
      if (!TryGet(scriptName, out var asset))
        throw new InvalidOperationException($"Script not found: {scriptName}");

      if (_callDepth >= MaxCallDepth)
        throw new InvalidOperationException($"Script call depth exceeded ({MaxCallDepth}): {scriptName}");

      args ??= Array.Empty<object>();
      _callDepth++;
      try
      {
        vm.Bridge.SetContext(PgslCommands.GetContext());
        using (vm.EnterScriptScope(args, asset.CompileResult.UserFunctions))
          vm.Execute(asset.CompileResult.Instructions, asset.CompileResult.Constants, clearVariables: false);
        return null;
      }
      catch (ReturnException ex)
      {
        return ex.ReturnValue;
      }
      finally
      {
        _callDepth--;
      }
    }

    public static void ClearCache()
    {
      lock (_lock)
      {
        _byName.Clear(); _missing.Clear();
        _functions = null;
        _byHash.Clear();
        _sourceByName.Clear();
        _libraryNames.Clear();
        _projectPath = null;
      }
    }
  }
}
