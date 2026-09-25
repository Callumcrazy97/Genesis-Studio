using System.Text.Json;

namespace Genesis.Application.Core.Settings;

public sealed class SettingsService
{
    private readonly string _settingsFile;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public SettingsService(string? settingsFile = null)
    {
        _settingsFile = settingsFile ?? ApplicationPaths.SettingsFile;
        Current = LoadFromDisk();
    }

    public GenesisSettings Current { get; private set; }

    public string? MigrationNotice { get; private set; }

    public event EventHandler? SettingsChanged;

    public void Update(Action<GenesisSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        update(Current);
        Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Replace(GenesisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings;
        Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        Current = new GenesisSettings();
        Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddRecentProject(string projectName, string projectFile)
    {
        string normalized = Path.GetFullPath(projectFile);
        Current.RecentProjects.RemoveAll(
            entry => string.Equals(
                Path.GetFullPath(entry.ProjectFile),
                normalized,
                StringComparison.OrdinalIgnoreCase));

        Current.RecentProjects.Insert(
            0,
            new RecentProject
            {
                Name = projectName,
                ProjectFile = normalized,
                LastOpenedUtc = DateTime.UtcNow,
            });

        List<RecentProject> pinned = Current.RecentProjects.Where(item => item.IsPinned).ToList();
        List<RecentProject> unpinned = Current.RecentProjects
            .Where(item => !item.IsPinned)
            .Take(12)
            .ToList();

        Current.RecentProjects = [.. pinned, .. unpinned];
        Save();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveMissingRecentProjects()
    {
        int removed = Current.RecentProjects.RemoveAll(
            item => !File.Exists(item.ProjectFile));

        if (removed > 0)
        {
            Save();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Save()
    {
        string? directory = Path.GetDirectoryName(_settingsFile);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The preferences file must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        Current.SchemaVersion = GenesisSettings.CurrentSchemaVersion;
        string temporaryFile = _settingsFile + ".tmp";
        string json = JsonSerializer.Serialize(Current, _jsonOptions);
        File.WriteAllText(temporaryFile, json);
        File.Move(temporaryFile, _settingsFile, true);
    }

    private GenesisSettings LoadFromDisk()
    {
        if (!File.Exists(_settingsFile))
        {
            return new GenesisSettings();
        }

        try
        {
            string json = File.ReadAllText(_settingsFile);
            GenesisSettings? settings = JsonSerializer.Deserialize<GenesisSettings>(json, _jsonOptions);
            if (settings is null)
            {
                return new GenesisSettings();
            }

            // Missing properties from older schemas retain their initialisers, but explicit nulls
            // still need normalising before Preferences/Studio consume them.
            settings.General ??= new GeneralSettings();
            settings.Appearance ??= new AppearanceSettings();
            settings.Editing ??= new EditingSettings();
            settings.Runtime ??= new RuntimeSettings();
            settings.Rendering ??= new RenderingSettings();
            if (settings.SchemaVersion < 3 && string.Equals(settings.Rendering.FrontFaceWinding, "Clockwise", StringComparison.OrdinalIgnoreCase))
            {
                settings.Rendering.FrontFaceWinding = "CounterClockwise";
                MigrationNotice = "The old clockwise rendering default was corrected to counter-clockwise so model exteriors render consistently. Per-model overrides are unchanged.";
            }
            if (Genesis.Rendering.Core.RenderBackendCatalog.IsRetiredValue(settings.Rendering.Backend))
            {
                MigrationNotice = $"The saved renderer '{settings.Rendering.Backend}' has been removed. Genesis now uses Direct3D 11. You can choose another renderer in Preferences.";
                settings.Rendering.Backend = "Direct3D11";
            }
            settings.Shortcuts ??= new ShortcutSettings();
            settings.RecentProjects ??= [];

            // Theme used to be one untyped string. A missing mode must remain inferential or a
            // v1 value such as "Cosmic Nebula" would deserialize to the new Colour default and
            // silently lose its backdrop on upgrade. Presence is checked instead of schema alone
            // so hand-authored and early preview files migrate safely too.
            if (!HasAppearanceThemeMode(json))
            {
                settings.Appearance.ThemeMode = AppearanceThemeModes.Automatic;
            }

            settings.SchemaVersion = GenesisSettings.CurrentSchemaVersion;
            return settings;
        }
        catch (JsonException)
        {
            string backup = _settingsFile + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_settingsFile, backup, false);
            return new GenesisSettings();
        }
    }

    private static bool HasAppearanceThemeMode(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!TryGetProperty(document.RootElement, "appearance", out JsonElement appearance)
            || appearance.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryGetProperty(appearance, "themeMode", out _);

        static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}
