using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Studio.Docking;

namespace Genesis.Application.Studio.Forms;

public sealed partial class StudioShellForm
{
    private AssistantDock? _assistantDock;

    /// <summary>Opens the AI assistant, made on first use so a project that never asks for it pays nothing.</summary>
    private void ShowAssistant()
    {
        if (_assistantDock is null || _assistantDock.IsDisposed)
            _assistantDock = new AssistantDock(_project, OpenResourceNamed, () => _dockPanel.ActiveDocument as Control,
                AssistantResourceNames, name => FindResourceNamed(name)?.FullPath);
        ShowToolWindow(_assistantDock);
    }

    private List<ResourceItem> AllResources() =>
        [.. _resources.BuildTree().Children.SelectMany(Flatten).Where(item => !item.IsFolder)];

    private IReadOnlyList<string> AssistantResourceNames() =>
        [.. AllResources().OrderBy(item => item.Kind).ThenBy(item => item.Name).Select(item => $"{item.Name} ({item.Kind})")];

    /// <summary>
    /// The resource the user most likely means by <paramref name="said"/>: the same name, else a name
    /// containing it (or it containing the name), else the most shared words; a kind word in what was
    /// said ("room", "sprite", "object", "script") breaks ties.
    /// </summary>
    private ResourceItem? FindResourceNamed(string said)
    {
        string wanted = said.Trim();
        if (wanted.Length == 0) return null;
        string[] words = wanted.ToLowerInvariant().Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        ResourceItem? best = null;
        int bestScore = 0;
        foreach (ResourceItem item in AllResources())
        {
            string name = item.Name.ToLowerInvariant();
            int score = string.Equals(item.Name, wanted, StringComparison.OrdinalIgnoreCase) ? 100
                : name.Contains(wanted.ToLowerInvariant(), StringComparison.Ordinal) || wanted.ToLowerInvariant().Contains(name, StringComparison.Ordinal) ? 60
                : words.Count(word => word.Length > 2 && name.Contains(word, StringComparison.Ordinal)) * 15;
            if (score == 0) continue;
            string kind = item.Kind.ToString().ToLowerInvariant();
            if (words.Any(word => kind.Contains(word, StringComparison.Ordinal) || (word == "sprite" && item.Kind == ResourceKind.Image)
                || (word == "script" && item.Kind == ResourceKind.PgslScript) || (word == "object" && item.Kind == ResourceKind.GameObject)))
                score += 10;
            if (score > bestScore) { best = item; bestScore = score; }
        }
        return best;
    }

    private string OpenResourceNamed(string said)
    {
        ResourceItem? item = FindResourceNamed(said);
        if (item is null) return $"No resource is named like \"{said}\".";
        OpenResource(item);
        return $"Opened {item.Kind} \"{item.Name}\" in its editor.";
    }
}
