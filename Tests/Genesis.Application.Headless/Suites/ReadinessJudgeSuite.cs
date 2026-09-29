using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Genesis.Application.Headless.Suites;

internal static class ReadinessJudgeSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Acceptance judges");
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.FullBuildRequiresMatchingCompleteRegressionAndAllSmokes", () =>
        {
            var fixture = FullBuildFixture();
            HeadlessHarness.Assert(Assess(fixture).State == "Passed", "A complete matching Full Build was rejected.");
            foreach (string backend in new[] { "dx11", "dx12", "vulkan", "opengl", "software" })
            {
                fixture = FullBuildFixture();
                JsonArray coverage = fixture.Summary["BackendCoverage"]!.AsArray();
                coverage.Remove(coverage.Single(item => item!.GetValue<string>() == backend));
                HeadlessHarness.Assert(Assess(fixture).State == "Unverified", "Full acceptance ignored the missing " + backend + " smoke.");
            }
            fixture = FullBuildFixture(); fixture.Summary["SkippedBackends"] = new JsonArray("dx11");
            HeadlessHarness.Assert(Assess(fixture).State == "Unverified", "Skipped coverage manufactured Full acceptance.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.FullBuildCannotHideFailuresOutsideEditorContracts", () =>
        {
            var fixture = FullBuildFixture();
            fixture.Regression["Tests"]![0]!["Name"] = "Render.FutureContract.UnknownToEditorJudge";
            fixture.Regression["Tests"]![0]!["Passed"] = false;
            HeadlessHarness.Assert(Assess(fixture).State == "Failed", "A failure outside the mandatory editor list was ignored.");
            fixture = FullBuildFixture(); fixture.Summary["Stages"]![0]!["Result"] = "Failed";
            HeadlessHarness.Assert(Assess(fixture).State == "Failed", "A failed package audit was ignored.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.FocusedQuickAndStaleEvidenceCannotReplaceFullBuild", () =>
        {
            foreach (string variant in new[] { "quick", "focused", "stale", "empty", "startup" })
            {
                var fixture = FullBuildFixture();
                if (variant == "quick") fixture.Summary["Profile"] = "Quick";
                if (variant == "focused") { fixture.Regression["Profile"] = "Focused Test (render)"; fixture.Regression["FastBuildGate"] = true; }
                if (variant == "stale") fixture.Regression["ProductFingerprint"] = "old";
                if (variant == "empty") fixture.Regression["Tests"] = new JsonArray();
                if (variant == "startup") fixture.Summary["Stages"]!.AsArray().RemoveAt(2);
                HeadlessHarness.Assert(Assess(fixture).State == "Unverified", variant + " evidence manufactured Full acceptance.");
            }
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.LatestFailedFullRunCannotBeErasedByFocusedRerun", () =>
        {
            string root = Path.Combine(context.Workspace, "FullBuildGate");
            Write("older", FullBuildFixture(), "2026-09-29T01:00:00Z");
            HeadlessHarness.Assert(ReadinessJudgeRunner.InspectFullBuild(root, "current").State == "Passed", "Matching Full report discovery failed.");
            var failed = FullBuildFixture(); failed.Regression["Tests"]![0]!["Passed"] = false;
            Write("newer", failed, "2026-09-29T02:00:00Z");
            var focused = FullBuildFixture(); focused.Summary["Profile"] = "Quick";
            Write("focused", focused, "2026-09-29T03:00:00Z");
            JudgeBuildGate gate = ReadinessJudgeRunner.InspectFullBuild(root, "current");
            HeadlessHarness.Assert(gate.State == "Failed" && gate.Evidence!.StartsWith("newer", StringComparison.Ordinal),
                "Older success or focused evidence erased the latest Full failure.");
            HeadlessHarness.Assert(ReadinessJudgeRunner.InspectFullBuild(root, "different").State == "Unverified", "Stale Full reports were accepted.");
            void Write(string name, (JsonObject Summary, JsonObject Regression) fixture, string completed)
            {
                string folder = Path.Combine(root, name); Directory.CreateDirectory(Path.Combine(folder, "Tests"));
                fixture.Regression["CompletedUtc"] = completed;
                File.WriteAllText(Path.Combine(folder, "BuildSummary.json"), fixture.Summary.ToJsonString());
                File.WriteAllText(Path.Combine(folder, "Tests", "results.json"), fixture.Regression.ToJsonString());
            }
        });
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
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.LayoutFindingsCannotBeApproved", () =>
        {
            foreach (string finding in new[]
                     {
                         "Clipped button: Create", "Small button: Apply (12px)",
                         "Code area exceeds its field width.", "Call signature or description is clipped.",
                         "Action toolbox has insufficient space to choose actions.",
                     })
            {
                JudgeCapture[] broken = captures.Select(capture => capture with { Findings = [finding] }).ToArray();
                JudgeDecision decision = ReadinessJudgeRunner.Decide(surface, [passed], broken, rating, context.Workspace);
                HeadlessHarness.Assert(decision.Usability == "Failed" && decision.Aesthetics == "Failed",
                    "High manual ratings erased a layout finding: " + finding);
            }
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.ThreeDRequiresModelTerrainAndEveryExportedBackend", () =>
        {
            JudgeDecision[] complete = ReadinessJudgeRunner.Surfaces.Select(item => new JudgeDecision(item.Name,
                item.RequiredFor2D, "Passed", "Passed", "Passed", 4, 4, [])).ToArray();
            TestCaseResult[] checks = ReadinessJudgeRunner.ThreeDRequirements.Select(name => new TestCaseResult("3D", name, true, 1, null)).ToArray();
            JudgeRequirement[] requirements = ReadinessJudgeRunner.Requirements(ReadinessJudgeRunner.ThreeDRequirements, checks);
            HeadlessHarness.Assert(ReadinessJudgeRunner.Complete(complete, requirements), "Complete 3D contract was rejected.");
            foreach (string required in new[] { "Model", "Terrain" })
            {
                JudgeDecision[] missing = complete.Select(item => item.Surface == required ? item with { Functionality = "Unverified" } : item).ToArray();
                HeadlessHarness.Assert(!ReadinessJudgeRunner.Complete(missing, requirements), "3D ignored its missing " + required + " workflow.");
            }
            foreach (string backend in new[] { "DX11", "DX12", "Vulkan", "OpenGL", "Software" })
            {
                string required = "Acceptance.VerdantHollow.Export." + backend;
                JudgeRequirement[] missing = ReadinessJudgeRunner.Requirements(ReadinessJudgeRunner.ThreeDRequirements, checks.Where(item => item.Name != required).ToArray());
                HeadlessHarness.Assert(!ReadinessJudgeRunner.Complete(complete, missing), "3D passed without the exported " + backend + " game.");
            }
            JudgeRequirement[] failed = ReadinessJudgeRunner.Requirements(ReadinessJudgeRunner.ThreeDRequirements,
                checks.Append(new("3D", "Runtime.Terrain.Parts.PhysicsEnterExitScriptsUseRealContacts", false, 1, "No contacts")).ToArray());
            HeadlessHarness.Assert(!ReadinessJudgeRunner.Complete(complete, failed), "A functional 3D failure was erased by good editor ratings.");
            HeadlessHarness.Assert(!ReadinessJudgeRunner.Complete(complete, requirements.Where(item => !item.Name.StartsWith("Acceptance.VerdantHollow.Export.", StringComparison.Ordinal)).ToArray()),
                "Omitting the export contract manufactured 3D acceptance.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.TwoDReviewsCannotReplaceThreeDRoomAndPathing", () =>
        {
            foreach (string name in new[] { "Room", "Pathing" })
            {
                JudgeSurface required = ReadinessJudgeRunner.Surfaces.Single(item => item.Name == name);
                TestCaseResult workflow = passed with { Name = required.Workflow };
                JudgeCapture[] onlyTwoD = captures.Select(item => item with { Surface = name }).ToArray();
                JudgeDecision incomplete = ReadinessJudgeRunner.Decide(required, [workflow], onlyTwoD,
                    rating with { Surface = name }, context.Workspace, ReadinessJudgeRunner.ThreeDVariants(name));
                HeadlessHarness.Assert(incomplete.Usability == "Unverified" && incomplete.Aesthetics == "Unverified",
                    "2D pixels approved a missing 3D " + name + " workspace.");
            }
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.SelectiveCapturesRetainCurrentStatesAndExpireOldBuilds", () =>
        {
            JudgeCapture first = captures[0], replacement = first with { Sha256 = "changed", Findings = ["Clipped button"] };
            JudgeCapture other = first with { Surface = "Other" };
            JudgeCaptureManifest previous = new("current", [first, other]);
            JudgeCaptureManifest merged = ReadinessJudgeRunner.MergeCaptures("current", previous, [replacement]);
            HeadlessHarness.Assert(merged.Captures.Count == 2 && merged.Captures.Single(item => item.Surface == first.Surface).Sha256 == "changed"
                && merged.Captures.Any(item => item.Surface == "Other"), "Selective inspection lost another current surface or preserved superseded pixels.");
            JudgeCaptureManifest changed = ReadinessJudgeRunner.MergeCaptures("new build", previous, [replacement]);
            HeadlessHarness.Assert(changed.Captures.Count == 1 && changed.Captures[0].Sha256 == "changed",
                "Selective inspection retained old-build captures under a new fingerprint.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Judge.AlteredPixelsInvalidateVisualRatings", () =>
        {
            File.WriteAllBytes(file, [9, 8, 7]);
            JudgeDecision decision = ReadinessJudgeRunner.Decide(surface, [passed], captures, rating, context.Workspace);
            HeadlessHarness.Assert(decision.Usability == "Unverified" && decision.Aesthetics == "Unverified",
                "A review of different pixels approved the changed capture.");
        });
    }

    private static JudgeBuildGate Assess((JsonObject Summary, JsonObject Regression) fixture)
    {
        using JsonDocument summary = JsonDocument.Parse(fixture.Summary.ToJsonString());
        using JsonDocument regression = JsonDocument.Parse(fixture.Regression.ToJsonString());
        return ReadinessJudgeRunner.AssessFullBuild(summary.RootElement, regression.RootElement, "current", "fixture");
    }

    private static (JsonObject Summary, JsonObject Regression) FullBuildFixture()
    {
        string[] backends = ["dx11", "dx12", "vulkan", "opengl", "software"];
        string[] stages = ["Audit package and engine assembly consistency", "Full regression tests",
            "Published Studio startup and bundled shader compiler smoke", "Promote successful build",
            .. backends.Select(backend => "Explicit " + backend + " renderer smoke")];
        return (new JsonObject
        {
            ["Profile"] = "Full", ["RequestedRegression"] = "Full", ["Result"] = "Passed", ["Regression"] = "Passed",
            ["RequestedBackends"] = new JsonArray(backends.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["BackendCoverage"] = new JsonArray(backends.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["SkippedBackends"] = new JsonArray(),
            ["Stages"] = new JsonArray(stages.Select(name => (JsonNode?)new JsonObject { ["Name"] = name, ["Result"] = "Passed" }).ToArray())
        }, new JsonObject
        {
            ["ProductFingerprint"] = "current", ["Profile"] = "Full Regression", ["FastBuildGate"] = false, ["Passed"] = true,
            ["Tests"] = new JsonArray(new JsonObject { ["Name"] = "Render.RealCheck", ["Passed"] = true })
        });
    }
}
