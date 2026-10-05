using System;
using System.IO;
using Genesis.Runtime.Assets;
using Genesis.Shared.Assets;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Directional sprites: Images whose frames show a model from evenly spaced facing angles
/// (Model Viewer, Convert to 2D sprites). Angles follow <see cref="PointDirection"/>: 0 faces
/// right (+X), 90 faces up the screen, counter-clockwise. A frame covers half a step either side of
/// its angle, so with 8 directions 0-22 picks the right-facing frame and 23-67 the up-right one.
/// </summary>
public static partial class PgslCommands
{
    [PgslCommand("SpriteDirectionFrame", "SpriteDirectionFrame(image,angleDegrees,animationFrame?) -> number",
        "Frame index of a directional Image for a facing angle (0 = right, counter-clockwise) and animation frame; -1 when the image is missing", "Sprites")]
    public static double SpriteDirectionFrame(string image, double angleDegrees, double animationFrame = 0)
    {
        if (!TryResolveDirectionalImage(image, out SpriteRuntimeDirections directions)) return -1;
        return SpriteDirections.FrameIndex(directions.Count, directions.FramesPerDirection, angleDegrees, animationFrame, directions.StartAngleDegrees);
    }

    [PgslCommand("SpriteDirectionCount", "SpriteDirectionCount(image) -> number",
        "How many facing angles a directional Image holds (one per frame for an Image without direction data); 0 when missing", "Sprites")]
    public static double SpriteDirectionCount(string image) =>
        TryResolveDirectionalImage(image, out SpriteRuntimeDirections directions) ? directions.Count : 0;

    [PgslCommand("SpriteDirectionFrames", "SpriteDirectionFrames(image) -> number",
        "Animation frames stored for each direction of a directional Image; 0 when missing", "Sprites")]
    public static double SpriteDirectionFrames(string image) =>
        TryResolveDirectionalImage(image, out SpriteRuntimeDirections directions) ? directions.FramesPerDirection : 0;

    [PgslCommand("SpriteSetDirection", "SpriteSetDirection(angleDegrees,animationFrame?) -> bool",
        "Show this instance's directional sprite facing an angle (0 = right, counter-clockwise); without an animation frame the current one is kept, so ImageSpeed animates within the direction", "Sprites")]
    public static bool SpriteSetDirection(double angleDegrees, double animationFrame = -1)
    {
        PgslContext ctx = GetContext();
        if (ctx is null || !double.IsFinite(angleDegrees)
            || !TryResolveDirectionalImage(ctx.SpriteIndex, out SpriteRuntimeDirections directions)) return false;
        double frame = animationFrame;
        if (frame < 0 || !double.IsFinite(frame))
        {
            double current = double.IsFinite(ctx.ImageIndex) ? Math.Max(0, Math.Floor(ctx.ImageIndex)) : 0;
            frame = current % directions.FramesPerDirection;
        }
        ctx.ImageIndex = SpriteDirections.FrameIndex(directions.Count, directions.FramesPerDirection, angleDegrees, frame, directions.StartAngleDegrees);
        return true;
    }

    private static bool TryResolveDirectionalImage(string image, out SpriteRuntimeDirections directions)
    {
        directions = null;
        if (string.IsNullOrWhiteSpace(image)) return false;
        // The cache is cleared when project assets change, so a Step-event call costs no file I/O.
        if (!SpriteAssetLoader.TryGetCachedAsset(image, out SpriteRuntimeAsset asset))
        {
            if (string.IsNullOrWhiteSpace(ProjectPath)) return false;
            try { asset = SpriteAssetLoader.Load(ProjectPath, image); }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException
                or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
        directions = SpriteDirections.Resolve(asset);
        return directions is not null;
    }
}
