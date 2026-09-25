using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Inspector;

/// <summary>Builds declarative dialogs from the same drawer registry used by Studio's Inspector.</summary>
public static class InspectorBuilder
{
    public static TableLayoutPanel BuildForObject(
        object target,
        string note = "",
        string? projectRoot = null,
        IWin32Window? dialogOwner = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        PropertyInfo[] properties = target.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead
                               && property.CanWrite
                               && property.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .OrderBy(property => property.GetCustomAttribute<InspectorGroupAttribute>()?.Order ?? 0)
            .ThenBy(property => property.MetadataToken)
            .ToArray();

        Dictionary<PropertyInfo, object?> original = properties.ToDictionary(
            property => property,
            property => property.GetValue(target));
        List<BuilderRow> rows = [];
        string? previousGroup = null;
        foreach (PropertyInfo property in properties)
        {
            InspectorGroupAttribute? groupAttribute = property.GetCustomAttribute<InspectorGroupAttribute>();
            string group = groupAttribute?.Name ?? KnownGroup(target.GetType(), property.Name);
            if (!string.Equals(group, previousGroup, StringComparison.Ordinal))
            {
                rows.Add(BuilderRow.Group(group));
                previousGroup = group;
            }

            InspectorRangeAttribute? rangeAttribute = property.GetCustomAttribute<InspectorRangeAttribute>();
            DrawerRange? knownRange = rangeAttribute is null ? KnownRange(target.GetType(), property.Name) : null;
            InspectorAssetAttribute? asset = property.GetCustomAttribute<InspectorAssetAttribute>();
            bool readOnly = property.GetCustomAttribute<ReadOnlyAttribute>()?.IsReadOnly == true;
            string label = property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                           ?? Humanize(property.Name);
            string? description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
            object? initial = original[property];
            Control drawer = PropertyDrawerRegistry.CreateControl(new PropertyDrawerContext(
                property.Name,
                Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType,
                initial,
                newValue => property.SetValue(target, ConvertForProperty(newValue, property.PropertyType)),
                Minimum: rangeAttribute?.Minimum ?? knownRange?.Minimum,
                Maximum: rangeAttribute?.Maximum ?? knownRange?.Maximum,
                Increment: rangeAttribute?.Increment ?? knownRange?.Increment,
                DecimalPlaces: rangeAttribute?.DecimalPlaces ?? knownRange?.DecimalPlaces,
                ReadOnly: readOnly,
                AssetKind: asset?.Kind,
                ProjectRoot: projectRoot,
                DialogOwner: dialogOwner,
                ControlName: "InspectorDrawer_" + property.Name,
                Description: description));
            StyleDrawer(drawer);
            rows.Add(BuilderRow.Property(label, drawer, description));
        }

        TableLayoutPanel form = new()
        {
            AutoScroll = true,
            BackColor = EditorChrome.Surface,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = rows.Count + 2,
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int rowIndex = 0;
        foreach (BuilderRow row in rows)
        {
            if (row.IsGroup)
            {
                form.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                Label heading = new()
                {
                    AutoEllipsis = true,
                    Dock = DockStyle.Fill,
                    Font = new Font(EditorChrome.BaseFont, FontStyle.Bold),
                    ForeColor = EditorChrome.Text,
                    Padding = new Padding(0, rowIndex == 0 ? 4 : 12, 0, 4),
                    Text = row.Label.ToUpperInvariant(),
                    TextAlign = ContentAlignment.MiddleLeft,
                };
                form.Controls.Add(heading, 0, rowIndex);
                form.SetColumnSpan(heading, 2);
            }
            else
            {
                form.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                Label label = new()
                {
                    AutoEllipsis = true,
                    Dock = DockStyle.Fill,
                    ForeColor = EditorChrome.Text,
                    Margin = new Padding(0, 4, 10, 4),
                    Text = row.Label,
                    TextAlign = ContentAlignment.MiddleLeft,
                };
                if (!string.IsNullOrWhiteSpace(row.Description))
                {
                    label.AccessibleDescription = row.Description;
                }
                row.Editor!.Dock = DockStyle.Fill;
                row.Editor.Margin = new Padding(0, 4, 0, 4);
                form.Controls.Add(label, 0, rowIndex);
                form.Controls.Add(row.Editor, 1, rowIndex);
            }
            rowIndex++;
        }

        Label hint = new()
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = EditorChrome.Muted,
            MaximumSize = new Size(620, 0),
            Padding = new Padding(0, 12, 0, 10),
            Text = note,
        };
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(hint, 0, rowIndex);
        form.SetColumnSpan(hint, 2);
        rowIndex++;

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            WrapContents = false,
        };
        Button apply = new() { Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        cancel.Click += (_, _) =>
        {
            foreach ((PropertyInfo property, object? value) in original) property.SetValue(target, value);
        };
        EditorChrome.StyleField(apply);
        EditorChrome.StyleField(cancel);
        buttons.Controls.Add(apply);
        buttons.Controls.Add(cancel);
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        form.Controls.Add(buttons, 0, rowIndex);
        form.SetColumnSpan(buttons, 2);
        return form;
    }

