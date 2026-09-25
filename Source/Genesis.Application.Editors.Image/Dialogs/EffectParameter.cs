namespace Genesis.Application.Editors.Image.Dialogs;

public enum EffectParamType
{
    Slider,
    Colour,
    Numeric,
}

public sealed class EffectParameter
{
    public EffectParameter(
        string name,
        string displayName,
        EffectParamType type,
        object defaultValue,
        double min = 0,
        double max = 100)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        Type = type;
        Default = defaultValue ?? throw new ArgumentNullException(nameof(defaultValue));
        Min = min;
        Max = max;
    }

    public string Name { get; }
    public string DisplayName { get; }
    public EffectParamType Type { get; }
    public object Default { get; }
    public double Min { get; }
    public double Max { get; }
}

public sealed class ImageEffectDefinition
{
    public ImageEffectDefinition(
        string title,
        IReadOnlyList<EffectParameter> parameters,
        Func<byte[], int, int, IReadOnlyDictionary<string, object>, byte[]> apply)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Parameters = parameters ?? Array.Empty<EffectParameter>();
        Apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public string Title { get; }
    public IReadOnlyList<EffectParameter> Parameters { get; }
    public Func<byte[], int, int, IReadOnlyDictionary<string, object>, byte[]> Apply { get; }
}
