using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Terrain Condition preview: evaluate a PGSL If expression with a variable bag, and read
/// AnimationPlay (and related) clips out of Object visual-action Then/Else source.
/// </summary>
public static partial class TerrainVisualCondition
{
    private static readonly HashSet<string> AnimationCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "AnimationPlay",
        "PlayAnimation",
        "SpritePlayAnimation",
        "SpriteSetAnimation",
    };

    public static string PlayAnimationSource(string clip, bool loop = true)
    {
        string tag = QuoteClip(clip);
        VisualActionTemplate template = new(
            "Play animation",
            "AnimationPlay",
            "Animation",
            "Start a named animation clip",
            [
                new VisualActionParameter("tag", tag),
                new VisualActionParameter("loop", loop ? "true" : "false", VisualActionValueKind.Boolean),
            ]);
        return VisualActionSyntax.CreateBlock(template);
    }

    public static string ClipFromActions(string source, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return fallback ?? "";
        }

        foreach (VisualActionBlock block in VisualActionSyntax.Parse(source))
        {
            if (!IsAnimationCommand(block.CommandName))
            {
                continue;
            }

            string clip = block.Parameters.Count > 0
                ? Unquote(block.Parameters[0].Value)
                : Unquote(block.Body);
            if (!string.IsNullOrWhiteSpace(clip))
            {
                return clip;
            }
        }

        Match match = AnimationCallPattern().Match(source);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return fallback ?? "";
    }

    public static bool EvaluateIf(string expression, IReadOnlyDictionary<string, double>? variables = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        string trimmed = expression.Trim();
        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            VMEngine.Initialize();
            StringBuilder source = new();
            if (variables is not null)
            {
                foreach ((string name, double value) in variables)
                {
                    if (!IsIdentifier(name))
                    {
                        continue;
                    }

                    source.Append(name)
                        .Append(" = ")
                        .Append(value.ToString("G17", CultureInfo.InvariantCulture))
                        .AppendLine(";");
                }
            }

            source.Append("__terrain_if = (").Append(trimmed).AppendLine(");");
            CompileResult compiled = VMEngine.Compile(source.ToString());
            if (compiled is null)
            {
                return false;
            }

            PgslVm vm = VMEngine.CreateVm();
            vm.Execute(compiled.Instructions, compiled.Constants);
            if (!vm.GetVariables().TryGetValue("__terrain_if", out object? result))
            {
                return false;
            }

            return IsTruthy(result);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsAnimationCommand(string command) =>
        !string.IsNullOrWhiteSpace(command)
        && (AnimationCommands.Contains(command)
            || AnimationCommands.Contains(command.Contains('.') ? command[(command.LastIndexOf('.') + 1)..] : command));

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => !string.IsNullOrWhiteSpace(text)
            && !text.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !text.Equals("0", StringComparison.OrdinalIgnoreCase),
        IConvertible convertible => Convert.ToDouble(convertible, CultureInfo.InvariantCulture) != 0d,
        _ => true,
    };

    private static bool IsIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !char.IsLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        for (int i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string QuoteClip(string clip)
    {
        string value = (clip ?? "").Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string Unquote(string value)
    {
        string text = (value ?? "").Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            return text[1..^1].Replace("\\\"", "\"");
        }

        return text;
    }

    [GeneratedRegex(
        @"(?:AnimationPlay|PlayAnimation|SpritePlayAnimation|SpriteSetAnimation)\s*\(\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnimationCallPattern();
}
