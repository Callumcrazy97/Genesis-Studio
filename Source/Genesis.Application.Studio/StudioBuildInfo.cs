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
    /// <summary>The version the installer and Add/Remove Programs show, from Installer/AppVersion.txt.</summary>
    public static string ProductVersion { get; } = Metadata("Genesis.ProductVersion", "0.0.0");
    public static string ShortLabel => $"H{Revision} / {BuildId}";
    public static string WindowLabel => $"Genesis Studio — H{Revision} — Build {BuildId}";

    /// <summary>What Help › About shows: the version first, then where the licences are.</summary>
    public static string AboutText =>
        $"Genesis Studio {ProductVersion}" + Environment.NewLine + Environment.NewLine +
        "Free to use; the games you make are yours. The licence:" + Environment.NewLine +
        Path.Combine(AppContext.BaseDirectory, "Licenses", "Genesis-LICENSE.txt") + Environment.NewLine + Environment.NewLine +
        "The software Genesis Studio is built with, and its licences:" + Environment.NewLine +
        Path.Combine(AppContext.BaseDirectory, "Licenses", "ThirdPartyNotices.txt") + Environment.NewLine + Environment.NewLine +
        DiagnosticText;

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
