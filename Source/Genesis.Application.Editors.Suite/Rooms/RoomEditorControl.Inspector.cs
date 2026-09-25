using System.Globalization;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Scene;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Global Inspector bridge for the Room Editor. The Room keeps ownership of undo, dirty state,
/// selection and save boundaries; the Studio Inspector is only another view over that state.
/// </summary>
public sealed partial class RoomEditorControl
{
    public event EventHandler? InspectorStateChanged;

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues()
    {
        List<ResourceInspectorLiveValue> values = [];
        AddRoomValues(values);
        AddEnvironmentValues(values);
        AddViewportValues(values);
        if (TileSelectionValid) AddSelectedTileLiveValues(values);
        else if (_selected is not null && CanInspectNodeInActiveContext(_selected)) AddSelectionValues(values, _selected);
        return values;
    }

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value) =>
        TryApplyInspectorValue(propertyPath, value);

    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        if (string.IsNullOrWhiteSpace(propertyPath)) return false;
        if (propertyPath.StartsWith("Selection.", StringComparison.OrdinalIgnoreCase)
            && (_selected is null || !CanInspectNodeInActiveContext(_selected))) return false;
        try
        {
            bool handled = TryApplyRoomValue(propertyPath, value)
                           || TryApplyEnvironmentValue(propertyPath, value)
                           || TryApplyViewportValue(propertyPath, value)
                           || TryApplySelectedTileLiveValue(propertyPath, value)
                           || TryApplySelectionValue(propertyPath, value);
            if (handled) NotifyRoomInspectorStateChanged();
            return handled;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException
                                           or OverflowException or ArgumentException)
        {
            return false;
        }
    }

    private void AddRoomValues(List<ResourceInspectorLiveValue> values)
    {
        RoomSettings settings = _room.Settings;
        values.Add(new("Room settings", "name", "Name", _room.Name, ReadOnly: true,
            Description: "The resource file owns the room name."));
        values.Add(new("Room settings", "dimension", "Dimension", _room.Dimension.ToString(),
            Choices: Enum.GetNames<RoomDimension>()));
        values.Add(Number("Room settings", "settings.width", "Width", settings.Width, 1, 100_000, 1, 0));
        values.Add(Number("Room settings", "settings.height", "Height", settings.Height, 1, 100_000, 1, 0));
        if (_room.Dimension == RoomDimension.ThreeD)
            values.Add(Number("Room settings", "settings.depth", "Depth", settings.Depth, 1, 100_000, 1, 0));
        values.Add(Number("Room settings", "settings.targetFps", "Frame rate", settings.TargetFps, 0, 1000, 1, 0));
        values.Add(Number("Room settings", "settings.fixedFps", "Physics rate", settings.FixedFps, 1, 1000, 1, 0));
        values.Add(new("Room settings", "settings.persistent", "Persistent", settings.Persistent));
        values.Add(new("Room settings", "settings.vsync", "VSync", settings.VSync));
        values.Add(new("Room settings", "settings.captureMouse", "Mouse capture",
            settings.CaptureMouse switch { true => "Capture", false => "Release", _ => "Automatic" },
            Choices: ["Automatic", "Capture", "Release"]));
        values.Add(new("Room settings", "settings.voxelWorld", "Voxel world", settings.VoxelWorld));
        values.Add(Number("Grid & snapping", "settings.gridSize", "Grid size", settings.GridSize,
            0.01m, 4096m, 0.25m, 2));
        values.Add(new("Grid & snapping", "settings.snapEnabled", "Snap enabled", settings.SnapEnabled));
    }

    private void AddEnvironmentValues(List<ResourceInspectorLiveValue> values)
    {
        RoomEnvironment environment = _room.Environment;
        values.Add(Number("Lighting & atmosphere", "environment.ambientIntensity", "Ambient intensity",
            environment.AmbientIntensity, 0, 8, 0.05m, 2));
        values.Add(Number("Lighting & atmosphere", "environment.fogDensity", "Fog density",
            environment.FogDensity, 0, 1, 0.001m, 3));
        values.Add(new("Lighting & atmosphere", "environment.dynamicSky", "Dynamic sky", environment.DynamicSky));
        values.Add(new("Climate", "environment.weather", "Weather", environment.Weather,
            Choices: Enum.GetNames<WeatherKind>()));
        values.Add(Number("Climate", "environment.timeOfDayHours", "Time of day", environment.TimeOfDayHours,
            0, 24, 0.25m, 2));
        values.Add(Number("Climate", "environment.timeScale", "Time scale", environment.TimeScale,
            0, 10_000, 0.25m, 2));
        values.Add(new("Climate", "environment.automaticWeather", "Automatic weather", environment.AutomaticWeather));
        values.Add(Number("Climate", "environment.climateSeed", "Climate seed", environment.ClimateSeed,
            0, int.MaxValue, 1, 0));
        values.Add(Number("Climate", "environment.dayOfYear", "Day of year", environment.DayOfYear, 1, 366, 1, 0));
        values.Add(Number("Climate", "environment.latitudeDegrees", "Latitude", environment.LatitudeDegrees,
            -90, 90, 0.1m, 1));
        values.Add(new("Lighting & atmosphere", "environment.atmospherePreset", "Atmosphere preset",
            environment.AtmospherePreset, Choices: Enum.GetNames<AtmospherePreset>()));
        values.Add(new("Lighting & atmosphere", "environment.volumetricClouds", "Volumetric clouds",
            environment.VolumetricClouds));
        values.Add(Number("Lighting & atmosphere", "environment.cloudQuality", "Cloud quality",
            environment.CloudQuality, 0, 4, 1, 0));
        values.Add(Number("Lighting & atmosphere", "environment.cloudBaseHeight", "Cloud base",
            environment.CloudBaseHeight, 0, 20_000, 5, 1));
        values.Add(Number("Lighting & atmosphere", "environment.cloudThickness", "Cloud thickness",
            environment.CloudThickness, 0, 20_000, 5, 1));
        values.Add(Number("Lighting & atmosphere", "environment.cloudCoverageScale", "Cloud coverage",
            environment.CloudCoverageScale, 0, 4, 0.01m, 2));
        values.Add(Number("Lighting & atmosphere", "environment.atmosphericHaze", "Atmospheric haze",
            environment.AtmosphericHaze, 0, 1, 0.01m, 2));
        AddAudio(values, "Wind", "environment.windAudio", environment.WindAudio);
        AddAudio(values, "Rain", "environment.rainAudio", environment.RainAudio);
        AddAudio(values, "Water", "environment.waterAudio", environment.WaterAudio);
        AddAudio(values, "Fire", "environment.fireAudio", environment.FireAudio);
        AddAudio(values, "Wildlife", "environment.wildlifeAudio", environment.WildlifeAudio);
        AddAudio(values, "Night", "environment.nightAudio", environment.NightAudio);
        RoomSoundscapeLevels levels = environment.SoundscapeLevels ?? new();
        foreach ((string name, float level) in new[] { ("Master", levels.Master), ("Wind", levels.Wind),
                     ("Rain", levels.Rain), ("Water", levels.Water), ("Fire", levels.Fire), ("Wildlife", levels.Wildlife), ("Night", levels.Night) })
            values.Add(Number("Soundscape", "environment.soundscapeLevels." + name, name + " level",
                RoomSoundscapeLevels.Sanitize(level), 0, 1, 0.05m, 2));
    }

    private void AddViewportValues(List<ResourceInspectorLiveValue> values)
    {
        if (ActiveViewport is not { } viewport) return;
        string group = $"Viewport {_activeViewport + 1}";
        values.Add(new(group, "Editor.ActiveViewport", "Active viewport", $"Viewport {_activeViewport + 1}",
            Choices: Enumerable.Range(1, RoomAsset.MaxViewports).Select(index => $"Viewport {index}").ToArray()));
        string root = $"viewports[{_activeViewport}]";
        values.Add(new(group, root + ".enabled", "Enabled", viewport.Enabled));
        values.Add(Number(group, root + ".sourceX", "Source X", viewport.SourceX, -1_000_000, 1_000_000, 1, 2));
        values.Add(Number(group, root + ".sourceY", "Source Y", viewport.SourceY, -1_000_000, 1_000_000, 1, 2));
        if (_room.Dimension == RoomDimension.ThreeD)
            values.Add(Number(group, root + ".sourceZ", "Source Z", viewport.SourceZ, -1_000_000, 1_000_000, 1, 2));
        values.Add(Number(group, root + ".sourceWidth", "Source width", viewport.SourceWidth, 1, 100_000, 1, 1));
        values.Add(Number(group, root + ".sourceHeight", "Source height", viewport.SourceHeight, 1, 100_000, 1, 1));
        values.Add(Number(group, root + ".portX", "Port X", viewport.PortX, 0, 100_000, 1, 0));
        values.Add(Number(group, root + ".portY", "Port Y", viewport.PortY, 0, 100_000, 1, 0));
        values.Add(Number(group, root + ".portWidth", "Port width", viewport.PortWidth, 1, 100_000, 1, 0));
        values.Add(Number(group, root + ".portHeight", "Port height", viewport.PortHeight, 1, 100_000, 1, 0));
        values.Add(Number(group, root + ".frustumNear", "Near plane", viewport.FrustumNear, 0.01m, 10_000, 0.01m, 2));
        values.Add(Number(group, root + ".frustumFar", "Far plane", viewport.FrustumFar, 1, 100_000, 1, 1));
        values.Add(Number(group, root + ".fieldOfView", "Field of view", viewport.FieldOfView, 1, 179, 1, 1));
        values.Add(new(group, root + ".followTarget", "Follow target", viewport.FollowTarget,
            Choices: ["", .. _room.Nodes.Where(node => node.Kind == RoomNodeKind.GameObject).Select(node => node.Name)]));
        values.Add(Number(group, root + ".followMarginX", "Follow margin X", viewport.FollowMarginX, 0, 100_000, 1, 1));
        values.Add(Number(group, root + ".followMarginY", "Follow margin Y", viewport.FollowMarginY, 0, 100_000, 1, 1));
        values.Add(Number(group, root + ".followSpeedX", "Follow speed X", viewport.FollowSpeedX, -1, 100_000, 1, 1));
        values.Add(Number(group, root + ".followSpeedY", "Follow speed Y", viewport.FollowSpeedY, -1, 100_000, 1, 1));
        values.Add(new(group, root + ".editorVisible", "Show in editor", viewport.EditorVisible));
        values.Add(new(group, root + ".editorFillStyle", "Display style", viewport.EditorFillStyle.ToString(),
            Choices: Enum.GetNames<RoomViewportFillStyle>()));
    }

    private void AddSelectionValues(List<ResourceInspectorLiveValue> values, RoomNode node)
    {
        string instance = $"{node.Name} · Instance";
        values.Add(new(instance, "Selection.Kind", "Kind", node.Kind.ToString(), ReadOnly: true));
        values.Add(new(instance, "Selection.Name", "Name", node.Name));
        values.Add(new(instance, "Selection.Enabled", "Enabled", node.Enabled));
        values.Add(new(instance, "Selection.Layer", "Layer", LayerFor(node)?.Name ?? string.Empty,
            Choices: _room.Layers.Select(layer => layer.Name).ToArray()));
        values.Add(new(instance, "Selection.Parent", "Parent",
            _room.Nodes.FirstOrDefault(candidate => candidate.Id == node.ParentId)?.Name ?? "(none)",
            Choices: ["(none)", .. _room.Nodes.Where(candidate => !ReferenceEquals(candidate, node) && !WouldCycle(node, candidate)).Select(candidate => candidate.Name)]));
        values.Add(new(instance, "Selection.Visible2D", "Visible in 2D", node.EnabledIn2D));
        values.Add(new(instance, "Selection.Visible3D", "Visible in 3D", node.EnabledIn3D));
        values.Add(Number(instance, "Selection.Depth", "Depth", NodeDepth(node), -100_000, 100_000, 1, 0));

        string transform = $"{node.Name} · Transform";
        values.Add(Number(transform, "Selection.Transform.Position.X", "Position X", node.Transform.X, -1_000_000, 1_000_000, 0.1m, 3));
        values.Add(Number(transform, "Selection.Transform.Position.Y", "Position Y", node.Transform.Y, -1_000_000, 1_000_000, 0.1m, 3));
        if (_room.Dimension == RoomDimension.ThreeD)
            values.Add(Number(transform, "Selection.Transform.Position.Z", "Position Z", node.Transform.Z, -1_000_000, 1_000_000, 0.1m, 3));
        values.Add(Number(transform, "Selection.Transform.Rotation.X", "Rotation X", node.Transform.RotationX, -360, 360, 1, 2));
        values.Add(Number(transform, "Selection.Transform.Rotation.Y", "Rotation Y", node.Transform.RotationY, -360, 360, 1, 2));
        values.Add(Number(transform, "Selection.Transform.Rotation.Z", "Rotation Z", node.Transform.RotationZ, -360, 360, 1, 2));
        values.Add(Number(transform, "Selection.Transform.Scale.X", "Scale X", node.Transform.ScaleX, 0.001m, 10_000, 0.05m, 3));
        values.Add(Number(transform, "Selection.Transform.Scale.Y", "Scale Y", node.Transform.ScaleY, 0.001m, 10_000, 0.05m, 3));
        if (_room.Dimension == RoomDimension.ThreeD)
            values.Add(Number(transform, "Selection.Transform.Scale.Z", "Scale Z", node.Transform.ScaleZ, 0.001m, 10_000, 0.05m, 3));

        AddKindSpecificSelection(values, node);
        AddComponentOverrides(values, node);
    }

    private void AddKindSpecificSelection(List<ResourceInspectorLiveValue> values, RoomNode node)
    {
        string group = $"{node.Name} · {node.Kind}";
        if (node.GameObject is { } gameObject)
        {
            values.Add(new(group, "Selection.GameObject.Prefab", "Object", gameObject.Prefab,
                AssetKind: ResourceKind.GameObject));
        }
        else if (node.Background is { } background)
        {
            values.Add(new(group, "Selection.Background.Asset", "Image", background.Asset, AssetKind: ResourceKind.Image));
            values.Add(new(group, "Selection.Background.Mode", "Mode", background.Mode.ToString(), Choices: Enum.GetNames<RoomBackgroundMode>()));
            values.Add(new(group, "Selection.Background.Layout", "Layout", background.Layout.ToString(), Choices: Enum.GetNames<RoomBackgroundLayout>()));
            values.Add(Number(group, "Selection.Background.ScrollX", "Scroll X", background.Scroll.ElementAtOrDefault(0), -10_000, 10_000, 0.05m, 3));
            values.Add(Number(group, "Selection.Background.ScrollY", "Scroll Y", background.Scroll.ElementAtOrDefault(1), -10_000, 10_000, 0.05m, 3));
            values.Add(new(group, "Selection.Background.RepeatX", "Repeat X", background.RepeatX));
            values.Add(new(group, "Selection.Background.RepeatY", "Repeat Y", background.RepeatY));
            values.Add(new(group, "Selection.Background.DepthTest", "Depth test", background.DepthTest));
            values.Add(Number(group, "Selection.Background.Opacity", "Opacity", background.Opacity, 0, 1, 0.01m, 2));
        }
        else if (node.TileLayer is { } tiles)
        {
            values.Add(new(group, "Selection.TileLayer.Tileset", "Tile set", tiles.Tileset, AssetKind: ResourceKind.Image));
            values.Add(Number(group, "Selection.TileLayer.CellWidth", "Cell width", tiles.CellWidth, 1, 4096, 1, 0));
            values.Add(Number(group, "Selection.TileLayer.CellHeight", "Cell height", tiles.CellHeight, 1, 4096, 1, 0));
            values.Add(Number(group, "Selection.TileLayer.Margin", "Margin", tiles.Margin, 0, 4096, 1, 0));
            values.Add(Number(group, "Selection.TileLayer.Separation", "Separation", tiles.Separation, 0, 4096, 1, 0));
            values.Add(new(group, "Selection.TileLayer.CollisionEnabled", "Collision", tiles.CollisionEnabled));
            values.Add(new(group, "Selection.TileLayer.PaintedCells", "Painted cells", tiles.Cells.Count, ReadOnly: true));
        }
        else if (node.Terrain is { } terrain)
        {
            values.Add(new(group, "Selection.Terrain.Asset", "Terrain", terrain.Asset, AssetKind: ResourceKind.Terrain));
            values.Add(new(group, "Selection.Terrain.Material", "Material image", terrain.Material, AssetKind: ResourceKind.Image));
            values.Add(new(group, "Selection.Terrain.Albedo", "Albedo", terrain.Albedo, AssetKind: ResourceKind.Image));
            values.Add(Number(group, "Selection.Terrain.UvScale", "UV scale", terrain.UvScale, 0.01m, 10_000, 0.05m, 3));
        }
    }

    private void AddComponentOverrides(List<ResourceInspectorLiveValue> values, RoomNode node)
    {
        if (node.GameObject is not { } gameObject) return;
        for (int index = 0; index < gameObject.ComponentOverrides.Count; index++)
        {
            RoomComponentOverride component = gameObject.ComponentOverrides[index];
            string group = $"{node.Name} · {Humanize(component.ComponentId)}";
            string root = $"Selection.Overrides[{index}]";
            values.Add(new(group, root + ".Enabled", "Enabled", component.Enabled switch
            {
                true => "Enabled",
                false => "Disabled",
                _ => "Inherit",
            }, Choices: ["Inherit", "Enabled", "Disabled"]));
            foreach ((string property, JToken token) in component.Properties)
            {
                if (token is not JValue scalar || scalar.Value is null || !Inspectable(scalar.Value)) continue;
                values.Add(new(group, root + ".Properties." + property, Humanize(property), scalar.Value,
                    AssetKind: OverrideAssetKind(property)));
            }
        }
    }

    private bool TryApplyRoomValue(string path, object? value)
    {
        if (path.Equals("dimension", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), true, out RoomDimension dimension))
        {
            ViewMode3D = dimension == RoomDimension.ThreeD;
            SyncRoomSettings();
            return true;
        }
        if (!path.StartsWith("settings.", StringComparison.OrdinalIgnoreCase)) return false;
        return SetRoomSettingsValue(path["settings.".Length..], value);
    }

    private bool SetRoomSettingsValue(string name, object? value)
    {
        RoomSettings previous = CloneSettings(_room.Settings);
        RoomSettings next = CloneSettings(_room.Settings);
        switch (name.ToLowerInvariant())
        {
            case "width": next.Width = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "height": next.Height = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "depth": next.Depth = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "targetfps": next.TargetFps = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "fixedfps": next.FixedFps = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "persistent": next.Persistent = Convert.ToBoolean(value, CultureInfo.InvariantCulture); break;
            case "vsync": next.VSync = Convert.ToBoolean(value, CultureInfo.InvariantCulture); break;
            case "capturemouse":
                next.CaptureMouse = Convert.ToString(value, CultureInfo.InvariantCulture)?.ToLowerInvariant() switch
                {
                    "capture" => true,
                    "release" => false,
                    _ => null,
                };
                break;
            case "voxelworld": next.VoxelWorld = Convert.ToBoolean(value, CultureInfo.InvariantCulture); break;
            case "gridsize": next.GridSize = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "snapenabled": next.SnapEnabled = Convert.ToBoolean(value, CultureInfo.InvariantCulture); break;
            default: return false;
        }
        next.Normalize();
        if (SettingsEqual(previous, next)) return true;
        ApplySettings(next);
        PushEdit("Room settings", () => ApplySettings(next), () => ApplySettings(previous));
        return true;
    }

    private bool TryApplyEnvironmentValue(string path, object? value)
    {
        const string prefix = "environment.";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string name = path[prefix.Length..].ToLowerInvariant();
        RoomEnvironment previous = CloneEnvironment(_room.Environment);
        RoomEnvironment next = CloneEnvironment(_room.Environment);
        switch (name)
        {
            case "ambientintensity": next.AmbientIntensity = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "fogdensity": next.FogDensity = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "dynamicsky": next.DynamicSky = Convert.ToBoolean(value, CultureInfo.InvariantCulture); break;
            case "weather": next.Weather = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Clear"; break;
            case "timeofdayhours": next.TimeOfDayHours = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "timescale": next.TimeScale = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "automaticweather": next.AutomaticWeather = Convert.ToBoolean(value, CultureInfo.InvariantCulture); break;
            case "climateseed": next.ClimateSeed = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "dayofyear": next.DayOfYear = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "latitudedegrees": next.LatitudeDegrees = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "atmospherepreset": next.AtmospherePreset = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Natural"; break;
            case "volumetricclouds": next.VolumetricClouds = Convert.ToBoolean(value, CultureInfo.InvariantCulture); break;
            case "cloudquality": next.CloudQuality = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "cloudbaseheight": next.CloudBaseHeight = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "cloudthickness": next.CloudThickness = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "cloudcoveragescale": next.CloudCoverageScale = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "atmospherichaze": next.AtmosphericHaze = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "windaudio": next.WindAudio = InspectorText(value); break;
            case "rainaudio": next.RainAudio = InspectorText(value); break;
            case "wateraudio": next.WaterAudio = InspectorText(value); break;
            case "fireaudio": next.FireAudio = InspectorText(value); break;
            case "wildlifeaudio": next.WildlifeAudio = InspectorText(value); break;
            case "nightaudio": next.NightAudio = InspectorText(value); break;
            case "soundscapelevels.master": next.SoundscapeLevels.Master = RoomSoundscapeLevels.Sanitize(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
            case "soundscapelevels.wind": next.SoundscapeLevels.Wind = RoomSoundscapeLevels.Sanitize(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
            case "soundscapelevels.rain": next.SoundscapeLevels.Rain = RoomSoundscapeLevels.Sanitize(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
            case "soundscapelevels.water": next.SoundscapeLevels.Water = RoomSoundscapeLevels.Sanitize(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
            case "soundscapelevels.fire": next.SoundscapeLevels.Fire = RoomSoundscapeLevels.Sanitize(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
            case "soundscapelevels.wildlife": next.SoundscapeLevels.Wildlife = RoomSoundscapeLevels.Sanitize(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
            case "soundscapelevels.night": next.SoundscapeLevels.Night = RoomSoundscapeLevels.Sanitize(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
            default: return false;
        }
        ApplyEnvironment(next);
        PushEdit("Room environment", () => ApplyEnvironment(next), () => ApplyEnvironment(previous));
        return true;
    }

    private bool TryApplyViewportValue(string path, object? value)
    {
        if (path.Equals("Editor.ActiveViewport", StringComparison.OrdinalIgnoreCase))
        {
            Match selected = Regex.Match(InspectorText(value), @"(\d+)$");
            if (!selected.Success) return false;
            SelectViewport(Math.Clamp(int.Parse(selected.Groups[1].Value, CultureInfo.InvariantCulture) - 1,
                0, RoomAsset.MaxViewports - 1));
            return true;
        }
        Match match = Regex.Match(path, @"^viewports\[(?<index>\d+)\]\.(?<name>\w+)$", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["index"].Value, out int index)
            || index < 0 || index >= _room.Viewports.Count) return false;
        RoomViewport previous = _room.Viewports[index].Clone();
        RoomViewport next = _room.Viewports[index].Clone();
        switch (match.Groups["name"].Value.ToLowerInvariant())
        {
            case "enabled": next.Enabled = Convert.ToBoolean(value, CultureInfo.InvariantCulture); break;
            case "sourcex": next.SourceX = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "sourcey": next.SourceY = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "sourcez": next.SourceZ = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "sourcewidth": next.SourceWidth = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "sourceheight": next.SourceHeight = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "portx": next.PortX = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "porty": next.PortY = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "portwidth": next.PortWidth = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "portheight": next.PortHeight = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "frustumnear": next.FrustumNear = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "frustumfar": next.FrustumFar = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "fieldofview": next.FieldOfView = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "followtarget": next.FollowTarget = InspectorText(value); break;
            case "followmarginx": next.FollowMarginX = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "followmarginy": next.FollowMarginY = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "followspeedx": next.FollowSpeedX = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "followspeedy": next.FollowSpeedY = Convert.ToSingle(value, CultureInfo.InvariantCulture); break;
            case "editorvisible": next.EditorVisible = Convert.ToBoolean(value, CultureInfo.InvariantCulture); break;
            case "editorfillstyle" when Enum.TryParse(InspectorText(value), true, out RoomViewportFillStyle fill): next.EditorFillStyle = fill; break;
            default: return false;
        }
        next.Normalize();
        ApplyViewport(index, next);
        PushEdit($"Viewport {index + 1}", () => ApplyViewport(index, next), () => ApplyViewport(index, previous));
        return true;
    }

    private bool TryApplySelectionValue(string path, object? value)
    {
        RoomNode? node = _selected;
        if (node is null) return false;
        if (path.Equals("Selection.Name", StringComparison.OrdinalIgnoreCase)) return SetNodeName(node, InspectorText(value));
        if (path.Equals("Selection.Enabled", StringComparison.OrdinalIgnoreCase)) return SetNodeEnabled(node, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
        if (path.Equals("Selection.Layer", StringComparison.OrdinalIgnoreCase))
        {
            RoomLayer? layer = _room.Layers.FirstOrDefault(candidate => candidate.Name.Equals(InspectorText(value), StringComparison.OrdinalIgnoreCase));
            return layer is not null && SetSelectionLayer(layer);
        }
        if (path.Equals("Selection.Parent", StringComparison.OrdinalIgnoreCase))
        {
            string name = InspectorText(value);
            RoomNode? parent = name.Equals("(none)", StringComparison.OrdinalIgnoreCase)
                ? null
                : _room.Nodes.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return SetNodeParent(node, parent);
        }
        if (path.Equals("Selection.Visible2D", StringComparison.OrdinalIgnoreCase)) return SetNodeDimensionVisibility(node, RoomDimension.TwoD, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
        if (path.Equals("Selection.Visible3D", StringComparison.OrdinalIgnoreCase)) return SetNodeDimensionVisibility(node, RoomDimension.ThreeD, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
        if (path.Equals("Selection.Depth", StringComparison.OrdinalIgnoreCase)) return SetNodeDepth(node, Convert.ToInt32(value, CultureInfo.InvariantCulture));
        if (TryApplyTransform(node, path, value)) return true;
        if (TryApplyKindSpecific(node, path, value)) return true;
        return TryApplyOverride(node, path, value);
    }

    internal bool TryApplyTransform(RoomNode node, string path, object? value)
    {
        if (!path.StartsWith("Selection.Transform.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        float converted = Convert.ToSingle(value, CultureInfo.InvariantCulture);
        return path.ToLowerInvariant() switch
        {
            "selection.transform.position.x" => SetNodeValue(node, "Position X", () => node.Transform.X, next => node.Transform.X = next, converted),
            "selection.transform.position.y" => SetNodeValue(node, "Position Y", () => node.Transform.Y, next => node.Transform.Y = next, converted),
            "selection.transform.position.z" => SetNodeValue(node, "Position Z", () => node.Transform.Z, next => node.Transform.Z = next, converted),
            "selection.transform.rotation.x" => SetNodeValue(node, "Rotation X", () => node.Transform.RotationX, next => node.Transform.RotationX = next, converted),
            "selection.transform.rotation.y" => SetNodeValue(node, "Rotation Y", () => node.Transform.RotationY, next => node.Transform.RotationY = next, converted),
            "selection.transform.rotation.z" => SetNodeValue(node, "Rotation Z", () => node.Transform.RotationZ, next => node.Transform.RotationZ = next, converted),
            "selection.transform.scale.x" => SetNodeValue(node, "Scale X", () => node.Transform.ScaleX, next => node.Transform.ScaleX = next, Math.Max(0.001f, converted)),
            "selection.transform.scale.y" => SetNodeValue(node, "Scale Y", () => node.Transform.ScaleY, next => node.Transform.ScaleY = next, Math.Max(0.001f, converted)),
            "selection.transform.scale.z" => SetNodeValue(node, "Scale Z", () => node.Transform.ScaleZ, next => node.Transform.ScaleZ = next, Math.Max(0.001f, converted)),
            _ => false,
        };
    }

    private bool TryApplyKindSpecific(RoomNode node, string path, object? value)
    {
        string text = InspectorText(value);
        if (node.GameObject is { } gameObject && path.Equals("Selection.GameObject.Prefab", StringComparison.OrdinalIgnoreCase))
            return SetNodeValue(node, "Object resource", () => gameObject.Prefab, next => gameObject.Prefab = next, text);
        if (node.Background is { } background)
        {
            if (path.Equals("Selection.Background.Asset", StringComparison.OrdinalIgnoreCase)) return SetNodeValue(node, "Background image", () => background.Asset, next => background.Asset = next, text);
            if (path.Equals("Selection.Background.Mode", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(text, true, out RoomBackgroundMode mode)) return SetBackgroundMode(node, mode);
            if (path.Equals("Selection.Background.Layout", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(text, true, out RoomBackgroundLayout layout)) return SetBackgroundLayout(node, layout);
            if (path.Equals("Selection.Background.ScrollX", StringComparison.OrdinalIgnoreCase)) { SetBackgroundScroll(node, Convert.ToSingle(value, CultureInfo.InvariantCulture), background.Scroll.ElementAtOrDefault(1)); return true; }
            if (path.Equals("Selection.Background.ScrollY", StringComparison.OrdinalIgnoreCase)) { SetBackgroundScroll(node, background.Scroll.ElementAtOrDefault(0), Convert.ToSingle(value, CultureInfo.InvariantCulture)); return true; }
            if (path.Equals("Selection.Background.RepeatX", StringComparison.OrdinalIgnoreCase)) return SetBackgroundRepeat(node, Convert.ToBoolean(value, CultureInfo.InvariantCulture), background.RepeatY);
            if (path.Equals("Selection.Background.RepeatY", StringComparison.OrdinalIgnoreCase)) return SetBackgroundRepeat(node, background.RepeatX, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
            if (path.Equals("Selection.Background.DepthTest", StringComparison.OrdinalIgnoreCase)) return SetNodeValue(node, "Background depth test", () => background.DepthTest, next => background.DepthTest = next, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
            if (path.Equals("Selection.Background.Opacity", StringComparison.OrdinalIgnoreCase)) return SetBackgroundOpacity(node, Convert.ToSingle(value, CultureInfo.InvariantCulture));
        }
        if (node.TileLayer is { } tiles)
        {
            if (path.Equals("Selection.TileLayer.Tileset", StringComparison.OrdinalIgnoreCase)) return SetNodeValue(node, "Tile set", () => tiles.Tileset, next => tiles.Tileset = next, text);
            if (path.Equals("Selection.TileLayer.CellWidth", StringComparison.OrdinalIgnoreCase)) return SetTileLayerCellSize(node, Convert.ToInt32(value, CultureInfo.InvariantCulture), tiles.CellHeight);
            if (path.Equals("Selection.TileLayer.CellHeight", StringComparison.OrdinalIgnoreCase)) return SetTileLayerCellSize(node, tiles.CellWidth, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            if (path.Equals("Selection.TileLayer.Margin", StringComparison.OrdinalIgnoreCase)) return SetNodeValue(node, "Tile margin", () => tiles.Margin, next => tiles.Margin = next, Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture)));
            if (path.Equals("Selection.TileLayer.Separation", StringComparison.OrdinalIgnoreCase)) return SetNodeValue(node, "Tile separation", () => tiles.Separation, next => tiles.Separation = next, Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture)));
            if (path.Equals("Selection.TileLayer.CollisionEnabled", StringComparison.OrdinalIgnoreCase)) return SetTileLayerCollision(node, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
        }
        if (node.Terrain is { } terrain)
        {
            if (path.Equals("Selection.Terrain.Asset", StringComparison.OrdinalIgnoreCase)) return SetNodeValue(node, "Terrain resource", () => terrain.Asset, next => terrain.Asset = next, text);
            if (path.Equals("Selection.Terrain.Material", StringComparison.OrdinalIgnoreCase)) return SetNodeValue(node, "Terrain material", () => terrain.Material, next => terrain.Material = next, text);
            if (path.Equals("Selection.Terrain.Albedo", StringComparison.OrdinalIgnoreCase)) return SetNodeValue(node, "Terrain albedo", () => terrain.Albedo, next => terrain.Albedo = next, text);
            if (path.Equals("Selection.Terrain.UvScale", StringComparison.OrdinalIgnoreCase)) return SetNodeValue(node, "Terrain UV scale", () => terrain.UvScale, next => terrain.UvScale = next, Math.Clamp(Convert.ToSingle(value, CultureInfo.InvariantCulture), 0.01f, 10_000f));
        }
        return false;
    }

    private bool TryApplyOverride(RoomNode node, string path, object? value)
    {
        if (node.GameObject is not { } gameObject || IsNodeLocked(node)) return false;
        Match match = Regex.Match(path,
            @"^Selection\.Overrides\[(?<index>\d+)\]\.(?<tail>.+)$",
            RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["index"].Value, out int index)
            || index < 0 || index >= gameObject.ComponentOverrides.Count) return false;
        RoomComponentOverride component = gameObject.ComponentOverrides[index];
        string tail = match.Groups["tail"].Value;
        if (tail.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
        {
            bool? next = InspectorText(value).ToLowerInvariant() switch
            {
                "enabled" => true,
                "disabled" => false,
                _ => null,
            };
            return SetNodeValue(node, "Component override", () => component.Enabled,
                changed => component.Enabled = changed, next);
        }
        const string propertyPrefix = "Properties.";
        if (!tail.StartsWith(propertyPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string property = tail[propertyPrefix.Length..];
        if (!component.Properties.TryGetValue(property, out JToken? current) || current is not JValue scalar) return false;
        JToken before = scalar.DeepClone();
        JToken after = ConvertToken(value, scalar.Type);
        if (JToken.DeepEquals(before, after)) return true;
        component.Properties[property] = after;
        PushEdit($"Override {property}",
            () => { component.Properties[property] = after.DeepClone(); RefreshPhase4Ui(); },
            () => { component.Properties[property] = before.DeepClone(); RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    private void NotifyRoomInspectorStateChanged()
    {
        QueueRoomUiRefresh();
    }

    private static ResourceInspectorLiveValue Number(
        string group, string path, string label, object value,
        decimal minimum, decimal maximum, decimal increment, int places) =>
        new(group, path, label, value, Minimum: minimum, Maximum: maximum,
            Increment: increment, DecimalPlaces: places);

    private static void AddAudio(List<ResourceInspectorLiveValue> values, string label, string path, string value) =>
        values.Add(new("Environment audio", path, label, value ?? string.Empty, AssetKind: ResourceKind.Audio));

    private static string InspectorText(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Humanize(string value) => Regex.Replace(value ?? string.Empty,
        "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ');

    private static bool Inspectable(object value) => value is bool or string or byte or sbyte
        or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static ResourceKind? OverrideAssetKind(string property) => property.ToLowerInvariant() switch
    {
        "sprite" or "image" or "albedo" or "normalmap" or "texture" => ResourceKind.Image,
        "model" => ResourceKind.Model,
        "shader" => ResourceKind.Shader,
        "audio" or "sound" => ResourceKind.Audio,
        "particle" => ResourceKind.Particle,
        "terrain" => ResourceKind.Terrain,
        _ => null,
    };

    private static JToken ConvertToken(object? value, JTokenType type) => type switch
    {
        JTokenType.Boolean => new JValue(Convert.ToBoolean(value, CultureInfo.InvariantCulture)),
        JTokenType.Integer => new JValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        JTokenType.Float => new JValue(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        _ => new JValue(InspectorText(value)),
    };
}
