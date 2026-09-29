using System.Text.Json;

namespace Genesis.Application.Headless;

internal sealed record JudgeBuildGate(string State, string? Evidence, IReadOnlyList<string> Findings);

internal static partial class ReadinessJudgeRunner
{
    private static readonly string[] RequiredBackends = ["dx11", "dx12", "vulkan", "opengl", "software"];

    internal static JudgeBuildGate InspectFullBuild(string root, string fingerprint)
    {
        List<(DateTime Completed, string Summary, string Results)> candidates = [];
        foreach (string summary in Directory.EnumerateFiles(root, "BuildSummary.json", SearchOption.AllDirectories))
        {
            string results = Path.Combine(Path.GetDirectoryName(summary)!, "Tests", "results.json");
            if (!File.Exists(results)) continue;
            using JsonDocument build = JsonDocument.Parse(File.ReadAllText(summary));
            using JsonDocument regression = JsonDocument.Parse(File.ReadAllText(results));
            if (Text(build.RootElement, "Profile") != "Full"
                || Text(regression.RootElement, "ProductFingerprint") != fingerprint) continue;
            DateTime completed = regression.RootElement.TryGetProperty("CompletedUtc", out JsonElement date)
                && date.TryGetDateTime(out DateTime value) ? value : DateTime.MinValue;
            candidates.Add((completed, summary, results));
        }
        if (candidates.Count == 0)
            return new("Unverified", null, ["A matching Full Build report with its Tests/results.json is required."]);

        // A newer failed Full run cannot be erased by selecting an older success or a focused rerun.
        var latest = candidates.OrderBy(item => item.Completed).ThenBy(item => item.Summary, StringComparer.Ordinal).Last();
        using JsonDocument summaryJson = JsonDocument.Parse(File.ReadAllText(latest.Summary));
        using JsonDocument resultsJson = JsonDocument.Parse(File.ReadAllText(latest.Results));
        return AssessFullBuild(summaryJson.RootElement, resultsJson.RootElement, fingerprint,
            Path.GetRelativePath(root, latest.Summary));
    }

    internal static JudgeBuildGate AssessFullBuild(JsonElement summary, JsonElement regression, string fingerprint, string evidence)
    {
        List<string> findings = [];
        if (Text(summary, "Profile") != "Full" || Text(summary, "RequestedRegression") != "Full"
            || Text(regression, "ProductFingerprint") != fingerprint
            || !Text(regression, "Profile").StartsWith("Full Regression", StringComparison.Ordinal)
            || !regression.TryGetProperty("FastBuildGate", out JsonElement fast) || fast.ValueKind != JsonValueKind.False
            || !regression.TryGetProperty("Tests", out JsonElement tests) || tests.ValueKind != JsonValueKind.Array
            || tests.GetArrayLength() == 0)
            return new("Unverified", evidence, ["The report does not establish a complete regression for this product."]);

        foreach (JsonElement test in tests.EnumerateArray())
            if (!True(test, "Passed")) findings.Add("Full regression failure: " + Text(test, "Name"));
        bool failed = findings.Count > 0 || !True(regression, "Passed")
            || Text(summary, "Result") == "Failed" || Text(summary, "Regression") == "Failed";
        if (Text(summary, "Result") != "Passed" || Text(summary, "Regression") != "Passed")
            findings.Add("The Full Build and complete regression must both pass.");

        foreach (string backend in RequiredBackends)
            if (!Contains(summary, "RequestedBackends", backend) || !Contains(summary, "BackendCoverage", backend))
                findings.Add("Missing explicit renderer smoke: " + backend);
        if (!summary.TryGetProperty("SkippedBackends", out JsonElement skipped)
            || skipped.ValueKind != JsonValueKind.Array || skipped.GetArrayLength() != 0)
            findings.Add("Full acceptance cannot include skipped renderer coverage.");

        string[] stages = ["Audit package and engine assembly consistency", "Full regression tests",
            "Published Studio startup and bundled shader compiler smoke", "Promote successful build",
            .. RequiredBackends.Select(backend => "Explicit " + backend + " renderer smoke")];
        foreach (string stage in stages)
        {
            JsonElement[] matches = summary.TryGetProperty("Stages", out JsonElement recorded)
                && recorded.ValueKind == JsonValueKind.Array
                ? recorded.EnumerateArray().Where(item => Text(item, "Name") == stage).ToArray() : [];
            if (matches.Length != 1 || Text(matches[0], "Result") != "Passed")
            {
                findings.Add("Missing or failed build stage: " + stage);
                failed |= matches.Any(item => Text(item, "Result") == "Failed");
            }
        }
        return new(failed ? "Failed" : findings.Count == 0 ? "Passed" : "Unverified", evidence, findings);
    }

    private static string Text(JsonElement source, string name) => source.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool True(JsonElement source, string name) => source.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;
    private static bool Contains(JsonElement source, string name, string value) => source.TryGetProperty(name, out JsonElement array)
        && array.ValueKind == JsonValueKind.Array && array.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
            && string.Equals(item.GetString(), value, StringComparison.OrdinalIgnoreCase));
}