    private static object? ConvertForProperty(object? value, Type propertyType)
    {
        Type targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (value is null) return Nullable.GetUnderlyingType(propertyType) is not null ? null : Activator.CreateInstance(targetType);
        if (targetType.IsInstanceOfType(value)) return value;
        if (targetType.IsEnum) return Enum.Parse(targetType, value.ToString() ?? string.Empty, ignoreCase: true);
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static void StyleDrawer(Control root)
    {
        if (root is not TableLayoutPanel and not Panel) EditorChrome.StyleField(root);
        foreach (Control child in root.Controls) StyleDrawer(child);
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Value";
        return System.Text.RegularExpressions.Regex
            .Replace(value, "([a-z0-9])([A-Z])", "$1 $2")
            .Replace('_', ' ');
    }

    private static string KnownGroup(Type owner, string property)
    {
        if (owner.Name == "FoliageScatterSettings")
        {
            return property switch
            {
                "NearDistance" or "FarDistance" or "StreamingCellSize" => "Streaming & LOD",
                "VisibleInstanceBudget" or "TriangleBudget" or "ResidentMemoryBudgetMegabytes"
                    or "GpuUploadBudgetMegabytes" or "TargetGpuMilliseconds" => "Performance budgets",
                _ => "Distribution",
            };
        }
        if (owner.Name == "TerrainPathSettings")
        {
            return property is "Width" or "GradeStrength" or "SplatChannel"
                ? "Path surface"
                : "Generation";
        }
        return "Settings";
    }

    private static DrawerRange? KnownRange(Type owner, string property)
    {
        if (owner.Name == "TerrainPathSettings")
        {
            return property switch
            {
                "PathCount" => new DrawerRange(1m, 8m, 1m, 0),
                "Width" => new DrawerRange(0.25m, 64m, 0.25m, 2),
                "GradeStrength" => new DrawerRange(0m, 1m, 0.01m, 2),
                "SplatChannel" => new DrawerRange(0m, 3m, 1m, 0),
                _ => null,
            };
        }
        if (owner.Name == "FoliageScatterSettings")
        {
            return property switch
            {
                "MaximumInstances" => new DrawerRange(0m, 250000m, 100m, 0),
                "Density" => new DrawerRange(0m, 1m, 0.01m, 2),
                "MinimumSpacing" => new DrawerRange(0.15m, 32m, 0.05m, 2),
                "PathExclusion" => new DrawerRange(0m, 8m, 0.05m, 2),
                "MaximumSlopeDegrees" => new DrawerRange(0m, 89m, 1m, 1),
                "NearDistance" => new DrawerRange(1m, 10000m, 1m, 1),
                "FarDistance" => new DrawerRange(1m, 20000m, 1m, 1),
                "StreamingCellSize" => new DrawerRange(4m, 512m, 1m, 1),
                "VisibleInstanceBudget" => new DrawerRange(128m, 32768m, 128m, 0),
                "TriangleBudget" => new DrawerRange(1000m, 50000000m, 1000m, 0),
                "ResidentMemoryBudgetMegabytes" => new DrawerRange(0.25m, 1024m, 0.25m, 2),
                "GpuUploadBudgetMegabytes" => new DrawerRange(0.25m, 64m, 0.25m, 2),
                "TargetGpuMilliseconds" => new DrawerRange(0.25m, 33.3m, 0.05m, 2),
                _ => null,
            };
        }
        return null;
    }

    private sealed record BuilderRow(string Label, Control? Editor, string? Description, bool IsGroup)
    {
        public static BuilderRow Group(string label) => new(label, null, null, true);
        public static BuilderRow Property(string label, Control editor, string? description) =>
            new(label, editor, description, false);
    }

    private sealed record DrawerRange(
        decimal Minimum,
        decimal Maximum,
        decimal Increment,
        int DecimalPlaces);
}
