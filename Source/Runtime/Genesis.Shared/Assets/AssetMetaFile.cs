using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Shared.Assets;

/// <summary>
/// Shared-layer reader/writer for the <c>.meta</c> sidecar files. The editor's
/// <c>GameManager.Models.ResourceMeta</c> owns the canonical shape (name/type/
/// created/layers); this class adds the <b>GUID</b> field and preserves every
/// other key present in the file (e.g. <c>owner_model</c>, <c>use_as_tileset</c>,
/// <c>ImportedSource</c>) by round-tripping through a <see cref="JObject"/>.
///
/// GUID is stored under the <c>guid</c> key as a 32-char hex string
/// (<see cref="AssetRef.GuidFormat"/>). Legacy metas without it are upgraded in
/// place on first read-write.
/// </summary>
public static class AssetMetaFile
{
    /// <summary>The JSON key under which the asset GUID is stored.</summary>
    public const string GuidKey = "guid";

    /// <summary>Read a GUID out of a <c>.meta</c> file. Returns <c>Guid.Empty</c> if missing/invalid/no file.</summary>
    public static Guid ReadGuid(string metaPath)
    {
        if (string.IsNullOrEmpty(metaPath) || !System.IO.File.Exists(metaPath)) return Guid.Empty;
        try
        {
            var obj = ReadObject(metaPath);
            return ReadGuid(obj);
        }
        catch { return Guid.Empty; }
    }

    /// <summary>Read a GUID from an already-parsed meta object.</summary>
    public static Guid ReadGuid(JObject obj)
    {
        if (obj == null) return Guid.Empty;
        var token = obj[GuidKey];
        if (token == null || token.Type == JTokenType.Null) return Guid.Empty;
        return Guid.TryParse(token.ToObject<string>(), out Guid g) ? g : Guid.Empty;
    }

    /// <summary>Read the type label (singular, e.g. "Sprite") out of a meta file. Null if absent.</summary>
    public static string ReadTypeLabel(string metaPath)
    {
        if (string.IsNullOrEmpty(metaPath) || !System.IO.File.Exists(metaPath)) return null;
        try
        {
            var obj = ReadObject(metaPath);
            var t = obj?["type"];
            return t?.Type == JTokenType.String ? t.ToObject<string>() : null;
        }
        catch { return null; }
    }

    /// <summary>Write a GUID into a <c>.meta</c> file, preserving all other keys. Creates the file if missing.</summary>
    public static void WriteGuid(string metaPath, Guid guid)
    {
        if (string.IsNullOrEmpty(metaPath) || guid == Guid.Empty) return;
        JObject obj;
        if (System.IO.File.Exists(metaPath))
        {
            obj = ReadObject(metaPath) ?? new JObject();
        }
        else
        {
            obj = new JObject();
        }
        obj[GuidKey] = guid.ToString(AssetRef.GuidFormat);
        WriteObject(metaPath, obj);
    }

    /// <summary>Parse a meta file as a <see cref="JObject"/> (null if empty/missing). Throws on malformed JSON.</summary>
    public static JObject ReadObject(string metaPath)
    {
        if (string.IsNullOrEmpty(metaPath) || !System.IO.File.Exists(metaPath)) return null;
        string text = System.IO.File.ReadAllText(metaPath);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JObject.Parse(text);
    }

    /// <summary>Write a meta object to disk, indented.</summary>
    public static void WriteObject(string metaPath, JObject obj)
    {
        if (string.IsNullOrEmpty(metaPath) || obj == null) return;
        string dir = System.IO.Path.GetDirectoryName(metaPath);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(metaPath, obj.ToString(Formatting.Indented));
    }
}
