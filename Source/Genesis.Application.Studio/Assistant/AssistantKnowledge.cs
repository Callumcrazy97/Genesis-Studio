using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Studio.Assistant;

/// <summary>
/// What the assistant is told: a short PGSL primer, and for each question the few commands that
/// match it best (plain keyword scoring over every command's name, signature and description), so a
/// small model's context holds what matters and not the whole reference.
/// </summary>
public static partial class AssistantKnowledge
{
    public const string Primer = """
        You are the Genesis Studio assistant. You help make games in Genesis with PGSL, its scripting language.
        PGSL rules:
        - C-like syntax: if / else if / else, while, for (init; condition; step), repeat (n), with (target), function Name(a, b) { return value; }.
        - No switch, no do-until. ++ and -- only as a statement on their own; in a for step write i += 1.
        - In an event, a plain assignment or var sets the instance's variable. Inside a function, var is local; write self.name to set the instance's variable.
        - Lists: var a = [1, 2, 3]; a[0]; PgListSize(a); PgListAdd(a, v). Lists count from 0; strings count from 1.
        - Text joins with +; a number joined to text becomes text.
        - x, y, room_width and room_height are built in: assign x and y (x = 10;), never declare them with var.
        - To move something, give it your own speed variables in Create (moveX = 3; moveY = -2;) and add them in Step (x += moveX;). Do not use hspeed or vspeed.
        - Random(n) gives a number from 0 to n (one argument). For a range write low + Random(high - low).
        - Keys and buttons are names in quotes, never constants: KeyCheck("Right"), KeyPressed("Space"), KeyCheck("W"), GamepadPressed("A").
        - Sin, Cos and PointDirection use degrees. Events of an object: Create, Step, Draw, DrawGui (and others).
        - Use only commands listed in the reference you are given; do not invent commands.
        Answer briefly and plainly.
        """;

    private sealed record Entry(string Name, string Signature, string Description, string Category, string[] Words);

    private static readonly Lazy<List<Entry>> Index = new(() =>
    {
        List<Entry> entries = [];
        IReadOnlyList<PgslCommandInfo> catalog = PgslCommandRegistry.GetFullCatalog();
        if (catalog.Count == 0)
        {
            // Studio may not have run a game yet, so the registry is built here on first use.
            PgslCommandRegistry.Build(typeof(PgslCommands));
            catalog = PgslCommandRegistry.GetFullCatalog();
        }
        foreach (PgslCommandInfo command in catalog)
        {
            string category = command.Category ?? string.Empty;
            string text = $"{SplitWords(command.Name)} {command.Signature} {command.Description} {category}";
            entries.Add(new Entry(command.Name, command.Signature ?? command.Name, command.Description ?? string.Empty, category,
                Words(text).Distinct().ToArray()));
        }
        return entries;
    });

    /// <summary>The <paramref name="count"/> commands that best match <paramref name="question"/>, one per line.</summary>
    public static string Reference(string question, int count = 14)
    {
        string[] asked = Words(question).Where(word => word.Length > 2 && !StopWords.Contains(word)).Distinct().ToArray();
        if (asked.Length == 0) return string.Empty;
        List<Entry> entries = Index.Value;
        Dictionary<string, int> documentFrequency = new();
        foreach (Entry entry in entries)
            foreach (string word in entry.Words) documentFrequency[word] = documentFrequency.GetValueOrDefault(word) + 1;
        var ranked = entries
            .Select(entry => (entry, score: asked.Sum(word =>
                (entry.Words.Contains(word) ? Math.Log(1 + entries.Count / (1.0 + documentFrequency.GetValueOrDefault(word))) : 0)
                + (entry.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ? 1.5 : 0))))
            .Where(item => item.score > 0)
            .OrderByDescending(item => item.score)
            .Take(count);
        StringBuilder text = new();
        foreach (var (entry, _) in ranked) text.Append("- ").Append(entry.Signature).Append(": ").AppendLine(entry.Description);
        return text.ToString();
    }

