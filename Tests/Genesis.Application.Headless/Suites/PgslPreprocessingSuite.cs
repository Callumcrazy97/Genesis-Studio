using System.Globalization;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static class PgslPreprocessingSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Runtime.Pgsl.Preprocessing");
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.Preprocessing.StringOverridesAndControlFlow",
            () => CheckStringOverrides(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Pgsl.Preprocessing.CommentsPreserveTokensAndSourceLines",
            CheckComments);
    }

    private static void CheckStringOverrides(HeadlessContext ctx)
    {
        const string source = """
            var LiteralUrl = "https://original.example.org/path";
            var LiteralControlText = "if (a=b) while (ready=false)";
            var LiteralCommentText = "/* literal block */ // literal line";
            var LiteralEscapes = "quoted \"word\" and C:\\Models\\Archer";
            var LiteralCollision = "__GENESIS_PGSL_LITERAL_0";
            var LiteralMultiline = "first line
            // still literal
            if (a=b) /* still literal */";
            var LiteralProbe = 0;
            if (LiteralControlText = "if (a=b) while (ready=false)") {
                LiteralProbe = 2;
            }
            while (LiteralProbe < 5) {
                LiteralProbe += 1;
            }
            """;
        const string replacement = "https://example.org/a?x=1 // data\nif (a=b) /* text */ \"quoted\" C:\\Models\\Archer";
        string directory = Path.Combine(ctx.Workspace, "PgslLiteralSource");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Create.pgsl");
        File.WriteAllText(path, source);
        byte[] before = File.ReadAllBytes(path);
        DateTime writeTime = File.GetLastWriteTimeUtc(path);
        string specialized = PgslExposedVariables.ApplyOverrides(File.ReadAllText(path),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["LiteralUrl"] = replacement });

        Dictionary<string, object> overridden = Execute("LiteralOverride", specialized, out string processed);
        Dictionary<string, object> inherited = Execute("LiteralInherited", source, out _);
        Assert((string)overridden["LiteralUrl"] == replacement
               && (string)inherited["LiteralUrl"] == "https://original.example.org/path",
            "String overrides were altered by preprocessing or leaked between VM instances.");
        Assert((string)overridden["LiteralControlText"] == "if (a=b) while (ready=false)"
               && (string)overridden["LiteralCommentText"] == "/* literal block */ // literal line"
               && (string)overridden["LiteralEscapes"] == "quoted \"word\" and C:\\Models\\Archer"
               && (string)overridden["LiteralCollision"] == "__GENESIS_PGSL_LITERAL_0"
               && ((string)overridden["LiteralMultiline"]).Replace("\r\n", "\n", StringComparison.Ordinal)
                   == "first line\n// still literal\nif (a=b) /* still literal */",
            "Comment removal or shorthand normalization modified quoted PGSL literal data.");
        Assert(Convert.ToDouble(overridden["LiteralProbe"], CultureInfo.InvariantCulture) == 5d
               && Convert.ToDouble(inherited["LiteralProbe"], CultureInfo.InvariantCulture) == 5d,
            "Protecting literal data prevented genuine if/while shorthand from executing.");
        Assert(processed.Contains("if (a=b) while (ready=false)", StringComparison.Ordinal),
            "The effective compiler source no longer contains the original string text.");
        Assert(before.SequenceEqual(File.ReadAllBytes(path)) && writeTime == File.GetLastWriteTimeUtc(path),
            "Instance specialization or compilation wrote to the authored event file.");
    }

    private static void CheckComments()
    {
        const string source = "var/* separator */ CommentProbe = 3;\r\n"
            + "/* block comment\r\nvar CommentProbe = 99;\r\n*/\r\n"
            + "// if (CommentProbe = 99) {\r\n"
            + "if (CommentProbe = 3) { CommentProbe += 4; }\r\n";
        Dictionary<string, object> values = Execute("CommentWhitespace", source, out string processed);
        Assert(Convert.ToDouble(values["CommentProbe"], CultureInfo.InvariantCulture) == 7d,
            "Removing comments merged identifier tokens or executed commented-out source.");
        Assert(source.Count(character => character == '\n') == processed.Count(character => character == '\n')
               && source.Count(character => character == '\r') == processed.Count(character => character == '\r'),
            "Comment preprocessing moved authored diagnostic source lines.");
    }

    private static Dictionary<string, object> Execute(string name, string source, out string processed)
    {
        PgslVm vm = VMEngine.CreateVm(debug: false);
        PgslContext? previousContext = VMEngine.Bridge.GetContext();
        PgslContext? previousCommands = PgslCommands.GetContext();
        string? previousProject = PgslCommands.ProjectPath;
        try
        {
            PgslCommands.ProjectPath = null;
            VMEngine.Bridge.SetContext(new PgslContext());
            CompiledScriptAsset compiled = ScriptAssetCompiler.Compile(name, source);
            processed = compiled.ProcessedSource;
            vm.Execute(compiled.CompileResult.Instructions, compiled.CompileResult.Constants);
            return vm.GetVariables();
        }
        finally
        {
            PgslCommands.ProjectPath = previousProject;
            VMEngine.Bridge.SetContext(previousContext);
            PgslCommands.BindContext(previousCommands);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
