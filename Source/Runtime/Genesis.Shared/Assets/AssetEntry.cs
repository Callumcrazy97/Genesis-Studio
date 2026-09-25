using System;

namespace Genesis.Shared.Assets;

/// <summary>
/// One resolved asset known to <see cref="AssetDatabase"/>. Created during a
/// scan and cached for the editor session. The <see cref="Guid"/> is the stable
/// identity (assigned once into the on-disk <c>.meta</c>); <see cref="Name"/>
/// and <see cref="RelativePath"/> update on rename/move; <see cref="Kind"/>
/// drives routing to the correct editor.
/// </summary>
public sealed class AssetEntry
{
    /// <summary>Stable GUID identity (32-char hex on disk).</summary>
    public Guid Guid { get; set; }

    /// <summary>Public resource name, case-insensitively unique across every resource type.</summary>
    public string Name { get; set; }

    /// <summary>Canonical kind (Sprite, Object, Room, ...).</summary>
    public AssetKind Kind { get; set; }

    /// <summary>Private project-relative storage location. Never use as an authoring/script reference.</summary>
    public string RelativePath { get; set; }

    /// <summary>Optional subfolder under the kind root (e.g. "Characters/Bosses"); "/" separated; null at root.</summary>
    public string Folder { get; set; }

    /// <summary>Absolute path to the primary on-disk file. Null if the entry is a virtual/unsaved resource.</summary>
    public string AbsolutePath { get; set; }

    /// <summary>Absolute path to the <c>.meta</c> sidecar (null if there is no meta file yet).</summary>
    public string MetaPath { get; set; }

    /// <summary>Build an <see cref="AssetRef"/> for this entry (carries GUID + cached name + kind).</summary>
    public AssetRef ToRef() => new AssetRef(Guid, Name, Kind);

    public override string ToString() => $"{Kind}:{Name} [{Guid.ToString(AssetRef.GuidFormat)}]";
}
