using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Genesis.Runtime.Project;

/// <summary>Small versioned numeric save slots, isolated by project identity, with atomic replacement.</summary>
public sealed class ProjectNumberSave
{
    private static readonly Dictionary<string, ProjectNumberSave> Stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
    public string LastError { get; private set; } = string.Empty;
    public bool Exists => _values.Count > 0;

    public ProjectNumberSave(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        if (!File.Exists(_path)) return;
        try
        {
            if (new FileInfo(_path).Length > 1024 * 1024) throw new InvalidDataException("Save exceeds the numeric slot limit.");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("version", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int format) || format != 1)
            { LastError = "Unsupported numeric save format."; return; }
            if (document.RootElement.TryGetProperty("values", out JsonElement values) && values.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty entry in values.EnumerateObject())
                    if (ValidKey(entry.Name) && entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetDouble(out double number) && double.IsFinite(number) && _values.Count < 256)
                        _values[entry.Name] = number;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidOperationException)
        {
            LastError = ex.Message;
            _values.Clear();
        }
    }
    public static ProjectNumberSave ForProject(string project)
    {
        if (string.IsNullOrWhiteSpace(project)) return null;
        string root = Path.GetFullPath(project);
        if (Stores.TryGetValue(root, out ProjectNumberSave save)) return save;
        string identity = root;
        try
        {
            foreach (string manifest in Directory.EnumerateFiles(root, "*.genesisproj", SearchOption.TopDirectoryOnly))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("projectId", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                { identity = id.GetString(); break; }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) { }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).Substring(0, 24);
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Genesis", "GameSaves", hash);
        save = new ProjectNumberSave(Path.Combine(folder, "campaign.json"));
        Stores[root] = save;
        return save;
    }
    public double Get(string key, double fallback) => key != null && _values.TryGetValue(key, out double result) ? result : fallback;
    public void Set(string key, double value)
    {
        if (ValidKey(key) && double.IsFinite(value) && (_values.Count < 256 || _values.ContainsKey(key))) _values[key] = value;
    }
    public void Clear() => _values.Clear();
    public bool Flush()
    {
        string temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, values = _values }, new JsonSerializerOptions { WriteIndented = true });
            using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temp, _path, overwrite: true);
            LastError = string.Empty; return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        { LastError = ex.Message; return false; }
    }
    private static bool ValidKey(string key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 96;
}
