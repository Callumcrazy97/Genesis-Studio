using System;
using System.Collections.Generic;

namespace Genesis.Shared.Assets;

/// <summary>
/// Runtime-facing sprite descriptor. Mirrors editor schema v2 fields consumed by Ember.
/// Authoring-only data (layers, rig, material channels) is intentionally omitted.
/// </summary>
public sealed class SpriteRuntimeAsset
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public SpriteRuntimeCanvas Canvas { get; set; } = new();

    public SpriteRuntimeImport Import { get; set; } = new();

    public SpriteRuntimeOrigin Origin { get; set; } = new();

    /// <summary>Atlas group this image belongs to. Empty resolves to Default Texture Group.</summary>
    public string TextureGroup { get; set; } = TextureGroupDefaults.DefaultName;

    public SpriteRuntimeUsage Usage { get; set; } = new();

    public List<SpriteRuntimeCollisionShape> CollisionShapes { get; set; } = [];

    public List<SpriteRuntimeFrame> Frames { get; set; } = [];

    public List<SpriteRuntimeAnimationTag> Tags { get; set; } = [];
}

public sealed class SpriteRuntimeCanvas
{
    public int Width { get; set; } = 64;

    public int Height { get; set; } = 64;
}

public sealed class SpriteRuntimeImport
{
    public string Source { get; set; } = string.Empty;
}

public sealed class SpriteRuntimeOrigin
{
    public double X { get; set; } = 0.5;

    public double Y { get; set; } = 0.5;

    public string Space { get; set; } = "normalized";
}

public sealed class SpriteRuntimeFrame
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int DurationMilliseconds { get; set; } = 100;

    public string Source { get; set; } = string.Empty;

    public SpriteRuntimeRectangle SourceRectangle { get; set; } = new();

    public SpriteRuntimeOrigin OriginOverride { get; set; }
}

public sealed class SpriteRuntimeRectangle
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

public sealed class SpriteRuntimeAnimationTag
{
    public string Name { get; set; } = string.Empty;

    public string StartFrameId { get; set; } = string.Empty;

    public string EndFrameId { get; set; } = string.Empty;

    public string Direction { get; set; } = "forward";

    public bool Loop { get; set; } = true;
}

/// <summary>Authoring collision metadata is also needed before the first rendered frame.</summary>
public sealed class SpriteRuntimeUsage
{
    public SpriteRuntimeTileset Tileset { get; set; } = new();
}

public sealed class SpriteRuntimeTileset
{
    public int TileWidth { get; set; } = 16;
    public int TileHeight { get; set; } = 16;
    public int Margin { get; set; }
    public int Spacing { get; set; }
    public int Columns { get; set; }
    public List<int> Collision { get; set; } = [];
}

public sealed class SpriteRuntimePoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class SpriteRuntimeCollisionShape
{
    public string FrameId { get; set; } = "";
    public SpriteRuntimePoint Position { get; set; } = new();
    public SpriteRuntimePoint Size { get; set; } = new();
    public double Radius { get; set; }
    public List<SpriteRuntimePoint> Points { get; set; } = [];
    public bool IsTrigger { get; set; }
}
