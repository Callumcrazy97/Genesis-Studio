using System.Windows.Forms;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// One shared attach path for Suite viewport chrome. Editors add their own tools around this;
/// they should not invent parallel Camera / View / Gizmo / 2D·3D / Target bars.
/// </summary>
/// <remarks>
/// Standard order after editor-specific items:
/// Dimension (2D/3D) → Preview Target → View → Camera → Gizmo.
/// </remarks>
internal static class EditorViewportChrome
{
    public static string DisplayAssetName(string? pathOrName) => ResourceDisplayName.Format(pathOrName);
    public sealed class Options
    {
        public required EditorViewport3D Viewport { get; init; }
        public Func<bool>? GetIs2D { get; init; }
        public Action<bool>? SetIs2D { get; init; }
        public string DimensionCaption { get; init; } = "View";
        public EditorPreviewTargetChrome.PreviewTargetBinding? PreviewTarget { get; init; }
        public string ViewTooltip { get; init; } = "Grid and reference-floor options";
        public EditorViewMenuChrome.FloorStyleBinding? FloorStyle { get; init; }
        public EditorViewMenuChrome.GridBinding? Grid { get; init; }
        public EditorViewMenuChrome.ToggleBinding? Wireframe { get; init; }
        public EditorViewMenuChrome.ToggleBinding? Fog { get; init; }
        public EditorViewMenuChrome.ToggleBinding? IsolateSelection { get; init; }
        public Editor3DSession? Session { get; init; }
        public IEnumerable<ToolStripItem>? ViewLeadingItems { get; init; }
        public IEnumerable<ToolStripItem>? ViewExtraItems { get; init; }
        public IEnumerable<ToolStripItem>? CameraExtraItems { get; init; }
        public bool IncludeGizmo { get; init; } = true;
        public string GizmoTooltip { get; init; } = "Move, rotate, or scale the selection";
        public Func<EditorGizmoMode>? ReadGizmoMode { get; init; }
        public Action<EditorGizmoMode>? WriteGizmoMode { get; init; }
        public Func<EditorGizmoSpace>? ReadGizmoSpace { get; init; }
        public Action<EditorGizmoSpace>? WriteGizmoSpace { get; init; }
        public Action? Invalidate { get; init; }
        public bool IncludeRotateGizmo { get; init; } = true;
        public bool IncludeScaleGizmo { get; init; } = true;
    }

    public sealed class Attached
    {
        public EditorDimensionChrome.DimensionToggle? Dimension { get; init; }
        public EditorPreviewTargetChrome.PreviewTargetControls? PreviewTarget { get; init; }
    }

    /// <summary>Appends the shared viewport chrome block to an existing toolbar.</summary>
    public static Attached Attach(ToolStrip toolbar, Options options)
    {
        ArgumentNullException.ThrowIfNull(toolbar);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Viewport);

        EditorDimensionChrome.DimensionToggle? dimension = null;
        EditorPreviewTargetChrome.PreviewTargetControls? previewTarget = null;

        if (toolbar.Items.Count > 0)
            toolbar.Items.Add(new ToolStripSeparator());

        if (options.GetIs2D is not null && options.SetIs2D is not null)
        {
            dimension = EditorDimensionChrome.AddTo(
                toolbar,
                options.GetIs2D,
                options.SetIs2D,
                options.DimensionCaption);
        }

        if (options.PreviewTarget is not null)
        {
            if (toolbar.Items.Count > 0)
                toolbar.Items.Add(new ToolStripSeparator());
            previewTarget = EditorPreviewTargetChrome.AddTo(toolbar, options.PreviewTarget);
        }

        if (toolbar.Items.Count > 0)
            toolbar.Items.Add(new ToolStripSeparator());

        toolbar.Items.Add(EditorViewMenuChrome.BuildViewMenu(
            options.ViewTooltip,
            grid: options.Grid,
            floorStyle: options.FloorStyle,
            wireframe: options.Wireframe,
            viewport: () => options.Viewport,
            fog: options.Fog,
            isolateSelection: options.IsolateSelection,
            session: options.Session,
            leadingItems: options.ViewLeadingItems,
            extraItems: options.ViewExtraItems));

        toolbar.Items.Add(EditorCameraMenuChrome.BuildCameraMenu(
            options.Viewport,
            options.CameraExtraItems));

        if (options.IncludeGizmo &&
            options.ReadGizmoMode is not null &&
            options.WriteGizmoMode is not null &&
            options.ReadGizmoSpace is not null &&
            options.WriteGizmoSpace is not null)
        {
            toolbar.Items.Add(EditorGizmoMenuChrome.BuildGizmoMenu(
                options.GizmoTooltip,
                options.ReadGizmoMode,
                options.WriteGizmoMode,
                options.ReadGizmoSpace,
                options.WriteGizmoSpace,
                options.Invalidate,
                includeRotate: options.IncludeRotateGizmo,
                includeScale: options.IncludeScaleGizmo));
        }

        return new Attached
        {
            Dimension = dimension,
            PreviewTarget = previewTarget,
        };
    }

    /// <summary>Pins shared File/Edit menus at the start of a Suite document toolbar.</summary>
    public static void AttachDocumentMenus(
        ToolStrip toolbar,
        IEditorSurface surface,
        IEnumerable<ToolStripItem>? fileExtras = null,
        IEnumerable<ToolStripItem>? editExtras = null)
    {
        ArgumentNullException.ThrowIfNull(toolbar);
        ArgumentNullException.ThrowIfNull(surface);

        List<ToolStripItem> leading =
        [
            EditorDocumentMenuChrome.BuildFileMenu(surface, fileExtras),
            EditorDocumentMenuChrome.BuildEditMenu(surface, editExtras),
        ];

        for (int i = leading.Count - 1; i >= 0; i--)
            toolbar.Items.Insert(0, leading[i]);

        if (toolbar.Items.Count > leading.Count)
            toolbar.Items.Insert(leading.Count, new ToolStripSeparator());
    }
}
