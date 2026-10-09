using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading;
using Genesis.Runtime.Project;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    // A plain numeric handle is always a number. Only these explicit references denote a container.
    private sealed record CollectionReference(string Kind, int Handle);
    private static readonly AsyncLocal<string> JsonErrorFallback = new();

    private static void SetJsonError(string error)
    {
        if (Store != null) Store["__json_error"] = error;
        else JsonErrorFallback.Value = error;
    }

    [PgslCommand("JsonLastError", "JsonLastError() -> string", "Last JSON or typed-collection error for this Object", "JSON")]
    public static string JsonLastError() => Store?.TryGetValue("__json_error", out object value) == true
        ? value as string ?? "" : JsonErrorFallback.Value ?? "";

    private static string CollectionKind(string kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "map" => "map", "list" => "list", _ => throw new ArgumentException("Collection kind must be map or list.")
    };

    private static CollectionReference Collection(double handle, string kind)
    {
        kind = CollectionKind(kind);
        if (!double.IsFinite(handle) || handle < 1 || handle > int.MaxValue || Math.Truncate(handle) != handle)
            throw new ArgumentException("Invalid collection handle.");
        bool present = kind == "map" ? ResolveRead<PgslMap>(kind, handle) != null : ResolveRead<List<object>>(kind, handle) != null;
        if (!present) throw new InvalidOperationException("Unknown or released " + kind + " handle: " + handle);
        return new(kind, (int)handle);
    }

    [PgslCommand("DsMapSetCollection", "DsMapSetCollection(id, key, handle, kind) -> bool", "Store an explicit map/list reference; numeric handles alone remain numbers", "Maps")]
    public static bool DsMapSetCollection(double id, string key, double handle, string kind)
    {
        try
        {
            _ = Collection(id, "map");
            if (key == null) throw new ArgumentException("Map key cannot be null.");
            PgslMap map = Resolve<PgslMap>("map", id);
            if (!map.Values.ContainsKey(key) && map.Values.Count >= MaxCollectionElements) throw new InvalidOperationException("Map is full.");
            MapStore(id, key, Collection(handle, kind)); SetJsonError(""); return true;
        }
        catch (Exception error) when (JsonError(error)) { SetJsonError(error.Message); return false; }
    }

    [PgslCommand("DsListAddCollection", "DsListAddCollection(id, handle, kind) -> bool", "Append an explicit map/list reference", "Lists")]
    public static bool DsListAddCollection(double id, double handle, string kind) => ListTypedValue(id, -1, () => Collection(handle, kind));
    [PgslCommand("DsListSetCollection", "DsListSetCollection(id, position, handle, kind) -> bool", "Replace a list entry with an explicit map/list reference", "Lists")]
    public static bool DsListSetCollection(double id, double position, double handle, string kind) => ListTypedValue(id, position, () => Collection(handle, kind), false);

    [PgslCommand("DsMapSetBoolean", "DsMapSetBoolean(id, key, value)", "Store a JSON boolean; DsMapGet reads it as 1 or 0", "Maps")]
    public static void DsMapSetBoolean(double id, string key, bool value) => MapStore(id, key, value);
    [PgslCommand("DsMapSetNull", "DsMapSetNull(id, key)", "Store JSON null, preserving the key", "Maps")]
    public static void DsMapSetNull(double id, string key) => MapStore(id, key, null);
    [PgslCommand("DsListAddBoolean", "DsListAddBoolean(id, value) -> bool", "Append a JSON boolean", "Lists")]
    public static bool DsListAddBoolean(double id, bool value) => ListTypedValue(id, -1, () => value);
    [PgslCommand("DsListAddNull", "DsListAddNull(id) -> bool", "Append JSON null", "Lists")]
    public static bool DsListAddNull(double id) => ListTypedValue(id, -1, () => null);
    [PgslCommand("DsListSetBoolean", "DsListSetBoolean(id, position, value) -> bool", "Replace an entry with a JSON boolean", "Lists")]
    public static bool DsListSetBoolean(double id, double position, bool value) => ListTypedValue(id, position, () => value, false);
    [PgslCommand("DsListSetNull", "DsListSetNull(id, position) -> bool", "Replace an entry with JSON null", "Lists")]
    public static bool DsListSetNull(double id, double position) => ListTypedValue(id, position, () => null, false);

    private static bool ListTypedValue(double id, double position, Func<object> value, bool append = true)
    {
        try
        {
            _ = Collection(id, "list");
            List<object> list = Resolve<List<object>>("list", id);
            if (append)
            {
                if (list.Count >= MaxCollectionElements) throw new InvalidOperationException("List is full.");
                list.Add(value());
            }
            else
            {
                if (!double.IsFinite(position) || position < 0 || position >= list.Count || Math.Truncate(position) != position)
                    throw new ArgumentException("List position is out of range.");
                list[(int)position] = value();
            }
            SetJsonError(""); return true;
        }
        catch (Exception error) when (JsonError(error)) { SetJsonError(error.Message); return false; }
    }

    private static string ValueKind(object value) => value switch
    {
        null => "null", CollectionReference reference => reference.Kind,
        string => "string", bool => "boolean", double or int or float or long => "number", _ => "unsupported"
    };

    [PgslCommand("DsMapValueKind", "DsMapValueKind(id, key) -> string", "Entry type: map, list, number, string, boolean, null or missing", "Maps")]
    public static string DsMapValueKind(double id, string key) => key != null
        && ResolveRead<PgslMap>("map", id)?.Values.TryGetValue(key, out object value) == true ? ValueKind(value) : "missing";
    [PgslCommand("DsListValueKind", "DsListValueKind(id, position) -> string", "Entry type; missing for invalid positions", "Lists")]
    public static string DsListValueKind(double id, double position)
    {
        List<object> list = ResolveRead<List<object>>("list", id);
        return list != null && double.IsFinite(position) && position >= 0 && position < list.Count && Math.Truncate(position) == position
            ? ValueKind(list[(int)position]) : "missing";
    }

    [PgslCommand("JsonEncode", "JsonEncode(handle, kind) -> string", "Encode a map/list tree with explicit nested references, up to 4 MiB and depth 64; cycles fail", "JSON")]
    public static string JsonEncode(double handle, string kind)
    {
        try
        {
            CollectionReference root = Collection(handle, kind);
            using MemoryStream stream = new();
            using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 64 });
            int elements = 0;
            Write(root, 0, new HashSet<CollectionReference>());
            writer.Flush();
            if (stream.Length > ProjectTextFiles.MaximumTextBytes) throw new InvalidOperationException("JSON exceeds the 4 MiB limit.");
            SetJsonError(""); return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);

            void Write(object value, int depth, HashSet<CollectionReference> active)
            {
                if (++elements > MaxCollectionElements || depth > 64) throw new InvalidOperationException("JSON exceeds the element or depth limit.");
                switch (value)
                {
                    case null: writer.WriteNullValue(); break;
                    case string text:
                        if (Encoding.UTF8.GetByteCount(text) > ProjectTextFiles.MaximumTextBytes) throw new InvalidOperationException("JSON string exceeds the text limit.");
                        writer.WriteStringValue(text); break;
                    case bool boolean: writer.WriteBooleanValue(boolean); break;
                    case double or int or float or long:
                        double number = AsNumber(value);
                        if (!double.IsFinite(number)) throw new InvalidOperationException("JSON numbers must be finite.");
                        writer.WriteNumberValue(number); break;
                    case CollectionReference reference:
                        _ = Collection(reference.Handle, reference.Kind);
                        if (!active.Add(reference)) throw new InvalidOperationException("JSON cannot encode a collection cycle.");
                        if (reference.Kind == "map")
                        {
                            writer.WriteStartObject();
                            PgslMap map = ResolveRead<PgslMap>("map", reference.Handle);
                            foreach (string key in map.Order) { writer.WritePropertyName(key); Write(map.Values[key], depth + 1, active); }
                            writer.WriteEndObject();
                        }
                        else
                        {
                            writer.WriteStartArray();
                            foreach (object entry in ResolveRead<List<object>>("list", reference.Handle)) Write(entry, depth + 1, active);
                            writer.WriteEndArray();
                        }
                        active.Remove(reference); break;
                    default: throw new InvalidOperationException("Unsupported JSON value.");
                }
                if (writer.BytesCommitted + writer.BytesPending > ProjectTextFiles.MaximumTextBytes)
                    throw new InvalidOperationException("JSON exceeds the 4 MiB limit.");
            }
        }
        catch (Exception error) when (JsonError(error)) { SetJsonError(error.Message); return ""; }
    }

    [PgslCommand("JsonDecode", "JsonDecode(text, kind) -> handle", "Decode an explicit map/list root; nested types are preserved; 0 on failure; release the owned tree with JsonFree", "JSON")]
    public static double JsonDecode(string text, string kind)
    {
        List<CollectionReference> owned = [];
        try
        {
            kind = CollectionKind(kind);
            if (Store == null) throw new InvalidOperationException("No active Object context for JSON collections.");
            if (text == null || Encoding.UTF8.GetByteCount(text) > ProjectTextFiles.MaximumTextBytes)
                throw new ArgumentException("JSON is missing or exceeds the 4 MiB limit.");
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            JsonValueKind expected = kind == "map" ? JsonValueKind.Object : JsonValueKind.Array;
            if (document.RootElement.ValueKind != expected) throw new ArgumentException("JSON root does not match kind " + kind + ".");
            int elements = 0;
            CollectionReference root = (CollectionReference)Read(document.RootElement);
            Store[OwnerKey(root)] = owned;
            SetJsonError(""); return root.Handle;

            object Read(JsonElement value)
            {
                if (++elements > MaxCollectionElements) throw new InvalidOperationException("JSON exceeds the element limit.");
                switch (value.ValueKind)
                {
                    case JsonValueKind.Null: return null;
                    case JsonValueKind.String: return value.GetString();
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    case JsonValueKind.Number:
                        if (!value.TryGetDouble(out double number) || !double.IsFinite(number)) throw new InvalidOperationException("JSON number is outside the finite numeric range.");
                        return number;
                    case JsonValueKind.Object:
                        int mapHandle = NextHandle("map");
                        PgslMap map = new(); Bind("map", mapHandle, map);
                        CollectionReference mapReference = new("map", mapHandle); owned.Add(mapReference);
                        foreach (JsonProperty property in value.EnumerateObject())
                        {
                            if (map.Values.ContainsKey(property.Name)) throw new InvalidOperationException("Duplicate JSON key: " + property.Name);
                            map.Values.Add(property.Name, Read(property.Value)); map.Order.Add(property.Name);
                        }
                        return mapReference;
                    case JsonValueKind.Array:
                        int listHandle = NextHandle("list");
                        List<object> list = []; Bind("list", listHandle, list);
                        CollectionReference listReference = new("list", listHandle); owned.Add(listReference);
                        foreach (JsonElement entry in value.EnumerateArray()) list.Add(Read(entry));
                        return listReference;
                    default: throw new InvalidOperationException("Unsupported JSON token.");
                }
            }
        }
        catch (Exception error) when (JsonError(error))
        {
            foreach (CollectionReference reference in owned) Unbind(reference.Kind, reference.Handle);
            SetJsonError(error.Message); return 0;
        }
    }

    private static string OwnerKey(CollectionReference root) => "__json_owned_" + root.Kind + "_" + root.Handle;

    [PgslCommand("JsonFree", "JsonFree(handle, kind) -> bool", "Release all decoder-owned containers, including removed children; never frees manually attached collections", "JSON")]
    public static bool JsonFree(double handle, string kind)
    {
        try
        {
            kind = CollectionKind(kind);
            if (!double.IsFinite(handle) || handle < 1 || handle > int.MaxValue || Math.Truncate(handle) != handle)
                throw new ArgumentException("Invalid decoder-owned handle.");
            string key = OwnerKey(new(kind, (int)handle));
            if (Store?.TryGetValue(key, out object tree) != true || tree is not List<CollectionReference> owned)
                throw new InvalidOperationException("Handle is not a live decoder-owned root.");
            foreach (CollectionReference reference in owned) Unbind(reference.Kind, reference.Handle);
            Store.Remove(key); SetJsonError(""); return true;
        }
        catch (Exception error) when (JsonError(error)) { SetJsonError(error.Message); return false; }
    }

    private static bool JsonError(Exception error) => error is ArgumentException or InvalidOperationException or JsonException;
}
