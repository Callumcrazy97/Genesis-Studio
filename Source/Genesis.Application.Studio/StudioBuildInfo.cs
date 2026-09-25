using System.Reflection;
using System.Runtime.InteropServices;

namespace Genesis.Application.Studio;

/// <summary>Identity read from the loaded Studio assembly, never a neighbouring file's timestamp.</summary>
public static class StudioBuildInfo
{
    private static readonly Assembly StudioAssembly = typeof(StudioBuildInfo).Assembly;
    public static string Revision { get; } = Metadata("Genesis.StudioRevision", "unknown");
    public static string BuildId { get; } = Metadata("Genesis.BuildId", "local");
    public static string Configuration { get; } = Metadata("Genesis.BuildConfiguration", "unspecified");
    public static string ShortLabel => $"H{Revision} / {BuildId}";
    public static string WindowLabel => $"Genesis Studio — H{Revision} — Build {BuildId}";
    public static string DiagnosticText =>
        WindowLabel + Environment.NewLine +
        $"Configuration: {Configuration}" + Environment.NewLine +
        $"Assembly: {StudioAssembly.GetName().Version}" + Environment.NewLine +
        $"Module: {StudioAssembly.ManifestModule.ModuleVersionId:D}" + Environment.NewLine +
        $"Studio assembly: {StudioAssembly.Location}" + Environment.NewLine +
        $"Process: {Environment.ProcessPath}" + Environment.NewLine +
        $"Runtime: {RuntimeInformation.FrameworkDescription} / {RuntimeInformation.ProcessArchitecture}" + Environment.NewLine +
        $"OS: {RuntimeInformation.OSDescription}" + Environment.NewLine +
        (BuildId == "local" ? "Local IDE/dotnet build; no Build.bat run identity was supplied."
            : $"Build report: TestResults/Builds/{BuildId}/BuildSummary.json (in the source workspace).");

    private static string Metadata(string name, string fallback) =>
        StudioAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == name)?.Value is { Length: > 0 } value ? value : fallback;
}
