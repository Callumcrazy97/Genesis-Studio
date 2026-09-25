using System;

namespace Genesis.Shared.Assets;

/// <summary>
/// A stable, GUID-keyed reference to a project asset. This replaces the bare
/// name-string references (<c>ObjectResource.Sprite</c>, <c>RoomAsset</c>
/// background/tileset/prefab fields, component <c>Props</c>) so that rename and
/// move operations preserve references instead of silently breaking them.
///
/// Serialization: designed for Newtonsoft.Json. <c>Guid</c> is a 32-char hex
/// string (<see cref="GuidFormat"/>), matching <c>RoomAsset.NewId()</c>.
/// Empty <c>Guid</c> means "unset". <c>Name</c> is the human-readable label at
/// last resolution — kept for diagnostics and backward-compat migration only;
/// it is <b>not</b> authoritative.
/// </summary>
public readonly struct AssetRef : IEquatable<AssetRef>
{
    /// <summary>32 lowercase hex chars, no dashes — matches <see cref="Guid.ToString(string)"/> format "N".</summary>
    public const string GuidFormat = "N";

    public static readonly AssetRef Empty = new AssetRef(Guid.Empty, null, AssetKind.Unknown);

    /// <summary>Stable identity. <see cref="Guid.Empty"/> means the ref is unset.</summary>
    public readonly Guid Guid;

    /// <summary>Display name at last resolution; null/blank when unset. Not authoritative.</summary>
    public readonly string Name;

    /// <summary>Kind cached at last resolution. <see cref="AssetKind.Unknown"/> when unset or unresolved.</summary>
    public readonly AssetKind Kind;

    public AssetRef(Guid guid, string name, AssetKind kind)
    {
        Guid = guid;
        Name = name;
        Kind = kind;
    }

    /// <summary>True when a GUID has been assigned (the ref points at something).</summary>
    public bool IsSet => Guid != Guid.Empty;

    /// <summary>True when no GUID is assigned.</summary>
    public bool IsEmpty => Guid == Guid.Empty;

    /// <summary>Build a ref from a 32-char hex GUID string and optional label/kind.</summary>
    public static AssetRef FromGuidString(string guidHex, string name = null, AssetKind kind = AssetKind.Unknown)
    {
        if (string.IsNullOrWhiteSpace(guidHex) || !Guid.TryParse(guidHex, out Guid g))
            return Empty;
        return new AssetRef(g, name, kind);
    }

    /// <summary>Convenience for the common "ref by GUID only" case.</summary>
    public static AssetRef Of(Guid guid) => guid == Guid.Empty ? Empty : new AssetRef(guid, null, AssetKind.Unknown);

    public bool Equals(AssetRef other) => Guid == other.Guid;
    public override bool Equals(object obj) => obj is AssetRef other && Equals(other);
    public override int GetHashCode() => Guid.GetHashCode();

    public static bool operator ==(AssetRef left, AssetRef right) => left.Guid == right.Guid;
    public static bool operator !=(AssetRef left, AssetRef right) => left.Guid != right.Guid;

    public override string ToString()
    {
        if (IsEmpty) return "AssetRef(empty)";
        return string.IsNullOrEmpty(Name)
            ? $"AssetRef({Guid.ToString(GuidFormat)})"
            : $"AssetRef({Name}:{Guid.ToString(GuidFormat)})";
    }
}
