using System.Windows.Forms;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Inspector;

/// <summary>
/// Everything a drawer needs to render one value. The same contract is consumed by the Studio
/// Inspector, reflective dialogs and (later) visual action cards, so range/asset semantics do not
/// get reimplemented by each surface.
/// </summary>
public sealed record PropertyDrawerContext(
    string PropertyName,
    Type PropertyType,
    object? InitialValue,
    Action<object?> ValueChanged,
    IReadOnlyList<string>? Choices = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    decimal? Increment = null,
    int? DecimalPlaces = null,
    bool ReadOnly = false,
    ResourceKind? AssetKind = null,
    string? ProjectRoot = null,
    IWin32Window? DialogOwner = null,
    string? ControlName = null,
    string? Description = null);

/// <summary>A unified property editor used everywhere Genesis exposes authored values.</summary>
public interface IPropertyDrawer
{
    bool CanDraw(PropertyDrawerContext context);

    Control CreateDrawer(PropertyDrawerContext context);
}

/// <summary>Constrains a numeric property and supplies its preferred editor step.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class InspectorRangeAttribute(
    double minimum,
    double maximum,
    double increment = 0.05,
    int decimalPlaces = 2) : Attribute
{
    public decimal Minimum { get; } = Convert.ToDecimal(minimum);
    public decimal Maximum { get; } = Convert.ToDecimal(maximum);
    public decimal Increment { get; } = Convert.ToDecimal(increment);
    public int DecimalPlaces { get; } = Math.Clamp(decimalPlaces, 0, 8);
}

/// <summary>Marks a string property as a project-relative resource reference.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class InspectorAssetAttribute(ResourceKind kind) : Attribute
{
    public ResourceKind Kind { get; } = kind;
}

/// <summary>Starts a named logical section in a reflective Inspector surface.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class InspectorGroupAttribute(string name, int order = 0) : Attribute
{
    public string Name { get; } = name;
    public int Order { get; } = order;
}
