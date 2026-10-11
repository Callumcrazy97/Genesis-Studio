using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Genesis.Runtime.Rendering;

/// <summary>2D background parallax read from an Image resource's own background usage.</summary>
/// <remarks>
/// The Image editor has always saved <c>usage.background.parallaxX/Y</c> (1 = moves with the
/// world, 0 = pinned to the view, 0.3 = a distant layer), but the 2D room pass ignored it. A
/// factor of 1, the default, leaves the drawn position exactly as before.
/// </remarks>
public sealed partial class RoomRenderSubsystem
{
    private readonly Dictionary<string, (float X, float Y)> _backgroundParallax = new(StringComparer.OrdinalIgnoreCase);

    private (float X, float Y) BackgroundParallax(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset) || !asset.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase)) return (1f, 1f);
        if (_backgroundParallax.TryGetValue(asset, out var cached)) return cached;
        (float X, float Y) factor = (1f, 1f);
        try
        {
            string path = Path.IsPathRooted(asset) ? asset : Path.Combine(_projectPath, asset.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && JObject.Parse(File.ReadAllText(path)).SelectToken("usage.background") is JObject background)
            {
                float x = background.Value<float?>("parallaxX") ?? 1f, y = background.Value<float?>("parallaxY") ?? 1f;
                if (float.IsFinite(x) && float.IsFinite(y)) factor = (x, y);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException) { }
        _backgroundParallax[asset] = factor;
        return factor;
    }
}
