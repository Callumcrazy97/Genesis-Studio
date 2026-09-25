using System.Collections.Generic;

namespace Genesis.Shared.Scripting
{
    /// <summary>VM-facing bridge to native PGSL command implementations.</summary>
    public interface IPgslEngineBridge
    {
        int CommandCount { get; }
        IReadOnlyDictionary<string, int> NativeIdMap { get; }
        void SetContext(PgslContext context);
        PgslContext GetContext();
        IEnumerable<object> FindObjects(string objName);
        object InvokeNative(int id, object[] args);
        bool IsNativeVoid(int id);

        /// <summary>
        /// The command name behind a native dispatch id, or null when the id is unknown.
        /// </summary>
        /// <remarks>
        /// Compiled bytecode carries ids. Any tool reporting on what a script actually did — the
        /// Object Sandbox's warnings, a profiler, a trace — has to be able to turn one back into
        /// the name the designer typed.
        /// </remarks>
        string NativeName(int id);
        bool IsVoid(string name, int argCount);
        /// <summary>Set a declared PGSL property without exception-driven command probing.</summary>
        bool TrySetProperty(string name, object value);
        object Invoke(string name, object[] args);
        void BuildNativeCallTable();
    }
}
