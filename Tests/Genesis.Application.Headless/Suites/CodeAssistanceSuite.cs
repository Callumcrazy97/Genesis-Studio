using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Application.Editors.Suite.Terrain;

namespace Genesis.Application.Headless.Suites;

internal static class CodeAssistanceSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Code assistance");
        HeadlessHarness.RunCase(context.Report, "Acceptance.CodeAssistance.TerrainRecipeArgumentsAndGeneratedEdits", () =>
        {
            using TerrainSourceWizard wizard = new(new TerrainCreationRecipe
            {
                Source = TerrainCreationSource.Code, Width = 24, Length = 24, Spacing = 4, Code = "height = 3;"
            }, context.Workspace);
            Genesis.Application.Studio.Theme.ThemeService.Apply(wizard);
            GateSuite.ShowHost(wizard);
            CodeEditor editor = wizard.Code;
            HeadlessHarness.Assert(editor.Width >= 480 && editor.Height >= 300 && editor.HasIntelligenceProvider,
                "The terrain generator does not provide a usable code surface with assistance.");
            editor.CodeText = "height = clamp(noise(x, z), /* lower bound, */ ";
            editor.MoveCaret(editor.CodeText.Length);
            HeadlessHarness.Assert(editor.SignatureVisible && editor.SignatureText.Contains("number minimum", StringComparison.Ordinal)
                && editor.ActiveParameterIndex == 1 && editor.CaretStatusText.Contains("Argument 2", StringComparison.Ordinal),
                "Terrain argument hints lost the outer call, type or current argument.");
            editor.CodeText = "TerrainSp"; editor.MoveCaret(editor.CodeText.Length);
            HeadlessHarness.Assert(editor.CompletionItems.Any(item => item.InsertText == "TerrainSpacing")
                && editor.CommitSelectedCompletion() && editor.CodeText == "TerrainSpacing",
                "The recipe's real spacing command cannot be completed.");
            editor.CodeText="function Hill(amplitude, frequency) { return amplitude * sin(x * frequency); }\nHill(5, ";
            editor.MoveCaret(editor.CodeText.Length);
            HeadlessHarness.Assert(editor.ActiveParameterIndex==1&&editor.SignatureText.Contains("any frequency",StringComparison.Ordinal),
                "Recipe functions declared in this file do not expose their real argument list.");
            editor.CodeText = "height = 3;";
            Generate();
            HeadlessHarness.Assert(wizard.Result?.Heights is { } first && first.Cast<float>().All(value => value == 3),
                "The initial edited recipe did not generate its actual heights.");
            editor.TextBox.SelectAll(); editor.TextBox.SelectedText = "height = 7;";
            HeadlessHarness.Assert(wizard.Result is null && !Descendants(wizard).OfType<Button>().Single(button => button.Text == "Create section").Enabled,
                "Editing the code left an obsolete preview available for creation.");
            Generate();
            HeadlessHarness.Assert(wizard.Result?.Heights is { } second && second.Cast<float>().All(value => value == 7),
                "Generation ignored the recipe typed into the actual editor.");
            void Generate()
            {
                Task generation = wizard.GeneratePreviewAsync();
                long deadline = Environment.TickCount64 + 15000;
                while (!generation.IsCompleted && Environment.TickCount64 < deadline) GateSuite.Pump(1, 5);
                HeadlessHarness.Assert(generation.IsCompleted, "Terrain recipe generation timed out.");
                generation.GetAwaiter().GetResult();
            }
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.CodeAssistance.AssetDefinitionsKeepEditingSpaceAndSave", () =>
        {
            foreach (string kind in new[] { "Particle", "Physics", "Shader" })
            {
                string path = Path.Combine(context.OutputRoot, "Layout." + kind.ToLowerInvariant() + ".json");
                File.WriteAllText(path, "{}");
                using Form host = GateSuite.NewHost(1040, 700);
                using EditorSurfaceControl editor = kind switch
                {
                    "Particle" => new ParticleEditorControl(path, context.Workspace),
                    "Physics" => new PhysicsEditorControl(path, context.Workspace),
                    _ => new ShaderEditorControl(path, context.Workspace),
                };
                host.Controls.Add(editor); GateSuite.ShowHost(host);
                switch (editor)
                {
                    case ParticleEditorControl particles: particles.SetAuthoringMode(ParticleAuthoringMode.Code); break;
                    case PhysicsEditorControl physics: physics.SetAuthoringMode(PhysicsAuthoringMode.Code); break;
                    case ShaderEditorControl shader: shader.SetAuthoringMode(ShaderAuthoringMode.Code); break;
                }
                host.ClientSize = new Size(620, 400); GateSuite.Pump(2, 10);
                CodeEditor code = Descendants(editor).OfType<CodeEditor>().Single();
                EditorCommandBar commands = editor.Controls.OfType<EditorCommandBar>().Single();
                HeadlessHarness.Assert(code.Visible && code.Width >= 500 && code.Height >= 190,
                    kind + " left too little editing space in a narrow dock.");
                HeadlessHarness.Assert(commands.IsSaveVisible && commands.IsDocumentStateVisible,
                    kind + " hid Save or document state in a narrow dock.");
            }
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.CodeAssistance.ScriptCodeKeepsEditingSpace", () =>
        {
            string path = Path.Combine(context.OutputRoot, "layout.pgsl");
            File.WriteAllText(path, "function Travel(speed) { return speed; }");
            using Form host = GateSuite.NewHost(1040, 700);
            using PgslScriptEditorControl editor = new(path, context.Workspace);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            editor.SetAuthoringMode(PgslScriptAuthoringMode.Code);
            GateSuite.Pump(2, 10);
            HeadlessHarness.Assert(editor.Code.Width >= 780 && !editor.Builder.Visible,
                "Builder panels crowd the code surface.");
            host.ClientSize = new Size(620, 400);
            GateSuite.Pump(2, 10);
            HeadlessHarness.Assert(editor.Code.Width >= 600, "The narrow script editor did not prioritize editable code.");
            editor.ScriptText = "function Broken() { DrawSprite(";
            HeadlessHarness.Assert(editor.ErrorCount > 0 && editor.Code.Height >= 150,
                "Diagnostics either vanished or left no usable editing area.");
            editor.SetAuthoringMode(PgslScriptAuthoringMode.Builder);
            HeadlessHarness.Assert(editor.Builder.Visible, "Returning to Builder lost the authored workflow.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.CodeAssistance.PersistentPositionAndTypedArguments", () =>
        {
            using Form host = GateSuite.NewHost(760, 420);
            using CodeEditor editor = new();
            AssetCodeIntelligenceProvider.AttachHlsl(editor);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            editor.CodeText = "float4 Tint(float4 colour, float strength) { return colour * strength; }\nTint(float4(1, 0, 0, 1), ";
            editor.MoveCaret(editor.CodeText.Length);
            HeadlessHarness.Assert(editor.SignatureVisible && editor.SignatureText.Contains("float strength", StringComparison.Ordinal)
                && editor.ActiveParameterIndex == 1 && editor.CaretStatusText.Contains("Ln 2", StringComparison.Ordinal)
                && editor.CaretStatusText.Contains("Argument 2", StringComparison.Ordinal),
                "The bottom bar did not show the declared function's type, active argument and caret position.");
            editor.CodeText = "\nfloat4 result = lerp(first, /* comma, */ second, ";
            editor.MoveCaret(editor.CodeText.Length);
            HeadlessHarness.Assert(editor.ActiveParameterIndex == 2 && editor.SignatureText.Contains("floatN amount", StringComparison.Ordinal),
                "Comments or nested expressions moved signature assistance to the wrong argument.");
            try
            {
                var settings = new Genesis.Application.Core.Settings.GenesisSettings();
                settings.Appearance.InterfaceScale = 2;
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(settings);
                Genesis.Application.Studio.Docking.SuiteChromeBridge.Push();
                Genesis.Application.Studio.Theme.ThemeService.Apply(editor);
                GateSuite.Pump(2, 10);
                RichTextBox signature = editor.Controls.OfType<Panel>()
                    .SelectMany(panel => panel.Controls.OfType<RichTextBox>()).Single(text => text.ReadOnly);
                HeadlessHarness.Assert(signature.Height >= EditorChrome.SmallFont.Height * 2 + 8
                    && editor.ActiveParameterIndex == 2,
                    "The enlarged bottom strip clipped its signature or active-argument context.");
            }
            finally
            {
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(new());
                Genesis.Application.Studio.Docking.SuiteChromeBridge.Push();
                Genesis.Application.Studio.Theme.ThemeService.Apply(editor);
            }
            host.ClientSize = new Size(290, 230);
            editor.CodeText = "flo";
            editor.MoveCaret(3);
            HeadlessHarness.Assert(editor.CompletionItems.Any(item => item.InsertText == "float4"), "HLSL type completion is missing.");
            HeadlessHarness.Assert(editor.CaretStatusText.Contains("Col 4", StringComparison.Ordinal), "Caret position vanished outside a call.");
            Label footer = editor.Controls.OfType<Label>().Single(label => label.Name == "CodeCaretStatus");
            HeadlessHarness.Assert(footer.Bottom <= editor.ClientSize.Height && footer.Width == editor.ClientSize.Width,
                "The footer leaves the narrow editor's bounds.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.CodeAssistance.DeclarationsCompleteFieldsAndTypedValues", () =>
        {
            foreach ((string field, string value, Action<CodeEditor> attach) in new (string, string, Action<CodeEditor>)[]
            {
                ("render.bl", "render.blend: Add", AssetCodeIntelligenceProvider.AttachParticle),
                ("solver.ite", "body: Dyn", AssetCodeIntelligenceProvider.AttachPhysics),
                ("stopping_dis", "loop: Ping", AssetCodeIntelligenceProvider.AttachPathing),
            })
            {
                using Form host = GateSuite.NewHost(560, 350);
                using CodeEditor editor = new();
                attach(editor);
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                editor.CodeText = field;
                editor.MoveCaret(field.Length);
                HeadlessHarness.Assert(editor.HasIntelligenceProvider && editor.CompletionItems.Count > 0,
                    "Asset field completion is missing for " + field);
                HeadlessHarness.Assert(editor.CommitSelectedCompletion() && editor.CodeText.Length > field.Length,
                    "Completion did not insert the selected field.");
                editor.CodeText = value;
                editor.MoveCaret(value.Length);
                HeadlessHarness.Assert(editor.CompletionItems.Count > 0 && editor.CaretStatusText.Contains(':', StringComparison.Ordinal),
                    "Valid enum values and their field type were not offered for " + value);
            }
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.CodeAssistance.PgslCommandsAndLocalFunctionHints", () =>
        {
            using Form host = GateSuite.NewHost(760, 420);
            using CodeEditor editor = new();
            editor.SetLanguage("PGSL");
            editor.IntelligenceRequested += (_, request) => PgslCodeIntelligenceProvider.ApplyRequest(editor, context.Workspace, editor.CodeText, request);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            editor.CodeText = "function Travel(target, speed) { return speed; }\nTravel(player, ";
            editor.MoveCaret(editor.CodeText.Length);
            HeadlessHarness.Assert(editor.SignatureText.Contains("any speed", StringComparison.Ordinal) && editor.ActiveParameterIndex == 1,
                "PGSL's local function arguments are missing.");
            editor.CodeText = "SpriteRigPlay(\"walk\", ";
            editor.MoveCaret(editor.CodeText.Length);
            HeadlessHarness.Assert(editor.SignatureText.Contains("string", StringComparison.Ordinal) && editor.ActiveParameterIndex == 1,
                "The gameplay sprite-rig command does not expose its real parameter types.");
            editor.CodeText = string.Empty;
            editor.RefreshIntelligence(forceCompletion: true);
            HeadlessHarness.Assert(editor.CompletionItems.Count > 0, "Ctrl+Space cannot offer PGSL symbols at an empty caret.");
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        foreach (Control nested in Descendants(child)) yield return nested;
    }
}
