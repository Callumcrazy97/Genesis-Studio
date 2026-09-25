using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>What happened when the auto-tester invoked one command.</summary>
public enum PgslCommandOutcome
{
    /// <summary>
    /// Invoked and returned without throwing. This is a callability result, not proof that the
    /// command produced the correct value or side effect.
    /// </summary>
    Callable,

    /// <summary>Threw. Every command is expected to tolerate dummy data, so this is a real defect.</summary>
    Threw,

    /// <summary>Declared <c>IsImplemented = false</c> — a known gap, invoked but not counted against us.</summary>
    NotImplemented,

    /// <summary>Could not be invoked because a parameter type is outside the PGSL value set.</summary>
    Unsupported,
}

/// <summary>One command's auto-test result.</summary>
public sealed record PgslCommandTestResult(
    string Name,
    string Category,
    string Signature,
    string Description,
    PgslCommandOutcome Outcome,
    double ElapsedMicroseconds,
    string Detail);

/// <summary>Totals across an auto-test run.</summary>
public sealed record PgslCommandTestReport(
    IReadOnlyList<PgslCommandTestResult> Results,
    double TotalMicroseconds)
{
    public int Total => Results.Count;
    public int Callable => Results.Count(r => r.Outcome == PgslCommandOutcome.Callable);
    public int Failed => Results.Count(r => r.Outcome == PgslCommandOutcome.Threw);
    public int NotImplemented => Results.Count(r => r.Outcome == PgslCommandOutcome.NotImplemented);
    public int Unsupported => Results.Count(r => r.Outcome == PgslCommandOutcome.Unsupported);

    public IEnumerable<PgslCommandTestResult> Slowest(int count) =>
        Results.Where(r => r.Outcome == PgslCommandOutcome.Callable)
            .OrderByDescending(r => r.ElapsedMicroseconds)
            .Take(count);
}

/// <summary>
/// Invokes every <c>[PgslCommand]</c> with dummy arguments and times it.
/// </summary>
/// <remarks>
/// One engine, two front ends: the Help → Commands dialog and the headless
/// <c>Runtime.Pgsl.CommandAutoTest</c> gate both run this, so what the dialog reports and what the
/// build asserts can never disagree. That is the same discipline as `AudioAssetSettings` being the
/// single parser for both editor and runtime (NEXT-041).
///
/// The point of the sweep is not to check that commands compute the *right* answer — the per-family
/// tests do that. It is to prove every command is **callable without a running game**: no unguarded
/// null context, no unguarded renderer, no crash on zero/negative/absurd input. A single throw here
/// is a command that would take down a real game the first time a designer called it early.
///
/// A scratch <see cref="PgslContext"/> is bound for the duration so instance-variable commands have
/// somewhere to write, and <c>ActiveGameContext</c> is left as-is (usually null) precisely so the
/// null paths are the ones exercised.
/// </remarks>
public static class PgslCommandAutoTester
{
    /// <summary>Dimension a command is grouped under for reporting.</summary>
    public static string DimensionOf(string category) => category switch
    {
        "3D Math" or "Drawing 3D" or "Camera Systems — 3D" or "Engine · Models" => "3D",
        "Drawing 2D" or "Camera" or "Sprites" or "Animation" => "2D",
        _ => "1D",
    };

