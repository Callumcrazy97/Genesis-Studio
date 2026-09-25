using System;

namespace Genesis.Shared.Assets;

/// <summary>Shared Texture Group names and atlas sizes used by Studio and Player.</summary>
public static class TextureGroupDefaults
{
    public const string DefaultName = "Default Texture Group";
    public const int DefaultAtlasSize = 2048;
    public const int LargeAtlasSize = 4096;

    public static string NormalizeOrDefault(string name) =>
        string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();

    public static int NormalizeAtlasSize(int size) =>
        size >= LargeAtlasSize ? LargeAtlasSize : DefaultAtlasSize;

    public static bool IsDefault(string name) =>
        string.Equals(NormalizeOrDefault(name), DefaultName, StringComparison.OrdinalIgnoreCase);
}
