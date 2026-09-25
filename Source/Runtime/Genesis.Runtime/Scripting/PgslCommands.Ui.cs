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
    }

    private static readonly object UiCacheGate = new();
    private static readonly Dictionary<string, UiDocumentCacheEntry> UiDocumentCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<UiAssetDocument, UiLayoutCache> UiLayoutCaches = new();

    [PgslCommand("DrawUi", "DrawUi(uiAsset)", "Draw a User Interface resource in the GUI pass", "User Interface")]
    public static void DrawUi(string uiAsset)
    {
        IPgslDrawSurface surface = Draw;
        PgslContext context = GetContext();
        if (surface is null || context is null || !TryLoadUi(uiAsset, out UiAssetDocument document)) return;

        float targetWidth = (float)Math.Max(1d, context.RoomWidth);
        float targetHeight = (float)Math.Max(1d, context.RoomHeight);
        float scaleX = targetWidth / Math.Max(1, document.DesignWidth);
        float scaleY = targetHeight / Math.Max(1, document.DesignHeight);
        UiLayoutCache layout = GetUiLayout(document);

        foreach (UiElement element in layout.Ordered)
        {
            if (!IsUiElementVisible(context, uiAsset, element, layout.Elements)) continue;
            if (!layout.Rectangles.TryGetValue(element.Id, out RectangleF designRect)) continue;
            RectangleF rect = new(
                designRect.X * scaleX,
                designRect.Y * scaleY,
                designRect.Width * scaleX,
                designRect.Height * scaleY);
            Color background = ParseUiColor(element.Background, Color.FromArgb(204, 22, 27, 34));
            Color foreground = ParseUiColor(element.Foreground, Color.White);
            Color accent = ParseUiColor(element.Accent, Color.FromArgb(108, 140, 255));
            string text = TextOverride(context, uiAsset, element);

            switch (element.Type)
            {
                case UiElementType.Panel:
                    surface.FillRectangle(background, rect);
                    break;
                case UiElementType.Text:
                    surface.DrawText(text, element.Font, element.FontSize * MathF.Min(scaleX, scaleY), foreground, Rectangle.Round(rect));
                    break;
                case UiElementType.Image:
                    if (!string.IsNullOrWhiteSpace(element.Image))
                        surface.DrawSprite(element.Image, rect.X, rect.Y, 0,
                            element.ImageScaleX * scaleX, element.ImageScaleY * scaleY, 0f, Color.White, foreground.A / 255f);
                    break;
                case UiElementType.Button:
                    surface.FillRectangle(background, rect);
                    surface.DrawRectangle(accent, rect);
                    surface.DrawText(text, element.Font, element.FontSize * MathF.Min(scaleX, scaleY), foreground, Rectangle.Round(rect));
                    break;
                case UiElementType.ProgressBar:
                    surface.FillRectangle(background, rect);
                    float maximum = MathF.Max(0.0001f, element.Maximum);
                    float value = ValueOverride(context, uiAsset, element);
                    float amount = Math.Clamp(value / maximum, 0f, 1f);
                    surface.FillRectangle(accent, new RectangleF(rect.X, rect.Y, rect.Width * amount, rect.Height));
                    surface.DrawRectangle(foreground, rect);
                    break;
            }
        }
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

    [PgslCommand("UiHitTest", "UiHitTest(uiAsset, x, y) -> string", "Topmost visible UI element at a GUI point", "User Interface")]
    public static string UiHitTest(string uiAsset, double x, double y)
    {
        PgslContext context = GetContext();
        if (context is null || !TryLoadUi(uiAsset, out UiAssetDocument document)) return string.Empty;
        float scaleX = (float)Math.Max(1d, context.RoomWidth) / Math.Max(1, document.DesignWidth);
        float scaleY = (float)Math.Max(1d, context.RoomHeight) / Math.Max(1, document.DesignHeight);
        UiLayoutCache layout = GetUiLayout(document);
        foreach (UiElement element in layout.ReverseOrdered)
        {
            if (!IsUiElementVisible(context, uiAsset, element, layout.Elements)) continue;
            if (!layout.Rectangles.TryGetValue(element.Id, out RectangleF rect)) continue;
            rect = new RectangleF(rect.X * scaleX, rect.Y * scaleY, rect.Width * scaleX, rect.Height * scaleY);
            if (rect.Contains((float)x, (float)y)) return element.Id;
        }
        return string.Empty;
    }

    private static RectangleF ResolveUiRect(
        UiAssetDocument document,
        UiElement element,
        IReadOnlyDictionary<string, UiElement> elements,
        IDictionary<string, RectangleF> cache,
        HashSet<string> visiting)
    {
        if (cache.TryGetValue(element.Id, out RectangleF cached)) return cached;
        RectangleF parent = new(0, 0, document.DesignWidth, document.DesignHeight);
        if (!string.IsNullOrWhiteSpace(element.ParentId)
            && elements.TryGetValue(element.ParentId, out UiElement parentElement)
            && visiting.Add(element.Id))
        {
            parent = ResolveUiRect(document, parentElement, elements, cache, visiting);
            visiting.Remove(element.Id);
        }
        RectangleF result = AnchorRect(parent, element);
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
            or ArgumentException) { return false; }
    }

    private static UiLayoutCache GetUiLayout(UiAssetDocument document) =>
        UiLayoutCaches.GetValue(document, BuildUiLayout);

    private static UiLayoutCache BuildUiLayout(UiAssetDocument document)
    {
        UiElement[] ordered = document.Elements.OrderBy(element => element.Order).ToArray();
        Dictionary<string, UiElement> elements = ordered
            .Where(element => !string.IsNullOrWhiteSpace(element.Id))
            .GroupBy(element => element.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RectangleF> rects = new(StringComparer.OrdinalIgnoreCase);
        foreach (UiElement element in ordered)
            ResolveUiRect(document, element, elements, rects, []);
        return new UiLayoutCache
        {
            Ordered = ordered,
            ReverseOrdered = ordered.Reverse().ToArray(),
            Elements = elements,
            Rectangles = rects,
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

    private static string UiOverrideKey(string asset, string id, string property) =>
        "__ui:" + asset + ":" + id + ":" + property;

    private static Color ParseUiColor(string value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string hex = value.Trim().TrimStart('#');
        try
        {
            return hex.Length switch
            {
                6 => Color.FromArgb(255, Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16)),
                8 => Color.FromArgb(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16), Convert.ToInt32(hex[6..8], 16)),
                _ => fallback,
            };
        }
        catch (FormatException) { return fallback; }
    }
}
