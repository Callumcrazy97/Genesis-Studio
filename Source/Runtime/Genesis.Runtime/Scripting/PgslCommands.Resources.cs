using System;
using System.Collections.Generic;
using Genesis.Shared.Scripting;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    [PgslCommand("ResourceExists", "ResourceExists(name) -> bool", "Check a globally unique project resource name; no path or extension", "Resources")]
    public static bool ResourceExists(string name) => !string.IsNullOrWhiteSpace(ProjectPath)
        && ResourceNames.For(ProjectPath).Find(name) is not null;

    [PgslCommand("ResourceName", "ResourceName(name) -> string", "Return the canonical resource name, or an empty string when absent", "Resources")]
    public static string ResourceName(string name) => string.IsNullOrWhiteSpace(ProjectPath) ? string.Empty
        : ResourceNames.For(ProjectPath).Find(name)?.Name ?? string.Empty;

    [PgslCommand("ResourceTypeOf", "ResourceTypeOf(name) -> string", "Return the resource type for a project name, or an empty string when absent", "Resources")]
    public static string ResourceTypeOf(string name) => string.IsNullOrWhiteSpace(ProjectPath) ? string.Empty
        : ResourceNames.For(ProjectPath).Find(name)?.Type.ToString() ?? string.Empty;
    [PgslCommand("ScriptExecute", "ScriptExecute(name, ...) -> value", "Execute a named PGSL Script resource; supports spaces in resource names", "Resources")]
    public static object ScriptExecute(string name, params object[] arguments)
    {
        ScriptAssetRegistry.EnsureProjectLoaded(ProjectPath);
        if (GetContext()?.ActiveVm is not PgslVm vm)
            throw new InvalidOperationException("ScriptExecute requires an active Object event or Script execution context.");
        string canonical = ResourceNames.For(ProjectPath).Find(name, ResourceType.Script)?.Name;
        if (string.IsNullOrEmpty(canonical)) throw new InvalidOperationException($"Unknown Script resource '{name}'.");
        return ScriptAssetRegistry.ExecuteWithReturn(vm, canonical, arguments ?? Array.Empty<object>()) ?? 0;
    }

}
