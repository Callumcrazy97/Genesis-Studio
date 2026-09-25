using System;
using System.Collections.Generic;

namespace Genesis.Runtime.Scripting;

/// <summary>What the VM absorbed rather than failed on.</summary>
public enum PgslNoteKind
{
    /// <summary>A name was read before anything gave it a value, so the VM used 0.</summary>
    UnresolvedRead,

    /// <summary>Reading a built-in property threw, and the VM substituted 0.</summary>
    ReadFailed,

    /// <summary>A command ran, for the caller to decide whether it could have meant anything.</summary>
    CommandCalled,
}

/// <summary>One thing the VM did quietly.</summary>
public sealed record PgslRuntimeNote(PgslNoteKind Kind, string Subject, string Detail);

/// <summary>
/// Opt-in record of everything the PGSL VM handles by substituting a default.
/// </summary>
/// <remarks>
/// The VM is deliberately forgiving at run time: reading an unset name yields 0, and a command that
/// needs a world it has not got returns a neutral value rather than killing the frame. That is right
/// for a shipped game — one typo should not end the session — but it is exactly wrong for a *tool*,
/// where the same forgiveness turns "your script did nothing" into "no errors". The Object Sandbox
/// reported a clean run for scripts whose every meaningful line had been absorbed this way.
///
/// So the VM keeps its behaviour and gains a witness. Collection is opt-in and thread-local: with no
/// collector installed <see cref="Note"/> is a null check, so the player pays nothing, and a tool
/// that wants the truth wraps the run in <see cref="Collect"/>.
/// </remarks>
public static class PgslRuntimeDiagnostics
{
    [ThreadStatic]
    private static List<PgslRuntimeNote> _sink;

    /// <summary>True while something is collecting on this thread.</summary>
    public static bool IsCollecting => _sink != null;

    /// <summary>
    /// Collects notes into <paramref name="into"/> until the returned scope is disposed. Scopes
    /// nest: the previous collector is restored, not discarded.
    /// </summary>
    public static IDisposable Collect(List<PgslRuntimeNote> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        Scope scope = new(_sink);
        _sink = into;
        return scope;
    }

    /// <summary>Records a note when someone is listening. A no-op otherwise.</summary>
    public static void Note(PgslNoteKind kind, string subject, string detail)
    {
        _sink?.Add(new PgslRuntimeNote(kind, subject ?? string.Empty, detail ?? string.Empty));
    }

    private sealed class Scope(List<PgslRuntimeNote> previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sink = previous;
        }
    }
}
