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
        foreach (string file in EnumerateScriptFiles(projectPath))
        {
          string name = ResourceNames.Name(projectPath, file, ResourceType.Script);
          if (_byName.ContainsKey(name)) throw new InvalidDataException("Duplicate script resource name: " + name);
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
      {
        if (_functions == null)
        {
          _functions = new Dictionary<string, UserFunction>(StringComparer.OrdinalIgnoreCase);
          foreach (CompiledScriptAsset asset in _byName.Values.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase))
            if (asset.CompileResult?.UserFunctions != null)
              foreach (var pair in asset.CompileResult.UserFunctions) _functions[pair.Key] = pair.Value;
        }
        return _functions.TryGetValue(name, out function);
      }
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
        _projectPath = null;
      }
    }
  }
}
