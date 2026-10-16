using System.Drawing;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Studio.Assistant;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Theme;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Studio.Docking;

/// <summary>
/// The AI assistant: a local GGUF model (run by llama.cpp, as in Nova) that answers PGSL questions
/// or writes PGSL that the PGSL checker then checks, sending the checker's errors back to the model
/// up to three times. Images, rooms and scripts can be attached; images are described in words so a
/// model without vision can still use them. Nothing leaves this PC.
/// </summary>
public sealed class AssistantDock : GenesisDockContent
{
    private const int CheckRounds = 3;
    private readonly ProjectSession _project;
    private readonly AssistantSettings _settings = AssistantSettings.Load();
    private readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, AccessibleName = "Model" };
    private readonly ModernButton _start = new() { Text = "Start", Accent = true, Glyph = "▶" };
    private readonly ModernButton _gear = new() { Text = "⚙", Width = 40 };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(8, 10, 0, 0) };
    private readonly RadioButton _ask = new() { Text = "Ask", Checked = true, AutoSize = true, Margin = new Padding(0, 8, 12, 0) };
    private readonly RadioButton _write = new() { Text = "Write PGSL", AutoSize = true, Margin = new Padding(0, 8, 12, 0) };
    private readonly RadioButton _game = new() { Text = "Make a game", AutoSize = true, Margin = new Padding(0, 8, 16, 0) };
    private readonly Label _attached = new() { AutoSize = true, Margin = new Padding(8, 10, 0, 0) };
    private readonly RichTextBox _transcript = new() { ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, DetectUrls = false };
    private readonly TextBox _input = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly ModernButton _send = new() { Text = "Send", Accent = true, Glyph = "➤", Dock = DockStyle.Right, Width = 110 };
    private readonly ModernButton _copy = new() { Text = "Copy code", Visible = false };
    private readonly ModernButton _save = new() { Text = "Save as Script…", Visible = false };
    private readonly List<(string Role, string Content)> _history = [];
    private readonly List<(string Name, string Text)> _attachments = [];
    private string _lastCode = string.Empty;
    private bool _busy;
    // What the assistant can do in Studio, supplied by the shell.
    private readonly Func<string, string> _openResource;
    private readonly Func<Control?> _activeEditor;
    private readonly Func<IReadOnlyList<string>> _resourceNames;
    private readonly Func<string, string?> _resourcePath;

    public AssistantDock(ProjectSession project, Func<string, string>? openResource = null, Func<Control?>? activeEditor = null,
        Func<IReadOnlyList<string>>? resourceNames = null, Func<string, string?>? resourcePath = null)
    {
        _project = project;
        _openResource = openResource ?? (name => "Opening resources is not available here.");
        _activeEditor = activeEditor ?? (() => null);
        _resourceNames = resourceNames ?? (() => []);
        _resourcePath = resourcePath ?? (_ => null);
        Text = "AI Assistant";
        TabText = "AI Assistant";
        DockAreas = DockAreas.DockRight | DockAreas.DockLeft | DockAreas.DockBottom | DockAreas.Float;
        ShowHint = DockState.DockRight;
        HideOnClose = true;

        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10), Tag = "surface" };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        FlowLayoutPanel header = Row(_model, _start, _gear, _status);
        layout.Controls.Add(header, 0, 0);

        ModernButton attachImage = new() { Text = "Image…", Glyph = "▣" };
        ModernButton attachRoom = new() { Text = "Room…", Glyph = "▱" };
        ModernButton attachScript = new() { Text = "Script…", Glyph = "ƒ" };
        ModernButton clear = new() { Text = "Clear", Glyph = "✕" };
        attachImage.Click += (_, _) => Attach("Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif", AssistantKnowledge.DescribeImage);
        attachRoom.Click += (_, _) => Attach("Rooms|*.room.json", AssistantKnowledge.DescribeRoom);
        attachScript.Click += (_, _) => Attach("PGSL|*.pgsl", path => "Script " + Path.GetFileName(path) + ":\n" + Truncate(File.ReadAllText(path), 4000));
        clear.Click += (_, _) => ClearConversation();
        layout.Controls.Add(Row(_ask, _write, _game, attachImage, attachRoom, attachScript, clear, _attached), 0, 1);

        _transcript.Font = ThemeService.InterfaceFont;
        layout.Controls.Add(_transcript, 0, 2);

        Panel compose = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), Tag = "surface" };
        _input.PlaceholderText = "Ask about PGSL, or describe what the code should do… (Ctrl+Enter sends)";
        _input.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = SendAsync(); }
        };
        _send.Click += async (_, _) => await SendAsync();
        compose.Controls.Add(_input);
        compose.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, Tag = "surface" });
        compose.Controls.Add(_send);
        layout.Controls.Add(compose, 0, 3);

        _copy.Click += (_, _) => { if (_lastCode.Length > 0) Clipboard.SetText(_lastCode); };
        _save.Click += (_, _) => SaveScript();
        layout.Controls.Add(Row(_copy, _save), 0, 4);

        _start.Click += async (_, _) => await ToggleServerAsync();
        _gear.Click += (_, _) => ShowSettingsMenu();
        _model.SelectedIndexChanged += (_, _) => ModelChosen();
        LocalModelServer.Shared.StateChanged += OnServerChanged;
        Disposed += (_, _) => LocalModelServer.Shared.StateChanged -= OnServerChanged;

        FillModels();
        ShowServerState();
        Say("Assistant", "I run on this PC with a GGUF model; nothing is sent anywhere. Choose a model, press Start, then ask a "
            + "question, or switch to Write PGSL and describe what the code should do. I check the code with the PGSL checker before "
            + "you see it. Attach an image, room or script to give me context.");
        ThemeService.Apply(this);
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        FlowLayoutPanel row = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Tag = "surface" };
        foreach (Control control in controls)
        {
            if (control is ModernButton button)
            {
                ShellDialogLayout.FitButton(button);
                button.Margin = new Padding(0, 0, 6, 4);
            }
            row.Controls.Add(control);
        }
        return row;
    }

    // ── Model and server ───────────────────────────────────────────────────────────────────

    private void FillModels()
    {
        _model.Items.Clear();
        foreach (string model in _settings.FindModels()) _model.Items.Add(new ModelChoice(model));
        _model.Items.Add("Browse for a model…");
        int chosen = _model.Items.OfType<ModelChoice>().ToList().FindIndex(choice => string.Equals(choice.Path, _settings.ModelPath, StringComparison.OrdinalIgnoreCase));
        _model.SelectedIndex = chosen >= 0 ? chosen : (_model.Items.Count > 1 ? 0 : -1);
    }

    private sealed record ModelChoice(string Path)
    {
        public override string ToString() => System.IO.Path.GetFileNameWithoutExtension(Path);
    }

    private void ModelChosen()
    {
        if (_model.SelectedItem is ModelChoice choice)
        {
            _settings.ModelPath = choice.Path;
            SaveSettings();
            return;
        }
        if (_model.SelectedItem is not string) return;
        using OpenFileDialog dialog = new() { Filter = "GGUF models|*.gguf", Title = "Choose a GGUF model" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _settings.ModelPath = dialog.FileName;
            SaveSettings();
        }
        FillModels();
    }

    private void ShowSettingsMenu()
    {
        ContextMenuStrip menu = new();
        string server = _settings.FindServer();
        menu.Items.Add(server.Length == 0 ? "llama-server: not found" : "llama-server: " + server).Enabled = false;
        menu.Items.Add("Locate llama-server…", null, (_, _) =>
        {
            using OpenFileDialog dialog = new() { Filter = "llama-server|llama-server.exe", Title = "Locate llama-server.exe" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { _settings.ServerPath = dialog.FileName; SaveSettings(); }
        });
        ToolStripMenuItem gpu = new("Use the graphics card") { Checked = _settings.UseGpu, CheckOnClick = true };
        gpu.CheckedChanged += (_, _) => { _settings.UseGpu = gpu.Checked; SaveSettings(); };
        menu.Items.Add(gpu);
        ToolStripMenuItem context = new("Context length");
        foreach (int length in new[] { 4096, 8192, 16384, 32768 })
        {
            ToolStripMenuItem item = new($"{length / 1024}K tokens") { Checked = _settings.ContextLength == length };
            item.Click += (_, _) => { _settings.ContextLength = length; SaveSettings(); };
            context.DropDownItems.Add(item);
        }
        menu.Items.Add(context);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Changes apply the next time the model starts.").Enabled = false;
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_gear, new Point(0, _gear.Height));
    }

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Say("Assistant", "Settings could not be saved: " + error.Message); }
    }

    private async Task ToggleServerAsync()
    {
        if (LocalModelServer.Shared.IsRunning)
        {
            LocalModelServer.Shared.Stop();
            return;
        }
        _start.Enabled = false;
        Progress<string> progress = new(text => _status.Text = text);
        try
        {
            await LocalModelServer.Shared.StartAsync(_settings, progress);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException
            or System.ComponentModel.Win32Exception or HttpRequestException or TaskCanceledException)
        {
            Say("Assistant", "The model could not start: " + error.Message);
        }
        _start.Enabled = true;
        ShowServerState();
    }

    private void OnServerChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(ShowServerState);
    }

    private void ShowServerState()
    {
        bool running = LocalModelServer.Shared.IsRunning;
        _start.Text = running ? "Stop" : "Start";
        _start.Glyph = running ? "■" : "▶";
        _start.Accent = !running;
        _start.Invalidate();
        _status.Text = running ? "Running " + LocalModelServer.Shared.ModelName : "Not running";
        _status.ForeColor = running ? ThemeService.Palette.Success : ThemeService.Palette.TextMuted;
        _send.Enabled = running && !_busy;
    }

    // ── Conversation ───────────────────────────────────────────────────────────────────────

    private void Attach(string filter, Func<string, string> describe)
    {
        using OpenFileDialog dialog = new() { Filter = filter, InitialDirectory = _project.AssetsPath, Title = "Attach for the assistant" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _attachments.Add((Path.GetFileName(dialog.FileName), describe(dialog.FileName)));
            _attached.Text = "Attached: " + string.Join(", ", _attachments.Select(item => item.Name));
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException or OutOfMemoryException)
        {
            Say("Assistant", "That could not be read: " + error.Message);
        }
    }

    private void ClearConversation()
    {
        _history.Clear();
        _attachments.Clear();
        _attached.Text = string.Empty;
        _transcript.Clear();
        _copy.Visible = _save.Visible = false;
    }

    private async Task SendAsync()
    {
        string question = _input.Text.Trim();
        if (question.Length == 0 || _busy || !LocalModelServer.Shared.IsRunning) return;
        _busy = true;
        _send.Enabled = false;
        _input.Clear();
        Say("You", question);
        try
        {
            if (_game.Checked) await MakeGameAsync(question);
            else if (_write.Checked) await WriteCodeAsync(question);
            else await AskAsync(question);
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or TaskCanceledException
            or System.Text.Json.JsonException)
        {
            Say("Assistant", "Something went wrong: " + error.Message);
        }
        _busy = false;
        ShowServerState();
    }

    /// <summary>The system message: the primer, the commands that match the request, and what is attached.</summary>
    private string Context(string question)
    {
        StringBuilder system = new(AssistantKnowledge.Primer);
        string reference = AssistantKnowledge.Reference(question + " " + string.Join(' ', _attachments.Select(item => item.Text)));
        if (reference.Length > 0) system.AppendLine().AppendLine("PGSL commands that may help:").Append(reference);
        foreach ((string name, string text) in _attachments) system.AppendLine().AppendLine("Attached " + name + ":").AppendLine(text);
        return system.ToString();
    }

    private List<(string Role, string Content)> Messages(string question, string system)
    {
        List<(string, string)> messages = [("system", system)];
        // A small model's context is short: the last few turns only.
        messages.AddRange(_history.TakeLast(6));
        messages.Add(("user", question));
        return messages;
    }

    private const int MaxSteps = 6;

    private const string ToolGuide = """
        You can act in Genesis Studio, one step at a time. Reply with JSON {"action", "target", "text"}:
        - "open": open a resource in its editor. target = the resource's name as the user means it (for example "Main Room"); Genesis picks the closest name. text = "".
        - "look": take a picture and have it described. target = "editor" (the open editor), "window" (all of Studio), "image:NAME" (a sprite or image resource), or "region:x,y,width,height" (pixels inside Studio's window). text = what to look for, or "".
        - "answer": reply to the user. target = "", text = the answer.
        After "open" or "look" you are told the result; then choose the next step.
        When the user asks about a room, sprite, object or what is on screen, open it if needed, look at it, then answer from what you were told.
        """;

    private static readonly JsonObject StepSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("answer", "open", "look") },
            ["target"] = new JsonObject { ["type"] = "string" },
            ["text"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("action", "target", "text"),
        ["additionalProperties"] = false,
    };

    /// <summary>
    /// A question answered in steps: the model may open a resource or look at part of Studio first.
    /// Looking gives a vision model the picture, and every model Genesis Nova OCR's text and a
    /// description of the picture, so models that cannot see still know what is there.
    /// </summary>
    private async Task AskAsync(string question)
    {
        string names = string.Join(", ", _resourceNames().Take(150));
        string system = Context(question) + "\n" + ToolGuide + (names.Length > 0 ? "\nResources in this project: " + names : string.Empty);
        List<(string Role, string Content)> messages = Messages(question, system);
        byte[]? picture = null;
        for (int step = 1; step <= MaxSteps; step++)
        {
            _status.Text = step == 1 ? "Thinking…" : $"Step {step}…";
            string reply = await LocalModelServer.Shared.ChatAsync(messages, StepSchema, 1024, image: picture);
            picture = null;
            JsonNode? act = JsonNode.Parse(reply);
            string action = act?["action"]?.GetValue<string>() ?? "answer";
            string target = act?["target"]?.GetValue<string>() ?? string.Empty;
            string text = act?["text"]?.GetValue<string>() ?? string.Empty;
            if (action == "answer")
            {
                _history.Add(("user", question));
                _history.Add(("assistant", text));
                Say("Assistant", text);
                return;
            }
            messages.Add(("assistant", reply));
            string observation;
            if (action == "open")
            {
                observation = _openResource(target);
                Say("Assistant", "▸ " + observation);
                // Let the editor open and paint before anything looks at it.
                await Task.Delay(600);
            }
            else
            {
                (observation, picture) = await LookAsync(target, text);
                Say("Assistant", "▸ Looked at " + (target.Length == 0 ? "the editor" : target) + ".");
            }
            messages.Add(("user", "Result: " + observation));
        }
        Say("Assistant", $"I could not finish in {MaxSteps} steps; ask again more specifically.");
    }

    /// <summary>Takes the picture the model asked for and describes it: OCR text, colours and size (and the picture itself for a vision model).</summary>
    private async Task<(string Observation, byte[]? Picture)> LookAsync(string target, string query)
    {
        Bitmap? shot = null;
        string what = target.Trim();
        Form? window = FindForm();
        try
        {
            if (what.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
            {
                string? path = _resourcePath(what["image:".Length..].Trim());
                shot = path is null ? null : StudioCapture.Image(path);
                if (shot is null) return ($"There is no image resource named like \"{what[6..]}\".", null);
            }
            else if (what.StartsWith("region:", StringComparison.OrdinalIgnoreCase) && window is not null)
            {
                int[] numbers = [.. what[7..].Split(',').Select(part => int.TryParse(part.Trim(), out int value) ? value : 0)];
                if (numbers.Length < 4 || numbers[2] <= 0 || numbers[3] <= 0) return ("A region needs x,y,width,height in pixels.", null);
                shot = StudioCapture.Region(window, new Rectangle(numbers[0], numbers[1], numbers[2], numbers[3]));
            }
            else if (what.Equals("window", StringComparison.OrdinalIgnoreCase) && window is not null)
                shot = StudioCapture.Window(window);
            else
            {
                Control? editor = _activeEditor();
                shot = editor is not null && editor.Visible ? StudioCapture.Control(editor) : window is null ? null : StudioCapture.Window(window);
            }
            if (shot is null) return ("There was nothing to look at.", null);
            OcrResult text = await GenesisOcr.ReadAsync(shot);
            StringBuilder observation = new($"A picture of {(what.Length == 0 ? "the editor" : what)}, {shot.Width} x {shot.Height} pixels.\n");
            observation.AppendLine(AssistantKnowledge.DescribeBitmap(shot));
            observation.Append(text.Describe(query));
            byte[]? picture = null;
            if (LocalModelServer.Shared.HasVision)
            {
                using MemoryStream png = new();
                shot.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                picture = png.ToArray();
                observation.Insert(0, "The picture is attached. ");
            }
            return (observation.ToString(), picture);
        }
        finally { shot?.Dispose(); }
    }

    private static readonly JsonObject CodeSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["code"] = new JsonObject { ["type"] = "string" },
            ["notes"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("code", "notes"),
        ["additionalProperties"] = false,
    };

    private async Task WriteCodeAsync(string request)
    {
        string system = Context(request) + "\nCore commands:\n" + AssistantKnowledge.EssentialReference() + "\nReply with JSON: \"code\" is the complete PGSL, \"notes\" says in one or two sentences where it goes (which event).";
        List<(string Role, string Content)> messages = Messages(request, system);
        string code = string.Empty, notes = string.Empty;
        IReadOnlyList<string> errors = [];
        for (int round = 1; round <= CheckRounds; round++)
        {
            _status.Text = round == 1 ? "Writing…" : $"Fixing what the checker found ({round} of {CheckRounds})…";
            string reply = await LocalModelServer.Shared.ChatAsync(messages, CodeSchema, 2048);
            JsonNode? answer = JsonNode.Parse(reply);
            code = answer?["code"]?.GetValue<string>() ?? string.Empty;
            notes = answer?["notes"]?.GetValue<string>() ?? string.Empty;
            errors = AssistantKnowledge.Check(code, _project.RootPath, allowUndeclared: true);
            if (errors.Count == 0) break;
            messages.Add(("assistant", reply));
            messages.Add(("user", "The PGSL checker found these errors:\n- " + string.Join("\n- ", errors.Take(8))
                + "\nFix them and return the whole code again."));
        }
        _history.Add(("user", request));
        _history.Add(("assistant", code));
        _lastCode = code;
        Say("Assistant", notes);
        SayCode(code);
        Say("Checker", errors.Count == 0
            ? "✓ Passes the PGSL check."
            : $"Still {errors.Count} problem(s) after {CheckRounds} tries:\n- " + string.Join("\n- ", errors.Take(6)));
        _copy.Visible = _save.Visible = code.Length > 0;
    }

    /// <summary>The model designs a small 2D game; Genesis checks it and writes its sprites, objects and room.</summary>
    private async Task MakeGameAsync(string request)
    {
        Progress<string> progress = new(text => _status.Text = text);
        GameBuilder.Result result = await GameBuilder.BuildAsync(_project, request, progress);
        Say("Assistant", GameBuilder.Describe(result.Json));
        Say("Checker", result.Success
            ? $"✓ Checked and made in {result.RoundsUsed} round(s):\n- " + string.Join("\n- ", result.Written) + "\nPress Run to play it."
            : $"Not made: {result.Problems.Count} problem(s) left after {result.RoundsUsed} tries:\n- " + string.Join("\n- ", result.Problems.Take(8)));
    }

    private void SaveScript()
    {
        if (_lastCode.Length == 0) return;
        using NamePromptDialog dialog = new("Save as Script", "Script name", "Assistant Script");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ResourceService resources = new(_project);
            string folder = ResourceFolderPolicy.RootFor(_project, ResourceKind.PgslScript);
            string path = resources.CreateResource(folder, ResourceKind.PgslScript, dialog.Value);
            File.WriteAllText(path, _lastCode);
            Say("Assistant", "Saved as " + Path.GetFileName(path) + " in the Scripts folder.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Say("Assistant", "The script could not be saved: " + error.Message);
        }
    }

    // ── Transcript ─────────────────────────────────────────────────────────────────────────

    private void Say(string who, string text)
    {
        ThemePalette palette = ThemeService.Palette;
        _transcript.SelectionStart = _transcript.TextLength;
        _transcript.SelectionFont = new Font(ThemeService.InterfaceFont, FontStyle.Bold);
        _transcript.SelectionColor = who == "You" ? palette.Accent : who == "Checker" ? palette.Success : palette.Text;
        _transcript.AppendText(who + Environment.NewLine);
        _transcript.SelectionFont = ThemeService.InterfaceFont;
        _transcript.SelectionColor = palette.Text;
        _transcript.AppendText(text.Trim() + Environment.NewLine + Environment.NewLine);
        _transcript.ScrollToCaret();
    }

    private void SayCode(string code)
    {
        _transcript.SelectionStart = _transcript.TextLength;
        _transcript.SelectionFont = ThemeService.CodeFont;
        _transcript.SelectionColor = ThemeService.Palette.Text;
        _transcript.AppendText(code.TrimEnd() + Environment.NewLine + Environment.NewLine);
        _transcript.ScrollToCaret();
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "\n…";
}
