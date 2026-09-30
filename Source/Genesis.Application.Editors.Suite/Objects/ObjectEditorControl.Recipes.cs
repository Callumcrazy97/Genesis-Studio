using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Objects;

public sealed partial class ObjectEditorControl
{
    private Panel? _recipePage;
    private StarterGallery? _recipeGallery;

    /// <summary>The behaviour recipe cards (built on first use).</summary>
    public StarterGallery? RecipeGallery => _recipeGallery;

    public bool IsRecipePageVisible => _recipePage?.Visible == true;

    /// <summary>
    /// Shows the recipe cards over the Builder: "Move with arrow keys", "Platformer controls",
    /// "Collectible"… A new Object starts here instead of at an empty event list.
    /// </summary>
    public void ShowBehaviourRecipes()
    {
        EnsureRecipePage();
        _recipeGallery!.SetItems(ObjectBehaviourRecipes.For(IsThreeD).Select(recipe =>
            new StarterItem(recipe.Id, recipe.Title, recipe.Description)
            {
                Category = recipe.Category,
                Glyph = recipe.Glyph,
                Swatch = UiTokens.FromHex(recipe.Swatch),
                Badge = HasRecipe(recipe) ? "✓ Added" : string.Empty,
                BadgeTone = StarterBadgeTone.Success,
            }));
        if (WorkspaceMode == ObjectWorkspaceMode.Code) SetWorkspaceMode(ObjectWorkspaceMode.Graph);
        _showObjectGameGuide = false;
        RefreshObjectWorkflow();
        _recipePage!.Visible = true;
        _recipePage.BringToFront();
        _objectWorkflow?.SetCurrent("Behaviour");
    }

    /// <summary>The Behaviour step: recipes for an Object with no code yet, otherwise the Builder.</summary>
    private void ShowBehaviourStep()
    {
        if (_events.Values.All(string.IsNullOrWhiteSpace)) ShowBehaviourRecipes();
        else ShowVisualActions();
    }

    private void HideBehaviourRecipes()
    {
        if (_recipePage is not null) _recipePage.Visible = false;
    }

    /// <summary>
    /// Appends a recipe's code to its events (never replacing what is there). Returns the events
    /// that changed; a recipe already present in an event is not added again.
    /// </summary>
    public IReadOnlyList<string> ApplyBehaviourRecipe(string id)
    {
        ObjectBehaviourRecipe recipe = ObjectBehaviourRecipes.Find(id)
            ?? throw new ArgumentException($"'{id}' is not a behaviour recipe.", nameof(id));
        List<string> changed = [];
        foreach ((string eventId, string block) in recipe.Events)
        {
            string existing = _events.GetValueOrDefault(eventId) ?? string.Empty;
            if (existing.Contains(recipe.Marker, StringComparison.Ordinal)) continue;
            SetEventBody(eventId, ObjectBehaviourRecipes.Append(existing, block));
            changed.Add(eventId);
        }

        if (changed.Count == 0)
        {
            UpdateStatus($"'{recipe.Title}' is already part of this Object.");
            return changed;
        }

        HideBehaviourRecipes();
        string focus = changed.Contains("Step") ? "Step" : changed[0];
        SelectEvent(focus);
        ShowVisualActions();
        UpdateStatus($"Added '{recipe.Title}' to {string.Join(" and ", changed)}. It is ordinary code: change the numbers to tune it.");
        return changed;
    }

    private bool HasRecipe(ObjectBehaviourRecipe recipe) =>
        recipe.Events.Keys.Any(eventId => (_events.GetValueOrDefault(eventId) ?? string.Empty).Contains(recipe.Marker, StringComparison.Ordinal));

    private void EnsureRecipePage()
    {
        if (_recipePage is not null) return;
        Control host = _authoringSplit.Parent ?? _objectAuthoringPanel!;
        _recipePage = new Panel { Name = "ObjectBehaviourRecipes", Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Visible = false };
        _recipeGallery = new StarterGallery("ObjectRecipeGallery")
        {
            Dock = DockStyle.Fill,
            Heading = "Start with a behaviour",
            Subheading = "Each card adds readable code to this Object's events. Change it afterwards in the Builder or Code.",
        };
        _recipeGallery.ItemChosen += (_, item) => ApplyBehaviourRecipe(item.Id);

        FlowLayoutPanel footer = new()
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8, 6, 8, 8),
            BackColor = EditorChrome.Surface,
        };
        Button back = new() { Name = "ObjectRecipesBack", AutoSize = true, Text = "Write my own events in the Builder" };
        EditorChrome.StyleField(back);
        back.Click += (_, _) => { HideBehaviourRecipes(); ShowVisualActions(); };
        Button addEvent = new() { Name = "ObjectRecipesAddEvent", AutoSize = true, Text = "Add an event…" };
        EditorChrome.StyleField(addEvent);
        addEvent.Click += (_, _) => { HideBehaviourRecipes(); AddEventViaWizard(); };
        footer.Controls.Add(back);
        footer.Controls.Add(addEvent);

        _recipePage.Controls.Add(_recipeGallery);
        _recipePage.Controls.Add(footer);
        host.Controls.Add(_recipePage);
    }
}
