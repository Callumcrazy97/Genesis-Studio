#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Genesis.Runtime.Modeling;
using Newtonsoft.Json.Linq;

namespace Genesis.Runtime.Scene;

/// <summary>
/// Objects inside Objects: an Object's <c>parts</c>, each another Object placed at an offset from it
/// (a cart's wheels, a knight's sword). Wherever the Object is made (a room, CreateInstance) each
/// part is made as an instance of its own, kept on it as it moves and turns, and destroyed with it.
/// Saved on the Object as <c>"parts": [{ "object": "Wheel", "x": 1, "y": 0, "z": 0.5, "pitch": 0,
/// "yaw": 0, "roll": 0, "scale": 1 }]</c>.
/// </summary>
public static class ObjectParts
{
    /// <summary>How deep parts of parts go (a part's parts are made too, up to this).</summary>
    public const int MaxDepth = 8;

    public readonly record struct Part(string Object, RoomTransform Local, Matrix4x4 Offset);

    /// <summary>The parts an Object's definition lists, in order (none when it lists none).</summary>
    public static List<Part> Read(JObject? prefab)
    {
        var parts = new List<Part>();
        if (prefab?["parts"] is not JArray list) return parts;
        foreach (JToken token in list)
        {
            if (token is not JObject part || ((string?)part["object"])?.Trim() is not { Length: > 0 } name) continue;
            float x = Number(part, "x", 0), y = Number(part, "y", 0), z = Number(part, "z", 0);
            float pitch = Number(part, "pitch", 0), yaw = Number(part, "yaw", 0), roll = Number(part, "roll", 0);
            float scale = Number(part, "scale", 1);
            var local = new RoomTransform
            {
                X = x, Y = y, Z = z, RotationX = pitch, RotationY = yaw, RotationZ = roll,
                ScaleX = scale, ScaleY = scale, ScaleZ = scale,
            };
            parts.Add(new Part(name, local, ModelSocketRuntime.Offset(x, y, z, pitch, yaw, roll, scale)));
        }
        return parts;
    }

    private static float Number(JObject part, string name, float fallback) =>
        float.TryParse(part[name]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
            ? value : fallback;
}
