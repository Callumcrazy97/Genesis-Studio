using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    private sealed class UiDocumentCacheEntry
    {
        public long Stamp { get; init; }
        public long Length { get; init; }
        public UiAssetDocument Document { get; init; }
    }

    private sealed class UiLayoutCache
    {
        public IReadOnlyList<UiElement> Ordered { get; init; }
        public IReadOnlyList<UiElement> ReverseOrdered { get; init; }
        public IReadOnlyDictionary<string, UiElement> Elements { get; init; }
        public IReadOnlyDictionary<string, RectangleF> Rectangles { get; init; }
        /// <summary>How long each laid-out Panel's or Scroll List's content is, in design pixels.</summary>
        public IReadOnlyDictionary<string, float> ContentExtents { get; init; }
        /// <summary>The Scroll Lists each element is inside, nearest first (elements in none are left out).</summary>
        public IReadOnlyDictionary<string, string[]> ScrollAncestors { get; init; }
    }

    private static readonly object UiCacheGate = new();
    private static readonly Dictionary<string, UiDocumentCacheEntry> UiDocumentCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<UiAssetDocument, UiPlacement> UiLayoutCaches = new();

    [PgslCommand("DrawUi", "DrawUi(uiAsset)", "Draw a User Interface resource in the GUI pass", "User Interface")]
    public static void DrawUi(string uiAsset)
    {
        IPgslDrawSurface surface = Draw;
        PgslContext context = GetContext();
        if (!_uiEnabled || surface is null || context is null || !TryLoadUi(uiAsset, out UiAssetDocument document)) return;

        (UiLayoutCache layout, float scaleX, float scaleY) = UiPlaced(document, UiTargetSize(context));
        UpdateUiPointer(context, uiAsset, layout, scaleX, scaleY);
        UiPointerState pointer = ReadUiPointer(context, uiAsset);

        // Children of a Scroll List are moved by its scroll and seen only through it.
        RectangleF? clipped = null;
        // The debug screen's GUI page lists each resource drawn, its elements and their area.
        bool trace = Genesis.Runtime.Debugger.GuiLayerTrace.Enabled;
        int traced = 0;
        RectangleF tracedArea = RectangleF.Empty;
        foreach (UiElement element in layout.Ordered)
        {
            if (!IsUiElementVisible(context, uiAsset, element, layout.Elements)) continue;
            if (!layout.Rectangles.TryGetValue(element.Id, out RectangleF designRect)) continue;
            RectangleF rect = ScaleUiRect(UiScrolledRect(context, uiAsset, layout, element, designRect), scaleX, scaleY);
            RectangleF? clip = UiClipRect(context, uiAsset, layout, element) is RectangleF seen ? ScaleUiRect(seen, scaleX, scaleY) : null;
            if (clip is RectangleF window && !window.IntersectsWith(rect)) continue;
            if (clip != clipped)
            {
                surface.SetClip(clip ?? RectangleF.Empty);
                clipped = clip;
            }
            DrawUiElement(surface, context, uiAsset, element, rect, MathF.Min(scaleX, scaleY), UiElementState(context, uiAsset, element, pointer),
                UiNamed(context, uiAsset, document, element));
            if (trace)
            {
                traced++;
                tracedArea = tracedArea.IsEmpty ? rect : RectangleF.Union(tracedArea, rect);
            }
        }
        if (clipped != null) surface.SetClip(RectangleF.Empty);
        if (trace) Genesis.Runtime.Debugger.GuiLayerTrace.NoteUi(uiAsset, traced, tracedArea, UiTargetSize(context));
    }

    [PgslCommand("UiSetText", "UiSetText(uiAsset, elementId, text)", "Override text for one UI element on this instance", "User Interface")]
    public static void UiSetText(string uiAsset, string elementId, string text) =>
        SetUiOverride(uiAsset, elementId, "text", text ?? string.Empty);

    [PgslCommand("UiSetValue", "UiSetValue(uiAsset, elementId, value)", "Override a progress/value element on this instance", "User Interface")]
    public static void UiSetValue(string uiAsset, string elementId, double value) =>
        SetUiOverride(uiAsset, elementId, "value", value);

    [PgslCommand("UiSetVisible", "UiSetVisible(uiAsset, elementId, visible)", "Show or hide one UI element on this instance", "User Interface")]
    public static void UiSetVisible(string uiAsset, string elementId, bool visible) =>
        SetUiOverride(uiAsset, elementId, "visible", visible);

    [PgslCommand("UiMouseX", "UiMouseX() -> number", "Mouse X in GUI pixels, including window scaling", "User Interface")]
    public static double UiMouseX() => (ActiveGameContext?.Input?.MousePosition.X ?? 0)
        * (ActiveGameContext?.RenderWidth > 0 && ActiveGameContext.ClientWidth > 0 ? (double)ActiveGameContext.RenderWidth / ActiveGameContext.ClientWidth : 1);

    [PgslCommand("UiMouseY", "UiMouseY() -> number", "Mouse Y in GUI pixels, including window scaling", "User Interface")]
    public static double UiMouseY() => (ActiveGameContext?.Input?.MousePosition.Y ?? 0)
        * (ActiveGameContext?.RenderHeight > 0 && ActiveGameContext.ClientHeight > 0 ? (double)ActiveGameContext.RenderHeight / ActiveGameContext.ClientHeight : 1);

    [PgslCommand("UiHitTest", "UiHitTest(uiAsset, x, y) -> string", "Topmost visible UI element at a GUI point", "User Interface")]
    public static string UiHitTest(string uiAsset, double x, double y)
    {
        PgslContext context = GetContext();
        if (!_uiEnabled || context is null || !TryLoadUi(uiAsset, out UiAssetDocument document)) return string.Empty;
        (UiLayoutCache layout, float scaleX, float scaleY) = UiPlaced(document, UiTargetSize(context));
        foreach (UiElement element in layout.ReverseOrdered)
        {
            if (!IsUiElementVisible(context, uiAsset, element, layout.Elements)) continue;
            if (!layout.Rectangles.TryGetValue(element.Id, out RectangleF rect)) continue;
            rect = ScaleUiRect(UiScrolledRect(context, uiAsset, layout, element, rect), scaleX, scaleY);
            if (UiClipRect(context, uiAsset, layout, element) is RectangleF seen && !ScaleUiRect(seen, scaleX, scaleY).Contains((float)x, (float)y)) continue;
            if (rect.Contains((float)x, (float)y)) return element.Id;
        }
        return string.Empty;
    }

    private static SizeF UiTargetSize(PgslContext context)
    {
        if (context.DrawSurface is PgslRenderDrawSurface { GuiSize.IsEmpty: false } surface) return surface.GuiSize;
        if (ActiveGameContext?.RenderWidth > 0 && ActiveGameContext.RenderHeight > 0)
            return new SizeF(ActiveGameContext.RenderWidth, ActiveGameContext.RenderHeight);
        return new SizeF((float)Math.Max(1d, context.RoomWidth), (float)Math.Max(1d, context.RoomHeight));
    }

    private static RectangleF ResolveUiRect(
        UiAssetDocument document,
        UiElement element,
        IReadOnlyDictionary<string, UiElement> elements,
        IDictionary<string, RectangleF> cache,
        HashSet<string> visiting,
        IReadOnlyDictionary<string, float> slots = null,
        SizeF root = default)
    {
        if (cache.TryGetValue(element.Id, out RectangleF cached)) return cached;
        RectangleF parent = root.IsEmpty ? new(0, 0, document.DesignWidth, document.DesignHeight) : new(0, 0, root.Width, root.Height);
        UiElement container = null;
        if (!string.IsNullOrWhiteSpace(element.ParentId)
            && elements.TryGetValue(element.ParentId, out UiElement parentElement)
            && visiting.Add(element.Id))
        {
            parent = ResolveUiRect(document, parentElement, elements, cache, visiting, slots, root);
            visiting.Remove(element.Id);
            container = parentElement;
        }
        // A laid-out parent places its children one after another; the rest are anchored.
        RectangleF result = container is { Layout: not UiLayoutMode.None } && slots != null && slots.TryGetValue(element.Id, out float slot)
            ? LayoutUiRect(parent, container, element, slot)
            : AnchorRect(parent, element);
        cache[element.Id] = result;
        return result;
    }

    private static RectangleF AnchorRect(RectangleF parent, UiElement element)
    {
        float width = MathF.Max(1f, element.Width);
        float height = MathF.Max(1f, element.Height);
        float x = parent.X + element.X;
        float y = parent.Y + element.Y;
        switch (element.Anchor)
        {
            case UiAnchor.Top: x = parent.X + (parent.Width - width) * 0.5f + element.X; break;
            case UiAnchor.TopRight: x = parent.Right - width - element.X; break;
            case UiAnchor.Left: y = parent.Y + (parent.Height - height) * 0.5f + element.Y; break;
            case UiAnchor.Center:
                x = parent.X + (parent.Width - width) * 0.5f + element.X;
                y = parent.Y + (parent.Height - height) * 0.5f + element.Y;
                break;
            case UiAnchor.Right:
                x = parent.Right - width - element.X;
                y = parent.Y + (parent.Height - height) * 0.5f + element.Y;
                break;
            case UiAnchor.BottomLeft: y = parent.Bottom - height - element.Y; break;
            case UiAnchor.Bottom:
                x = parent.X + (parent.Width - width) * 0.5f + element.X;
                y = parent.Bottom - height - element.Y;
                break;
            case UiAnchor.BottomRight:
                x = parent.Right - width - element.X;
                y = parent.Bottom - height - element.Y;
                break;
            case UiAnchor.Stretch:
                width = MathF.Max(1f, parent.Width - element.X - element.Width);
                height = MathF.Max(1f, parent.Height - element.Y - element.Height);
                break;
        }
        return new RectangleF(x, y, width, height);
    }

    private static bool TryLoadUi(string asset, out UiAssetDocument document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(asset)) return false;
        string project = ProjectPath ?? string.Empty;
        string path = ResourceNames.Resolve(project, asset, ResourceType.UserInterface);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            FileInfo info = new(path);
            lock (UiCacheGate)
            {
                if (UiDocumentCache.TryGetValue(path, out UiDocumentCacheEntry cached)
                    && cached.Stamp == info.LastWriteTimeUtc.Ticks
                    && cached.Length == info.Length)
                {
                    document = cached.Document;
                    return true;
                }
            }

            UiAssetDocument loaded = UiAssetDocument.Load(path);
            lock (UiCacheGate)
            {
                UiDocumentCache[path] = new UiDocumentCacheEntry
                {
                    Stamp = info.LastWriteTimeUtc.Ticks,
                    Length = info.Length,
                    Document = loaded,
                };
            }
            document = loaded;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or System.Text.Json.JsonException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            lock (UiCacheGate)
                if (UiDocumentCache.TryGetValue(path, out UiDocumentCacheEntry cached)) { document = cached.Document; return true; }
            return false;
        }
    }

    // The UI's layout as last placed on the screen (its design size until first drawn).
    private static UiLayoutCache GetUiLayout(UiAssetDocument document)
    {
        UiPlacement placement = UiLayoutCaches.GetValue(document, static _ => new UiPlacement());
        lock (placement)
            return placement.Layout ??= BuildUiLayout(document, default);
    }

    /// <summary>
    /// The UI laid out for the screen it is drawn on, and the factors from its design pixels to the
    /// screen's: stretched to the window (Engine.Ui.Scale.Auto.Enabled) or at its design pixels with
    /// its anchors on the window's edges, times Engine.Ui.Scale.Value either way.
    /// </summary>
    private static (UiLayoutCache Layout, float ScaleX, float ScaleY) UiPlaced(UiAssetDocument document, SizeF target)
    {
        float scale = _uiScale;
        float fitX = _uiScaleAuto ? target.Width / Math.Max(1, document.DesignWidth) : 1f;
        float fitY = _uiScaleAuto ? target.Height / Math.Max(1, document.DesignHeight) : 1f;
        SizeF root = scale == 1f && _uiScaleAuto ? default
            : _uiScaleAuto ? new SizeF(document.DesignWidth / scale, document.DesignHeight / scale)
            : new SizeF(MathF.Max(1f, target.Width / scale), MathF.Max(1f, target.Height / scale));
        UiPlacement placement = UiLayoutCaches.GetValue(document, static _ => new UiPlacement());
        lock (placement)
        {
            if (placement.Layout == null || placement.Root != root)
            {
                placement.Layout = BuildUiLayout(document, root);
                placement.Root = root;
            }
            return (placement.Layout, fitX * scale, fitY * scale);
        }
    }

    private sealed class UiPlacement
    {
        public SizeF Root;
        public UiLayoutCache Layout;
    }

    private static UiLayoutCache BuildUiLayout(UiAssetDocument document, SizeF root)
    {
        UiElement[] ordered = document.Elements.OrderBy(element => element.Order).ToArray();
        Dictionary<string, UiElement> elements = ordered
            .Where(element => !string.IsNullOrWhiteSpace(element.Id))
            .GroupBy(element => element.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RectangleF> rects = new(StringComparer.OrdinalIgnoreCase);
        (Dictionary<string, float> slots, Dictionary<string, float> extents) = UiLayoutSlots(ordered, elements);
        foreach (UiElement element in ordered)
            ResolveUiRect(document, element, elements, rects, [], slots, root);
        Dictionary<string, string[]> scrollAncestors = new(StringComparer.OrdinalIgnoreCase);
        foreach (UiElement element in ordered)
            if (UiScrollAncestors(element, elements) is { Length: > 0 } lists) scrollAncestors[element.Id] = lists;
        return new UiLayoutCache
        {
            Ordered = ordered,
            ReverseOrdered = ordered.Reverse().ToArray(),
            Elements = elements,
            Rectangles = rects,
            ContentExtents = extents,
            ScrollAncestors = scrollAncestors,
        };
    }

    private static void SetUiOverride(string asset, string id, string property, object value)
    {
        PgslContext context = GetContext();
        if (context is null || string.IsNullOrWhiteSpace(asset) || string.IsNullOrWhiteSpace(id)) return;
        context.Variables[UiOverrideKey(asset, id, property)] = value;
    }

    private static string TextOverride(PgslContext context, string asset, UiElement element) =>
        context.Variables.TryGetValue(UiOverrideKey(asset, element.Id, "text"), out object value)
            ? Convert.ToString(value) ?? string.Empty
            : element.Text;

    private static float ValueOverride(PgslContext context, string asset, UiElement element) =>
        context.Variables.TryGetValue(UiOverrideKey(asset, element.Id, "value"), out object value)
            ? Convert.ToSingle(value)
            : element.Value;

    private static bool VisibleOverride(PgslContext context, string asset, UiElement element) =>
        context.Variables.TryGetValue(UiOverrideKey(asset, element.Id, "visible"), out object value)
            ? Convert.ToBoolean(value)
            : element.Visible;

    private static bool IsUiElementVisible(
        PgslContext context,
        string asset,
        UiElement element,
        IReadOnlyDictionary<string, UiElement> elements)
    {
        UiElement current = element;
        for (int depth = 0; depth <= elements.Count; depth++)
        {
            if (!VisibleOverride(context, asset, current)) return false;
            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !elements.TryGetValue(current.ParentId, out current)) return true;
        }
        return false;
    }

    // Made once per asset, element and property: every element's visibility (and its parents'),
    // text and value were looked up with a freshly joined key every frame (Mushroom Meadow's menus).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Asset, string Id, string Property), string> UiOverrideKeys = new();

    private static string UiOverrideKey(string asset, string id, string property) =>
        UiOverrideKeys.GetOrAdd((asset ?? string.Empty, id ?? string.Empty, property ?? string.Empty),
            static key => "__ui:" + key.Asset + ":" + key.Id + ":" + key.Property);
}
