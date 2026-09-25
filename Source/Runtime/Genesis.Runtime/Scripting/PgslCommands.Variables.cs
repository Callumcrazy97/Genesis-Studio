using System;
using System.Globalization;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>Typed dynamic-variable actions used by PGSL and the Object Universal Builder.</summary>
public static partial class PgslCommands
{
    [PgslCommand("VariableSet", "VariableSet(name, value)",
        "Set a numeric script variable by name", "Variables")]
    public static void VariableSet(string name, double value) => SetVariableValue(name, value);

    [PgslCommand("VariableGet", "VariableGet(name) -> number",
        "Get a numeric script variable by name", "Variables")]
    public static double VariableGet(string name)
    {
        object value = GetVariableValue(name);
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    [PgslCommand("VariableSetBool", "VariableSetBool(name, value)",
        "Set a boolean script variable by name", "Variables")]
    public static void VariableSetBool(string name, bool value) => SetVariableValue(name, value);

    [PgslCommand("VariableGetBool", "VariableGetBool(name) -> bool",
        "Get a boolean script variable by name", "Variables")]
    public static bool VariableGetBool(string name)
    {
        object value = GetVariableValue(name);
        if (value is bool boolean) return boolean;
        if (value is string text && bool.TryParse(text, out boolean)) return boolean;
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0; }
        catch { return false; }
    }

    [PgslCommand("VariableSetText", "VariableSetText(name, value)",
        "Set a text script variable by name", "Variables")]
    public static void VariableSetText(string name, string value) => SetVariableValue(name, value ?? string.Empty);

    [PgslCommand("VariableGetText", "VariableGetText(name) -> string",
        "Get a text script variable by name", "Variables")]
    public static string VariableGetText(string name) =>
        Convert.ToString(GetVariableValue(name), CultureInfo.InvariantCulture) ?? string.Empty;

    private static void SetVariableValue(string name, object value)
    {
        PgslContext context = GetContext();
        string key = name?.Trim() ?? string.Empty;
        if (context is null || key.Length == 0) return;
        if (context.ActiveVm is PgslVm vm) vm.SetVariable(key, value);
        context.Variables[key] = value;
    }

    private static object GetVariableValue(string name)
    {
        PgslContext context = GetContext();
        string key = name?.Trim() ?? string.Empty;
        if (context is null || key.Length == 0) return null;
        if (context.ActiveVm is PgslVm vm && vm.TryReadVariable(key, out object vmValue)) return vmValue;
        return context.Variables.TryGetValue(key, out object value) ? value : null;
    }
}
