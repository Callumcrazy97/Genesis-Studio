using System;
using System.Collections.Generic;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Reading and writing another instance's variables, and finding instances of an Object, so a
// controller can tell what it spawned what to do. `with (target) { ... }` does the same for a block.
public static partial class PgslCommands
{
    [PgslCommand("InstanceVariableSet", "InstanceVariableSet(id, name, value)",
        "Set a variable of another instance, as its own scripts see it", "Instances")]
    public static void InstanceVariableSet(double id, string name, object value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        PgslBehavior.FindById(id)?.Vm?.SetVariable(name.Trim(), value);
    }

    [PgslCommand("InstanceVariableGet", "InstanceVariableGet(id, name) -> any",
        "A variable of another instance; 0 when it or the variable does not exist", "Instances")]
    public static object InstanceVariableGet(double id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0d;
        PgslVm vm = PgslBehavior.FindById(id)?.Vm;
        return vm != null && vm.TryReadVariable(name.Trim(), out object value) && value != null ? value : 0d;
    }

    [PgslCommand("InstanceVariableExists", "InstanceVariableExists(id, name) -> bool",
        "Whether another instance has set a variable of that name", "Instances")]
    public static bool InstanceVariableExists(double id, string name) =>
        !string.IsNullOrWhiteSpace(name) && PgslBehavior.FindById(id)?.Vm is PgslVm vm && vm.TryReadVariable(name.Trim(), out _);

    [PgslCommand("InstanceNumber", "InstanceNumber(objectName) -> number",
        "How many live instances of an Object there are (\"all\" counts every scripted instance)", "Instances")]
    public static double InstanceNumber(string objectName) => PgslBehavior.FindTargets(objectName).Count;

    [PgslCommand("InstanceFind", "InstanceFind(objectName, n) -> id",
        "The id of the n-th live instance of an Object, counting from 0; 0 when there is none", "Instances")]
    public static double InstanceFind(string objectName, double n)
    {
        List<object> found = PgslBehavior.FindTargets(objectName);
        if (!double.IsFinite(n) || n < 0 || n >= found.Count) return 0;
        return ((PgslBehavior)found[(int)n]).Entity.Id;
    }
}
