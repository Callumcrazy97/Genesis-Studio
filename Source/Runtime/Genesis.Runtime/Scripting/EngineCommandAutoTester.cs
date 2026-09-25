using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Genesis.Shared.Commands;

namespace Genesis.Runtime.Scripting;

/// <summary>What happened when the auto-tester invoked one Engine.* command.</summary>
public enum EngineCommandOutcome
{
    /// <summary>Returned without throwing; correctness/effect is not asserted by this sweep.</summary>
    Callable,
    Threw,
    NotImplemented,
    Unsupported,
}

public sealed record EngineCommandTestResult(
    string Name,
    string Category,
    string Signature,
    string Description,
    EngineCommandOutcome Outcome,
    double ElapsedMicroseconds,
    string Detail,
    string SampleData);

public sealed record EngineCommandTestReport(
    IReadOnlyList<EngineCommandTestResult> Results,
    double TotalMicroseconds)
{
    public int Total => Results.Count;
    public int Callable => Results.Count(r => r.Outcome == EngineCommandOutcome.Callable);
    public int Failed => Results.Count(r => r.Outcome == EngineCommandOutcome.Threw);
    public int NotImplemented => Results.Count(r => r.Outcome == EngineCommandOutcome.NotImplemented);
    public int Unsupported => Results.Count(r => r.Outcome == EngineCommandOutcome.Unsupported);
}

/// <summary>
/// Invokes every <c>[EngineCommand]</c> with dummy arguments and captures return values as
/// example data. Unimplemented commands that throw <see cref="NotImplementedException"/> are
/// reported as gaps, not crashes.
/// </summary>
public static class EngineCommandAutoTester
{
    public static IReadOnlyList<EngineCommandInfo> Catalogue()
    {
        EngineCommandRegistry.Build();
        return EngineCommandRegistry.GetCatalog()
            .OrderBy(command => command.Category, StringComparer.Ordinal)
            .ThenBy(command => command.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static EngineCommandTestReport Run(int repeats = 3)
    {
        int passes = Math.Clamp(repeats, 1, 1000);
        var results = new List<EngineCommandTestResult>();
        Stopwatch clock = new();
        double total = 0;

        foreach (MethodInfo method in EnumerateCommands())
        {
            EngineCommandAttribute attribute = method.GetCustomAttribute<EngineCommandAttribute>();
            if (attribute == null) continue;
            EngineCommandTestResult result = Invoke(method, attribute, passes, clock);
            total += result.ElapsedMicroseconds;
            results.Add(result);
        }

        return new EngineCommandTestReport(
            results
                .OrderBy(r => r.Category, StringComparer.Ordinal)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToArray(),
            total);
    }

    public static IReadOnlyList<(MethodInfo Method, EngineCommandAttribute Attribute)> Members()
    {
        var members = new List<(MethodInfo, EngineCommandAttribute)>();
        foreach (MethodInfo method in EnumerateCommands())
        {
            EngineCommandAttribute attribute = method.GetCustomAttribute<EngineCommandAttribute>();
            if (attribute != null)
                members.Add((method, attribute));
        }

        return members
            .OrderBy(item => item.Item2.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Item2.Signature, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<MethodInfo> EnumerateCommands()
    {
        foreach (MethodInfo method in typeof(Engine).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<EngineCommandAttribute>() != null)
                yield return method;
        }
    }

    private static EngineCommandTestResult Invoke(
        MethodInfo method, EngineCommandAttribute attribute, int passes, Stopwatch clock)
    {
        string name = ExtractName(attribute.Signature);
        EngineCommandTestResult Result(EngineCommandOutcome outcome, double micros, string detail, string sample) =>
            new(name, attribute.Category, attribute.Signature, attribute.Description, outcome, micros, detail, sample);

        ParameterInfo[] parameters = method.GetParameters();
        object[] arguments = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            if (!TryDummy(parameters[i].ParameterType, i, out object value))
            {
                return Result(
                    EngineCommandOutcome.Unsupported,
                    0,
                    $"parameter '{parameters[i].Name}' is {parameters[i].ParameterType.Name}",
                    "");
            }

            arguments[i] = value;
        }

        try
        {
            object sample = method.Invoke(null, arguments);
            clock.Restart();
            for (int pass = 0; pass < passes; pass++)
                sample = method.Invoke(null, arguments);
            clock.Stop();

            if (!attribute.IsImplemented)
            {
                return Result(
                    EngineCommandOutcome.NotImplemented,
                    Micros(clock, passes),
                    "declared unimplemented",
                    FormatSample(sample));
            }

            return Result(
                EngineCommandOutcome.Callable,
                Micros(clock, passes),
                string.Empty,
                FormatSample(sample));
        }
        catch (Exception exception)
        {
            clock.Stop();
            Exception real = Unwrap(exception);
            if (!attribute.IsImplemented || real is NotImplementedException)
            {
                return Result(
                    EngineCommandOutcome.NotImplemented,
                    Micros(clock, passes),
                    real.Message,
                    "");
            }

            return Result(
                EngineCommandOutcome.Threw,
                Micros(clock, passes),
                $"{real.GetType().Name}: {real.Message}",
                "");
        }
    }

    private static string ExtractName(string signature)
    {
        int dot = signature.IndexOf('.');
        int paren = signature.IndexOf('(');
        if (dot >= 0 && paren > dot)
            return signature.Substring(dot + 1, paren - dot - 1).Trim();
        return signature;
    }

    private static double Micros(Stopwatch clock, int passes) =>
        clock.Elapsed.TotalMilliseconds * 1000.0 / Math.Max(1, passes);

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } wrapper
            ? wrapper.InnerException
            : exception;

    private static string FormatSample(object value)
    {
        if (value == null) return "void";
        if (value is float[] floats)
            return "[" + string.Join(", ", floats.Select(f => f.ToString("0.###", CultureInfo.InvariantCulture))) + "]";
        if (value is Array array)
            return $"{array.GetType().Name} len {array.Length}";
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name;
    }

    private static bool TryDummy(Type type, int position, out object value)
    {
        if (type == typeof(double)) { value = 1.0 + position; return true; }
        if (type == typeof(bool)) { value = position % 2 == 0; return true; }
        if (type == typeof(string)) { value = position == 0 ? "EngineAutoTest" : "x"; return true; }
        if (type == typeof(int)) { value = 1 + position; return true; }
        if (type == typeof(float)) { value = 1f + position; return true; }
        if (type == typeof(byte[])) { value = Array.Empty<byte>(); return true; }
        if (type == typeof(float[])) { value = new[] { 1f, 2f, 3f, 4f }; return true; }
        if (type == typeof(object)) { value = 1.0 + position; return true; }
        value = null;
        return false;
    }
}