    /// <summary>Every declared command, ordered by category then name.</summary>
    public static IReadOnlyList<PgslCommandInfo> Catalogue()
    {
        List<PgslCommandInfo> commands = [];
        foreach (MemberInfo member in EnumerateCommandMembers())
        {
            foreach (PgslCommandAttribute attribute in member.GetCustomAttributes<PgslCommandAttribute>())
            {
                commands.Add(new PgslCommandInfo
                {
                    Name = attribute.Name,
                    Signature = attribute.Signature,
                    Description = attribute.Description,
                    Category = attribute.Category,
                    Namespace = attribute.Namespace,
                    CSharpMember = member.Name,
                    IsProperty = member is PropertyInfo,
                    IsImplemented = attribute.IsImplemented,
                });
            }
        }

        return [.. commands.OrderBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Invoke every command with dummy data, timing each.
    /// </summary>
    /// <param name="categoryFilter">When set, only run commands in this category.</param>
    /// <param name="repeats">Invocations per command; the reported time is the mean.</param>
    public static PgslCommandTestReport Run(string categoryFilter = null, int repeats = 3)
    {
        int passes = Math.Clamp(repeats, 1, 1000);
        List<PgslCommandTestResult> results = [];

        // A scratch context so instance-variable commands have state to touch. ActiveGameContext is
        // deliberately NOT set: the null-service paths are exactly what needs proving.
        PgslContext scratch = new() { RoomWidth = 1280, RoomHeight = 720 };
        PgslContext previous = PgslCommands.BindContext(scratch);
        Stopwatch clock = new();
        double totalMicroseconds = 0;

        try
        {
            foreach (MemberInfo member in EnumerateCommandMembers())
            {
                foreach (PgslCommandAttribute attribute in member.GetCustomAttributes<PgslCommandAttribute>())
                {
                    if (categoryFilter is not null
                        && !string.Equals(attribute.Category, categoryFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    PgslCommandTestResult result = Invoke(member, attribute, passes, clock);
                    totalMicroseconds += result.ElapsedMicroseconds;
                    results.Add(result);
                }
            }
        }
        finally
        {
            PgslCommands.BindContext(previous);
        }

        return new PgslCommandTestReport(
            [.. results.OrderBy(r => r.Category, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal)],
            totalMicroseconds);
    }

    /// <summary>Every declared command member with its attribute, ordered like <see cref="Catalogue"/>.</summary>
    public static IReadOnlyList<(MemberInfo Member, PgslCommandAttribute Attribute)> Members()
    {
        List<(MemberInfo Member, PgslCommandAttribute Attribute)> members = [];
        foreach (MemberInfo member in EnumerateCommandMembers())
        {
            foreach (PgslCommandAttribute attribute in member.GetCustomAttributes<PgslCommandAttribute>())
                members.Add((member, attribute));
        }

        return [.. members
            .OrderBy(item => item.Attribute.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Attribute.Name, StringComparer.Ordinal)];
    }

    private static IEnumerable<MemberInfo> EnumerateCommandMembers()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        Type type = typeof(PgslCommands);
        foreach (MethodInfo method in type.GetMethods(flags))
        {
            if (method.IsSpecialName) continue;   // property accessors are reached via the properties
            if (method.GetCustomAttributes<PgslCommandAttribute>().Any()) yield return method;
        }

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (property.GetCustomAttributes<PgslCommandAttribute>().Any()) yield return property;
        }
    }

    private static PgslCommandTestResult Invoke(
        MemberInfo member, PgslCommandAttribute attribute, int passes, Stopwatch clock)
    {
        PgslCommandTestResult Result(PgslCommandOutcome outcome, double micros, string detail) =>
            new(attribute.Name, attribute.Category, attribute.Signature, attribute.Description, outcome, micros, detail);

        if (member is PropertyInfo property)
        {
            // Read (and write back, when settable) so both accessors are exercised.
            try
            {
                if (property.CanRead) property.GetValue(null);   // untimed warm-up, as for methods

                clock.Restart();
                for (int pass = 0; pass < passes; pass++)
                {
                    object value = property.CanRead ? property.GetValue(null) : null;
                    if (property.CanWrite && value is not null) property.SetValue(null, value);
                }

                clock.Stop();
                return Result(
                    attribute.IsImplemented ? PgslCommandOutcome.Callable : PgslCommandOutcome.NotImplemented,
                    Micros(clock, passes),
                    "property");
            }
            catch (Exception exception)
            {
                clock.Stop();
                return Result(PgslCommandOutcome.Threw, Micros(clock, passes), Unwrap(exception));
            }
        }

        MethodInfo method = (MethodInfo)member;
        ParameterInfo[] parameters = method.GetParameters();
        object[] arguments = new object[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            if (!TryDummy(parameters[index].ParameterType, index, out object value))
            {
                return Result(
                    PgslCommandOutcome.Unsupported,
                    0,
                    $"parameter '{parameters[index].Name}' is {parameters[index].ParameterType.Name}, "
                    + "which is outside the PGSL value set (double/string/bool)");
            }

            arguments[index] = value;
        }

        try
        {
            // Untimed warm-up. Without it the first invocation carries JIT compilation and any
            // one-off lazy initialisation, which swamps the real cost — PlaySound measured 17ms on
            // its first call and microseconds thereafter. The timing is meant to show what a command
            // costs a running game, not what it costs to compile.
            method.Invoke(null, arguments);

            clock.Restart();
            for (int pass = 0; pass < passes; pass++)
            {
                method.Invoke(null, arguments);
            }

            clock.Stop();
            return Result(
                attribute.IsImplemented ? PgslCommandOutcome.Callable : PgslCommandOutcome.NotImplemented,
                Micros(clock, passes),
                string.Empty);
        }
        catch (Exception exception)
        {
            clock.Stop();
            return Result(PgslCommandOutcome.Threw, Micros(clock, passes), Unwrap(exception));
        }
    }

    private static double Micros(Stopwatch clock, int passes) =>
        clock.Elapsed.TotalMilliseconds * 1000.0 / Math.Max(1, passes);

    /// <summary>Reflection wraps the real fault in a TargetInvocationException; report the inner one.</summary>
    private static string Unwrap(Exception exception)
    {
        Exception real = exception is TargetInvocationException { InnerException: not null } wrapper
            ? wrapper.InnerException
            : exception;
        return $"{real.GetType().Name}: {real.Message}";
    }

    /// <summary>
    /// Dummy argument for a parameter. Values are chosen to be *plausible but awkward*: small
    /// non-zero numbers that vary per position (so a command using two coordinates does not get the
    /// same value twice and accidentally hit a degenerate-input early return), and asset names that
    /// will not resolve — because "asset missing" is the path most likely to be unguarded.
    /// </summary>
    private static bool TryDummy(Type type, int position, out object value)
    {
        if (type == typeof(double)) { value = 1.0 + position; return true; }
        if (type == typeof(bool)) { value = position % 2 == 0; return true; }
        if (type == typeof(string)) { value = position == 0 ? "PgslAutoTest" : "x"; return true; }
        if (type == typeof(int)) { value = 1 + position; return true; }
        if (type == typeof(float)) { value = 1f + position; return true; }

        // A handful of commands predate the double/string/bool convention and take a Color or a
        // loosely-typed object; the VM marshals those at the call site. They still need covering —
        // an unguarded one would crash a real game just the same.
        if (type == typeof(System.Drawing.Color)) { value = System.Drawing.Color.FromArgb(255, 40, 90, 200); return true; }
        if (type == typeof(object[])) { value = new object[] { 1.0, 2.0, 3.0 }; return true; }
        if (type == typeof(object)) { value = 1.0 + position; return true; }

        value = null;
        return false;
    }
}
