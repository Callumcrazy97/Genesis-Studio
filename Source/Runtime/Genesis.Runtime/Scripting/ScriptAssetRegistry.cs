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
    private static readonly Dictionary<string, CompiledScriptAsset> _byName =
      new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CompiledScriptAsset> _byHash =
      new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The exact source last registered under each name.</summary>
    private static readonly Dictionary<string, string> _sourceByName =
      new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();
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

    public static void LoadFromProject(string projectPath)
    {
      lock (_lock)
      {
        _projectPath = projectPath;
        _byName.Clear();
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
        foreach (string file in EnumerateScriptFiles(projectPath))
        {
          string name = ResourceNames.Name(projectPath, file, ResourceType.Script);
          if (_byName.ContainsKey(name)) throw new InvalidDataException("Duplicate script resource name: " + name);
          _libraryNames.Add(name);
          try
          {
            Register(name, File.ReadAllText(file));
          }
          catch (Exception ex)
          {
            RecordLoadError(name, file, ex);
          }
        }
      }
    }

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
        var previousSources = new Dictionary<string, string>(_sourceByName, StringComparer.OrdinalIgnoreCase);
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
          _byName.Clear();
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
          if (previousSources.TryGetValue(name, out string source)) _sourceByName[name] = source;
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
        if (_byName.ContainsKey(name) && _sourceByName.TryGetValue(name, out string known) && string.Equals(known, source, StringComparison.Ordinal))
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
        if (_sourceByName.TryGetValue(name, out string known)
            && string.Equals(known, source, StringComparison.Ordinal)
            && _byName.ContainsKey(name))
          return;
      }
      var asset = ScriptAssetCompiler.Compile(name, source);
      lock (_lock)
      {
        _sourceByName[name] = source;
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

      string path = ResourceNames.Resolve(_projectPath, name, ResourceType.Script);
      if (!File.Exists(path) || !path.EndsWith(".pgsl", StringComparison.OrdinalIgnoreCase))
        return null;

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

    public static IReadOnlyList<string> GetScriptNames()
    {
      lock (_lock)
        return _byName.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ApplyScriptCallSyntax(string source)
    {
      if (string.IsNullOrEmpty(source)) return source;
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
        _byName.Clear();
        _functions = null;
        _byHash.Clear();
        _sourceByName.Clear();
        _libraryNames.Clear();
        _projectPath = null;
      }
    }
  }
}
