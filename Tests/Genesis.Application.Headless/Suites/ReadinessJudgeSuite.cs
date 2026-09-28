using System.Security.Cryptography;

namespace Genesis.Application.Headless.Suites;

internal static class ReadinessJudgeSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Acceptance judges");
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.ContentAndPlayerChangesExpireEvidence", () =>
        {
            string root = Path.Combine(context.Workspace, "FingerprintProduct");
            string content = Path.Combine(root, "Projects", "Templates", "Assets", "TwoDShowcase");
            string player = Path.Combine(root, "Player");
            Directory.CreateDirectory(content); Directory.CreateDirectory(player);
            File.WriteAllBytes(Path.Combine(root, "Genesis.Runtime.dll"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(player, "Genesis.Runtime.dll"), [3, 2, 1]);
            string room = Path.Combine(content, "Meadow.room.json");
            File.WriteAllText(room, "{\"tiles\":[]}");
            string initial = ReadinessJudgeRunner.ProductFingerprint(root);
            File.WriteAllText(room, "{\"tiles\":[1]}");
            string changedTemplate = ReadinessJudgeRunner.ProductFingerprint(root);
            HeadlessHarness.Assert(initial != changedTemplate, "Template-only game changes did not expire acceptance evidence.");
            File.WriteAllBytes(Path.Combine(player, "Genesis.Runtime.dll"), [4, 5, 6]);
            string changedPlayer = ReadinessJudgeRunner.ProductFingerprint(root);
            HeadlessHarness.Assert(changedTemplate != changedPlayer, "A changed exported Player retained the same acceptance fingerprint.");
            File.WriteAllText(Path.Combine(content, "Time Bonus.pgsl"), "return argument0 * 10;");
            HeadlessHarness.Assert(changedPlayer != ReadinessJudgeRunner.ProductFingerprint(root), "Adding a gameplay resource did not expire evidence.");
            File.WriteAllText(Path.Combine(root, "test.log"), "Transient logs are outside the product.");
            string withLog = ReadinessJudgeRunner.ProductFingerprint(root);
            File.WriteAllText(Path.Combine(root, "test.log"), "Changed log.");
            HeadlessHarness.Assert(withLog == ReadinessJudgeRunner.ProductFingerprint(root), "Transient logs prevent stable product acceptance.");
        });
        JudgeSurface surface = new("Probe", null, "Editor.Probe");
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.MissingEvidenceCannotPass", () =>
        {
            JudgeDecision decision = ReadinessJudgeRunner.Decide(surface, [], [], null, context.Workspace);
            HeadlessHarness.Assert(decision.Functionality == "Unverified" && decision.Usability == "Unverified"
                && decision.Aesthetics == "Unverified", "The judge approved a surface without evidence.");
        });
        string file = Path.Combine(context.Workspace, "judge-capture-probe.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        JudgeCapture[] captures = new[] { "normal", "narrow", "scale125", "scale150", "scale200" }
            .Select(variant => new JudgeCapture("Probe", variant, Path.GetFileName(file), hash, 96, "fixture", [])).ToArray();
        JudgeRating rating = new("Probe", 4, 4, "Reviewed all current variants.", [hash]);
        TestCaseResult passed = new("Probe", "Editor.Probe", true, 1, null);
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.CompleteCurrentEvidenceCanPass", () =>
        {
            JudgeDecision decision = ReadinessJudgeRunner.Decide(surface, [passed], captures, rating, context.Workspace);
            HeadlessHarness.Assert(decision.Functionality == "Passed" && decision.Usability == "Passed"
                && decision.Aesthetics == "Passed", "The judge rejected complete current evidence.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.GoodRatingsCannotOverrideFunctionalFailure", () =>
        {
            JudgeDecision decision = ReadinessJudgeRunner.Decide(surface, [passed with { Passed = false, Error = "Save failed" }],
                captures, rating, context.Workspace);
            HeadlessHarness.Assert(decision.Functionality == "Failed", "Visual ratings erased a functional failure.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.PassingChildCannotReplaceCompleteWorkflow", () =>
        {
            JudgeDecision decision = ReadinessJudgeRunner.Decide(surface, [passed with { Name = "Editor.Probe.Save" }],
                captures, rating, context.Workspace);
            HeadlessHarness.Assert(decision.Functionality == "Unverified"
                && decision.Findings.Any(finding => finding.StartsWith("Complete workflow missing", StringComparison.Ordinal)),
                "A passing subtest substituted for an absent complete editor workflow.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.CompleteWorkflowCannotEraseFailedChild", () =>
        {
            JudgeDecision decision = ReadinessJudgeRunner.Decide(surface,
                [passed, passed with { Name = "Editor.Probe.Save", Passed = false, Error = "Save failed" }],
                captures, rating, context.Workspace);
            HeadlessHarness.Assert(decision.Functionality == "Failed", "A complete workflow erased a failing child check.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.UnrelatedEvidenceCannotApproveWorkflow", () =>
        {
            JudgeDecision decision = ReadinessJudgeRunner.Decide(surface, [passed with { Name = "Editor.Other" }],
                captures, rating, context.Workspace);
            HeadlessHarness.Assert(decision.Functionality == "Unverified", "An unrelated passing workflow approved this editor.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.UnwiredCodeCannotBeApproved", () =>
        {
            JudgeCapture[] unwired = captures.Select(capture => capture with
                { Findings = ["Code surface lacks a completion/signature provider."] }).ToArray();
            JudgeDecision decision = ReadinessJudgeRunner.Decide(surface, [passed], unwired, rating, context.Workspace);
            HeadlessHarness.Assert(decision.Functionality == "Failed" && decision.Usability == "Failed",
                "The judge approved an unwired scripting surface.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.AlteredPixelsInvalidateVisualRatings", () =>
        {
            File.WriteAllBytes(file, [9, 8, 7]);
            JudgeDecision decision = ReadinessJudgeRunner.Decide(surface, [passed], captures, rating, context.Workspace);
            HeadlessHarness.Assert(decision.Usability == "Unverified" && decision.Aesthetics == "Unverified",
                "A review of different pixels approved the changed capture.");
        });
    }
}