    /// <summary>Commands a game's code reaches for most; offered with every request to write code.</summary>
    private static readonly string[] Essentials =
        ["KeyCheck", "KeyPressed", "GamepadAxis", "GamepadPressed", "DeltaTime", "Clamp", "InstanceCreate", "InstanceDestroy",
         "PlaySound", "RoomGoto", "DrawSprite", "DrawTextScaled", "DrawSetColorRgb", "DrawRectangle"];

    /// <summary>The essential commands, one per line, for code requests.</summary>
    public static string EssentialReference()
    {
        StringBuilder text = new();
        foreach (Entry entry in Index.Value.Where(entry => Essentials.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)))
            text.Append("- ").Append(entry.Signature).Append(": ").AppendLine(entry.Description);
        return text.ToString();
    }

    /// <summary>
    /// The PGSL checker's errors for <paramref name="code"/>, plus any command that does not exist
    /// (the checker leaves calls alone; a small model invents them). Functions the code defines, and
    /// those in the project's own Scripts, count as known.
    /// </summary>
    public static IReadOnlyList<string> Check(string code, string? projectRoot = null, bool allowUndeclared = false)
    {
        // Strict, as Run (F5) checks: wrong argument counts and shadowed built-ins are errors there.
        PgslValidationReport report = PgslScriptValidator.ValidateSource(code, "assistant", strict: true);
        // A lone snippet may read variables another event sets; those are allowed when asked.
        List<string> errors = [.. report.Errors.Where(error => !allowUndeclared || !error.Contains("Undeclared variable", StringComparison.Ordinal))];
        _ = Index.Value; // builds the registry
        HashSet<string> own = new(FunctionDeclaration().Matches(code).Select(match => match.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
        if (projectRoot is not null && Directory.Exists(projectRoot))
            foreach (string script in Directory.EnumerateFiles(projectRoot, "*.pgsl", SearchOption.AllDirectories).Take(500))
                foreach (Match match in FunctionDeclaration().Matches(File.ReadAllText(script))) own.Add(match.Groups[1].Value);
        foreach (string name in Call().Matches(code).Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Keywords.Contains(name) || own.Contains(name) || PgslCommandRegistry.TryGet(name) is not null) continue;
            errors.Add($"Unknown command: {name}. Use only commands from the reference, or define it with function {name}(...).");
        }
        return errors;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        { "if", "while", "for", "repeat", "with", "function", "return", "switch", "var", "else" };

    [GeneratedRegex(@"\bfunction\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex FunctionDeclaration();

    [GeneratedRegex(@"(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex Call();

    private static IEnumerable<string> Words(string text) =>
        WordPattern().Matches(SplitWords(text)).Select(match => Stem(match.Value.ToLowerInvariant()));

    /// <summary>"keys" finds "key", "moving" finds "move": a light stemmer is enough for matching commands.</summary>
    private static string Stem(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal)) return word[..^3];
        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal)) return word[..^2];
        if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal) && !word.EndsWith("ses", StringComparison.Ordinal)) return word[..^1];
        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)) return word[..^1];
        return word;
    }

    private static string SplitWords(string text) => CamelBoundary().Replace(text ?? string.Empty, " ");

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "how", "can", "make", "want", "when", "what", "into", "from", "have", "use",
        "game", "please", "would", "like", "need", "does", "write", "code", "pgsl", "script", "object",
    };

    [GeneratedRegex("[A-Za-z][A-Za-z0-9]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    // ── Seeing, for models without vision ──────────────────────────────────────────────────

    /// <summary>
    /// A picture described in words a text-only model can use: size, transparent area, main colours,
    /// brightness and whether it looks like a strip of animation frames.
    /// </summary>
    public static string DescribeImage(string path)
    {
        using Bitmap source = new(path);
        int width = source.Width, height = source.Height;
        using Bitmap bitmap = source.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels = new byte[width * height * 4];
        try
        {
            for (int y = 0; y < height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
        }
        finally { bitmap.UnlockBits(data); }

        int transparent = 0, left = width, top = height, right = -1, bottom = -1;
        double brightness = 0;
        Dictionary<int, int> colours = new();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                byte b = pixels[at], g = pixels[at + 1], r = pixels[at + 2], a = pixels[at + 3];
                if (a < 16) { transparent++; continue; }
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                brightness += (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
                colours[key] = colours.GetValueOrDefault(key) + 1;
            }
        int opaque = width * height - transparent;
        StringBuilder text = new();
        text.AppendLine($"Image {Path.GetFileName(path)}: {width} x {height} pixels.");
        if (opaque == 0)
        {
            text.AppendLine("It is fully transparent.");
            return text.ToString();
        }
        text.AppendLine($"Transparent: {100.0 * transparent / (width * height):0}% of pixels. Visible content spans x {left}-{right}, y {top}-{bottom}.");
        text.AppendLine($"Average brightness of visible pixels: {100 * brightness / opaque:0}%.");
        text.AppendLine($"About {colours.Count} distinct colours (grouped in steps of 16). Main colours: "
            + string.Join(", ", colours.OrderByDescending(pair => pair.Value).Take(6).Select(pair =>
                $"#{(pair.Key >> 8 & 15) * 17:X2}{(pair.Key >> 4 & 15) * 17:X2}{(pair.Key & 15) * 17:X2} {100.0 * pair.Value / opaque:0}%")) + ".");
        if (height > 0 && width % height == 0 && width / height is >= 2 and <= 64)
            text.AppendLine($"It may be an animation strip of {width / height} square frames, {height} x {height} each.");
        else if (width > 0 && height % width == 0 && height / width is >= 2 and <= 64)
            text.AppendLine($"It may be a vertical strip of {height / width} square frames, {width} x {width} each.");
        return text.ToString();
    }

    /// <summary>A room in words: its size and dimension, and what is placed in it.</summary>
    public static string DescribeRoom(string path)
    {
        using JsonDocument room = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = room.RootElement;
        StringBuilder text = new();
        string name = root.TryGetProperty("name", out JsonElement nameValue) ? nameValue.GetString() ?? Path.GetFileName(path) : Path.GetFileName(path);
        bool threeD = root.TryGetProperty("dimension", out JsonElement dimension)
            && (dimension.ValueKind == JsonValueKind.Number ? dimension.GetInt32() == 1 : dimension.GetString() == "ThreeD");
        text.Append($"Room {name} ({(threeD ? "3D" : "2D")})");
        if (root.TryGetProperty("settings", out JsonElement settings)
            && settings.TryGetProperty("width", out JsonElement width) && settings.TryGetProperty("height", out JsonElement height))
            text.Append($", {width.GetRawText()} x {height.GetRawText()}");
        text.AppendLine(".");
        if (!root.TryGetProperty("nodes", out JsonElement nodes) || nodes.ValueKind != JsonValueKind.Array) return text.ToString();
        int listed = 0, total = nodes.GetArrayLength();
        foreach (JsonElement node in nodes.EnumerateArray())
        {
            if (listed++ >= 60) break;
            string kind = node.TryGetProperty("kind", out JsonElement k) ? k.GetString() ?? "" : "";
            string label = node.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
            string prefab = node.TryGetProperty("gameObject", out JsonElement go) && go.TryGetProperty("prefab", out JsonElement p)
                ? Path.GetFileName(p.GetString() ?? "").Replace(".object.json", "", StringComparison.OrdinalIgnoreCase) : "";
            string position = node.TryGetProperty("transform", out JsonElement t) && t.TryGetProperty("position", out JsonElement pos)
                ? pos.GetRawText().Replace("\n", "").Replace(" ", "") : "";
            text.AppendLine($"- {kind} {label}" + (prefab.Length > 0 ? $" (object {prefab})" : "") + (position.Length > 0 ? $" at {position}" : ""));
        }
        if (total > 60) text.AppendLine($"…and {total - 60} more.");
        return text.ToString();
    }
}
