using System.Security.Cryptography;
using System.Text.Json;

namespace Genesis.Application.Headless.Suites;

internal static class GameExportEvidenceSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Acceptance.ExportEvidence.MeadowAndVerdantKeepIndependentReports", () =>
        {
            HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string folder, string workflow, int count) in new[]
                     { ("MushroomMeadowExport", "RigPlaybackVerified", 8), ("VerdantHollowExport", "AuthoredTerrainSurfaceVerified", 7) })
                foreach (string backend in new[] { "DX11", "DX12", "Vulkan", "OpenGL", "Software" })
                {
                    string directory = Path.GetFullPath(Path.Combine(ctx.Captures, folder, backend));
                    string reportPath = Path.Combine(directory, "acceptance.json");
                    HeadlessHarness.Assert(File.Exists(reportPath), "An exported game's native report was lost: " + reportPath);
                    using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
                    JsonElement root = report.RootElement;
                    HeadlessHarness.Assert(root.GetProperty("Success").GetBoolean() && root.GetProperty(workflow).GetBoolean(),
                        "The report belongs to a failed or different game: " + reportPath);
                    JsonElement[] captures = root.GetProperty("Captures").EnumerateArray().ToArray();
                    HeadlessHarness.Assert(captures.Length == count, "An exported game's capture set was replaced: " + reportPath);
                    foreach (JsonElement capture in captures)
                    {
                        string file = Path.GetFullPath(capture.GetProperty("File").GetString()!);
                        HeadlessHarness.Assert(file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && files.Add(file) && File.Exists(file), "A capture was shared, misplaced or overwritten: " + file);
                        HeadlessHarness.Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) == capture.GetProperty("Sha256").GetString(),
                            "The original exported pixels changed after the other game ran: " + file);
                    }
                }
            HeadlessHarness.Assert(files.Count == 75, "The two games did not retain all 75 native frames.");
        });
    }
}
