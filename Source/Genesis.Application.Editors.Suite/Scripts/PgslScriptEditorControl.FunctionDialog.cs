using System.Text.RegularExpressions;
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Application.Editors.Suite.UiKit;

namespace Genesis.Application.Editors.Suite.Scripts;

public sealed partial class PgslScriptEditorControl
{
    private DpiAwareForm CreateFunctionDialog(ScriptRoutine? existing)
    {
        float scale = Math.Max(1, EditorChrome.BaseFont.SizeInPoints / 9.5f);
        DpiAwareForm dialog = new()
        {
            Text = existing is null ? "Add Function" : "Edit Function Signature",
            ClientSize = new Size((int)(500 * scale), (int)(345 * scale)),
            StartPosition = FormStartPosition.CenterParent, Font = EditorChrome.BaseFont,
            BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text,
        };
        string defaultName = "NewFunction";
        for (int suffix = 2; ParseRoutines(_code.CodeText).Any(routine => routine.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase)); suffix++)
            defaultName = "NewFunction" + suffix;
        TextBox name = new() { Text = existing?.Name ?? defaultName, Name = "ScriptFunctionName" };
        TextBox parameters = new()
        {
            Text = existing is null ? string.Empty : string.Join(", ", existing.Parameters.Select(parameter => $"{parameter.Name}: {parameter.Type}")),
            PlaceholderText = "distance: Float, fast: Boolean", Name = "ScriptFunctionInputs",
        };
        ThemedComboBox returns = new() { DropDownStyle = ComboBoxStyle.DropDownList, Name = "ScriptFunctionReturnType" };
        returns.Items.AddRange(["Void", .. Enum.GetNames<BlueprintValueType>()]);
        returns.SelectedItem = existing?.ReturnType ?? "Void";
        if (returns.SelectedIndex < 0) returns.SelectedIndex = 0;
        EditorChrome.StyleField(name); EditorChrome.StyleField(parameters); EditorChrome.StyleField(returns);
        FlowLayoutPanel fields = new()
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(14), BackColor = EditorChrome.Surface,
        };
        void Label(string text) => fields.Controls.Add(new Label { AutoSize = true, Text = text,
            ForeColor = EditorChrome.Muted, Margin = new Padding(3, 8, 3, 4) });
        Label("A function packages actions you can call again. Inputs are values supplied by its caller.");
        Label("Function name"); fields.Controls.Add(name);
        Label("Inputs — optional, separated by commas"); fields.Controls.Add(parameters);
        Label("Return type — Void performs actions without returning a value"); fields.Controls.Add(returns);
        Label error = new() { Dock = DockStyle.Bottom, Height = EditorChrome.BaseFont.Height * 2 + 12,
            ForeColor = EditorChrome.Error, Padding = new Padding(14, 2, 14, 2),
            Name = "ScriptFunctionError", Visible = false };
        Button create = new() { Text = existing is null ? "Create function" : "Update signature",
            AutoSize = true, Name = "ScriptFunctionConfirm", MinimumSize = new Size(100, EditorChrome.BaseFont.Height + 14) };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel,
            AutoSize = true, MinimumSize = new Size(80, EditorChrome.BaseFont.Height + 14) };
        EditorChrome.StyleField(create); EditorChrome.StyleField(cancel);
        FlowLayoutPanel footer = new() { Dock = DockStyle.Bottom, Height = EditorChrome.BaseFont.Height + 30,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 2, 8, 2), WrapContents = false };
        footer.Controls.Add(create); footer.Controls.Add(cancel);
        create.Click += (_, _) =>
        {
            void Reject(string reason) { error.Text = reason; error.Visible = true; }
            string functionName = name.Text.Trim();
            if (!Regex.IsMatch(functionName, @"^[A-Za-z_]\w*$")
                || PgslCodeIntelligenceProvider.Keywords.Contains(functionName, StringComparer.OrdinalIgnoreCase))
            { Reject("Use a name beginning with a letter or underscore; avoid PGSL keywords."); return; }
            if (ParseRoutines(_code.CodeText).Any(routine => !routine.Name.Equals(existing?.Name, StringComparison.OrdinalIgnoreCase)
                && routine.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase)))
            { Reject("A function with that name already exists in this Script."); return; }
            List<RoutineParameter> parsed = [];
            foreach (string token in parameters.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = token.Split(':', 2, StringSplitOptions.TrimEntries);
                if (!Regex.IsMatch(parts[0], @"^[A-Za-z_]\w*$")
                    || PgslCodeIntelligenceProvider.Keywords.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
                    || parsed.Any(parameter => parameter.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase)))
                { Reject("Each input needs a distinct PGSL name, such as distance: Float."); return; }
                BlueprintValueType type = BlueprintValueType.Float;
                if (parts.Length == 2 && (!Enum.TryParse(parts[1], true, out type) || !Enum.IsDefined(type)))
                { Reject("Input types are Float, Int, String, Boolean and Vector3."); return; }
                parsed.Add(new RoutineParameter(parts[0], type));
            }
            dialog.Tag = new FunctionSignature(functionName, parsed, returns.SelectedItem?.ToString() ?? "Void");
            dialog.DialogResult = DialogResult.OK;
        };
        void SizeFields()
        {
            int width = Math.Max(100, fields.ClientSize.Width - fields.Padding.Horizontal - 28);
            foreach (Control field in fields.Controls)
            {
                field.Width = width;
                if (field is Label label) label.MaximumSize = new Size(width, 0);
            }
        }
        fields.SizeChanged += (_, _) => SizeFields();
        dialog.Controls.Add(fields); dialog.Controls.Add(error); dialog.Controls.Add(footer);
        dialog.AcceptButton = create; dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => { SizeFields(); name.Focus(); name.SelectAll(); };
        SizeFields();
        return dialog;
    }
}
