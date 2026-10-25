#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Genesis.Shared.Commands;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// The one standard way a script reaches the engine: <c>Engine.&lt;Area&gt;.&lt;Name&gt;</c>.
/// <list type="bullet">
/// <item>Every command is also <c>Engine.Area.Name(...)</c>: its area comes from its category
/// (Physics, Audio, Model, Image...) and its name loses the area's own word, so PhysicsApplyForce is
/// <c>Engine.Physics.ApplyForce</c>, DsListAdd is <c>Engine.List.Add</c> and SpriteSetFrame is
/// <c>Engine.Image.SetFrame</c>.</item>
/// <item>Every value is a property: where an area has a Get/Set (or Is/Set) pair, the value's own
/// name reads and sets it, so <c>Engine.Physics.AirDrag</c> reads the air drag and
/// <c>Engine.Physics.AirDrag = 0.2;</c> sets it (<c>+=</c> and the rest work too). A value of an
/// instance (Engine.Physics.Mass, Engine.Image.Frame) is the current instance's: its own in its
/// events, each target's in turn inside <c>with</c>.</item>
/// </list>
/// The flat names (PhysicsApplyForce) and the older qualified ones (Engine.Rendering.Decals.DecalAdd)
/// keep working; this adds names, it never takes one away. A command or property written with an
/// explicit namespace in its attribute (Engine.Sky.Coverage) is already standard and is left as it is.
/// </summary>
public static class PgslApi
{
    /// <summary>One name of the standard: a command, or a property made from a Get/Set pair.</summary>
    public sealed class Entry
    {
        /// <summary>The full name, Engine.Physics.ApplyForce.</summary>
        public string Name = "";
        /// <summary>Physics, Audio...</summary>
        public string Area = "";
        /// <summary>ApplyForce, AirDrag...</summary>
        public string Member = "";
        /// <summary>The command this name is another name for (PhysicsApplyForce); null for a property made from a pair.</summary>
        public string? Command;
        public bool IsProperty;
        /// <summary>For a property made from a pair: the commands that read and set it (either may be missing).</summary>
        public string? GetterCommand, SetterCommand;
        public string? GetterMember, SetterMember;
        public string Category = "";
        public string Description = "";
        public string Signature = "";
    }

    private sealed record Area(string Name, string[] Prefixes);

    // Each area and the words its commands' names start with, which its names drop (longest first).
    private static readonly Area[] Areas =
    {
        new("Physics", new[] { "Physics" }), new("Audio", new[] { "Audio" }), new("Model", new[] { "Model" }),
        new("Image", new[] { "Sprite" }), new("Texture", new[] { "Texture" }), new("Animation", new[] { "Animation" }),
        new("Draw", new[] { "Draw" }), new("Input", new[] { "Input" }), new("Network", new[] { "Net" }),
        new("Instance", new[] { "Instance" }), new("Lighting", new[] { "LightEmitter", "Environment", "Light" }), new("Sky", new[] { "Sky" }),
        new("Rendering", new[] { "Render" }), new("PostEffect", new[] { "PostEffect" }), new("Text", new[] { "String" }),
        new("Language", new[] { "Language" }), new("List", new[] { "DsList" }), new("NumList", new[] { "NumList" }),
        new("Array", new[] { "PgList", "Array" }), new("Map", new[] { "DsMap" }), new("Grid", new[] { "DsGrid" }),
        new("Stack", new[] { "DsStack" }), new("Queue", new[] { "DsQueue" }), new("Priority", new[] { "DsPriority" }),
        new("Data", new[] { "Ds" }), new("Json", new[] { "Json" }), new("Math", Array.Empty<string>()),
        new("Noise", new[] { "Noise" }), new("Time", new[] { "Date" }), new("Alarm", new[] { "Alarm" }),
        new("Camera", new[] { "Camera" }), new("View", new[] { "View" }), new("Display", new[] { "Window", "Game" }),
        new("Room", new[] { "Room" }), new("Ui", new[] { "Ui" }), new("Debug", new[] { "Debug" }),
        new("Save", new[] { "Save" }), new("File", new[] { "File" }), new("Navigation", new[] { "NavMesh" }),
        new("Job", new[] { "Job" }), new("Shader", new[] { "Shader" }), new("Decal", new[] { "Decal" }),
        new("Mesh", new[] { "Mesh" }), new("Recording", new[] { "Record" }), new("Tile", new[] { "Tile" }),
        new("Terrain", new[] { "Terrain" }), new("Water", new[] { "Water" }), new("Weather", new[] { "Weather" }),
        new("Particle", new[] { "Particle" }), new("Global", new[] { "Global" }), new("Variable", new[] { "Variable" }),
        new("Resource", new[] { "Resource" }), new("Game", new[] { "Game" }),
    };

    private static readonly Dictionary<string, Area> AreasByName = Areas.ToDictionary(a => a.Name, StringComparer.Ordinal);

    // A command whose name starts with one of these belongs to that area whatever its category
    // (NumListAdd is a list command filed under Lists; RenderSetAntiAliasing is filed under Display).
    private static readonly (string Prefix, string Area)[] NameAreas =
    {
        ("NumList", "NumList"), ("PgList", "Array"), ("DsStack", "Stack"), ("DsQueue", "Queue"),
        ("DsPriority", "Priority"), ("PostEffect", "PostEffect"), ("Render", "Rendering"), ("Sky", "Sky"),
        ("Camera", "Camera"), ("Window", "Display"), ("Mouse", "Input"), ("InputRecord", "Input"),
        ("InputReplay", "Input"), ("Room", "Room"), ("Global", "Global"), ("Language", "Language"),
        ("Water", "Water"), ("Weather", "Weather"), ("Particle", "Particle"), ("Effects2D", "Particle"),
        ("NavMesh", "Navigation"),
    };

    private static readonly Dictionary<string, string> CategoryAreas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Physics"] = "Physics", ["Collision"] = "Physics", ["Audio"] = "Audio", ["Models"] = "Model",
        ["Sprites"] = "Image", ["Sprite rig"] = "Image", ["Textures"] = "Texture", ["Animation"] = "Animation",
        ["Animation State"] = "Animation", ["Drawing 2D"] = "Draw", ["Drawing 3D"] = "Draw", ["Input"] = "Input",
        ["Networking"] = "Network", ["Instances"] = "Instance", ["Instance Variables"] = "Instance",
        ["Lighting"] = "Lighting", ["Lighting 2D"] = "Lighting", ["Strings"] = "Text", ["Text"] = "Text",
        ["Lists"] = "List", ["Arrays"] = "Array", ["Maps"] = "Map", ["Grids"] = "Grid", ["Data Structures"] = "Data",
        ["JSON"] = "Json", ["Math"] = "Math", ["3D Math"] = "Math", ["Noise"] = "Noise", ["Time"] = "Time",
        ["Alarms"] = "Alarm", ["Camera"] = "Camera", ["Views"] = "View", ["Display"] = "Display", ["Rooms"] = "Room",
        ["User Interface"] = "Ui", ["Debug"] = "Debug", ["Save Data"] = "Save", ["Files"] = "File",
        ["Navigation"] = "Navigation", ["Native Jobs"] = "Job", ["Shaders"] = "Shader", ["Decals"] = "Decal",
        ["Meshes"] = "Mesh", ["Recording"] = "Recording", ["Tiles"] = "Tile", ["Terrain"] = "Terrain",
        ["Weather"] = "Weather", ["Particles"] = "Particle", ["Particles 2D"] = "Particle", ["Game"] = "Game",
        ["General"] = "Game", ["System"] = "Game", ["Events"] = "Game", ["Engine Variables"] = "Game",
        ["Variables"] = "Variable", ["Resources"] = "Resource", ["Rendering"] = "Rendering",
        // The Engine commands' categories.
        ["Windowing"] = "Display", ["Performance"] = "Debug", ["Viewport"] = "View", ["Assets"] = "Resource",
    };

    // The few names the rules would make worse than the obvious one.
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal)
    {
        ["CreateInstance"] = "Create",
    };

    private static readonly string[] Verbs = { "Get", "Set", "Is" };

    private static readonly object Gate = new();
    private static Built? _built;

    private sealed class Built
    {
        public readonly Dictionary<string, Entry> ByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Entry> Entries = new();
        public readonly List<string> Skipped = new();
        public int CatalogCount;
        // For ValueType: the commands' C# methods by name, and the catalogue by full name.
        public Dictionary<string, MethodInfo[]> Methods = new();
        public readonly Dictionary<string, PgslCommandInfo> Catalog = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every name of the standard, in area and name order.</summary>
    public static IReadOnlyList<Entry> Entries => Get().Entries;

    /// <summary>Names the rules gave two commands, or that an existing name already had: the first keeps it (for review).</summary>
    public static IReadOnlyList<string> Skipped => Get().Skipped;

    /// <summary>The areas, Engine.Physics and the rest.</summary>
    public static IEnumerable<string> AreaNames => Areas.Select(area => area.Name);

    /// <summary>
    /// Every Engine.Area.Name a script can use, in area and name order: the standard names, and the
    /// commands and properties written with a namespace of their own (Engine.Physics.Mass,
    /// Engine.Sky.Coverage). What the reference (Documentation/EngineApi.md) lists.
    /// </summary>
    public static IReadOnlyList<(string Name, bool IsProperty, bool CanSet, string Signature, string Description)> Reference()
    {
        var rows = new Dictionary<string, (string Name, bool IsProperty, bool CanSet, string Signature, string Description)>(StringComparer.OrdinalIgnoreCase);
        var catalog = new Dictionary<string, PgslCommandInfo>(StringComparer.OrdinalIgnoreCase);
        // The registry is filled when something first needs it (a game, the script editor); the
        // Inspector's settings can be asked for before either, and keep what they were given.
        if (PgslCommandRegistry.GetCatalog().Count == 0) PgslCommands.WarmRegistry();
        foreach (PgslCommandInfo info in PgslCommandRegistry.GetCatalog()) catalog.TryAdd(info.QualifiedName, info);
        bool Settable(PgslCommandInfo info) => info.IsProperty && typeof(PgslCommands)
            .GetProperty(info.CSharpMember[(info.CSharpMember.LastIndexOf('.') + 1)..], BindingFlags.Public | BindingFlags.Static)?.CanWrite == true;
        foreach (Entry entry in Entries)
        {
            bool canSet = entry.Command == null ? entry.SetterCommand != null
                : entry.IsProperty && catalog.TryGetValue(entry.Command, out PgslCommandInfo? named) && Settable(named);
            rows[entry.Name] = (entry.Name, entry.IsProperty, canSet, entry.Signature, entry.Description);
        }
        foreach (PgslCommandInfo info in catalog.Values)
        {
            if (string.IsNullOrWhiteSpace(info.Namespace) || rows.ContainsKey(info.QualifiedName)) continue;
            rows[info.QualifiedName] = (info.QualifiedName, info.IsProperty, Settable(info), info.Signature, info.Description);
        }
        return rows.Values.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The type a property of the API takes (double, bool, string...), or null when the name is not a
    /// property that can be set.
    /// </summary>
    public static Type? ValueType(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string qualified = name;
        if (TryGet(name, out Entry entry))
        {
            if (!entry.IsProperty) return null;
            if (entry.Command == null)
            {
                MethodInfo? setter = entry.SetterMember == null ? null
                    : Get().Methods.GetValueOrDefault(entry.SetterMember[(entry.SetterMember.LastIndexOf('.') + 1)..])?
                        .FirstOrDefault(method => method.GetParameters().Length >= 1 && Required(method) == 1);
                return setter?.GetParameters()[0].ParameterType;
            }
            qualified = entry.Command;
        }
        if (!Get().Catalog.TryGetValue(qualified, out PgslCommandInfo? info) || !info.IsProperty) return null;
        PropertyInfo? property = typeof(PgslCommands).GetProperty(info.CSharpMember[(info.CSharpMember.LastIndexOf('.') + 1)..], BindingFlags.Public | BindingFlags.Static);
        return property?.CanWrite == true ? property.PropertyType : null;
    }

    public static bool TryGet(string name, out Entry entry)
    {
        entry = null!;
        return !string.IsNullOrEmpty(name) && name.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase)
            && Get().ByName.TryGetValue(name, out entry!);
    }

    /// <summary>The area and standard name of a flat command (PhysicsApplyForce: Physics, ApplyForce); false when it has none.</summary>
    public static bool TryPlace(string command, string category, out string area, out string member)
    {
        member = command;
        area = "";
        foreach ((string prefix, string named) in NameAreas)
        {
            if (StartsWithWord(command, prefix)) { area = named; break; }
        }
        if (area.Length == 0 && !CategoryAreas.TryGetValue(category ?? "", out area!)) { area = ""; return false; }
        member = Renamed.TryGetValue(command, out string? renamed) ? renamed : Strip(command, AreasByName[area].Prefixes);
        return true;
    }

    private static string Strip(string name, string[] prefixes)
    {
        string result = name;
        foreach (string prefix in prefixes)
        {
            if (StartsWithWord(result, prefix)) { result = result[prefix.Length..]; break; }
        }
        // GetCameraX is the camera's X: the area's word after Get, Set or Is goes too.
        foreach (string verb in Verbs)
        {
            if (!result.StartsWith(verb, StringComparison.Ordinal)) continue;
            string rest = result[verb.Length..];
            foreach (string prefix in prefixes)
            {
                if (StartsWithWord(rest, prefix)) { result = verb + rest[prefix.Length..]; break; }
            }
            break;
        }
        return result.Length > 0 && char.IsLetter(result[0]) ? result : name;
    }

    // "Physics" starts PhysicsApplyForce but not Physicsy; "Light" does not start Light2DPoint
    // (the name left would begin with a digit).
    private static bool StartsWithWord(string name, string prefix) =>
        name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal) && char.IsUpper(name[prefix.Length]);

    private static Built Get()
    {
        Built? built = _built;
        if (built != null && built.CatalogCount == PgslCommandRegistry.GetCatalog().Count) return built;
        lock (Gate)
        {
            if (PgslCommandRegistry.GetCatalog().Count == 0) PgslCommands.WarmRegistry();
            built = _built;
            if (built != null && built.CatalogCount == PgslCommandRegistry.GetCatalog().Count) return built;
            return _built = Build(PgslCommandRegistry.GetCatalog());
        }
    }

    private static Built Build(IReadOnlyList<PgslCommandInfo> catalog)
    {
        var built = new Built { CatalogCount = catalog.Count };
        // Names some command already owns: an explicit namespace, or an Engine command's.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PgslCommandInfo info in catalog)
            if (!string.IsNullOrWhiteSpace(info.Namespace)) taken.Add(info.QualifiedName);
        foreach ((string bare, string? namespaced, _, _) in PgslNamespaceResolver.EngineCommands())
            if (namespaced != null) taken.Add(namespaced);

        var methods = typeof(PgslCommands).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .GroupBy(method => method.Name).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        built.Methods = methods;
        foreach (PgslCommandInfo info in catalog) built.Catalog.TryAdd(info.QualifiedName, info);
        var perArea = new Dictionary<string, Dictionary<string, PgslCommandInfo>>(StringComparer.Ordinal);

        // Flat commands first, so a name the rules give two commands goes to the flat one; then those
        // with an older namespace of their own (Engine.Rendering.Lights.LightFlash is also
        // Engine.Lighting.Flash); then the Engine commands (Engine.AudioPlay is Engine.Audio.Play).
        foreach (PgslCommandInfo info in catalog
            .OrderBy(info => string.IsNullOrWhiteSpace(info.Namespace) ? 0 : 1)
            .ThenBy(info => info.Name, StringComparer.Ordinal))
        {
            if (!info.IsImplemented || info.Name.Contains('.')) continue;
            bool namespaced = !string.IsNullOrWhiteSpace(info.Namespace);
            if (!TryPlace(info.Name, info.Category, out string area, out string member)) continue;
            string name = "Engine." + area + "." + member;
            // A command written with its standard name (Engine.Physics.Mass, Engine.Lighting.Bounce.Strength)
            // is that name already: one in a part of an area gets no shorter name beside it.
            if (namespaced && (!info.Flat || string.Equals(name, info.QualifiedName, StringComparison.OrdinalIgnoreCase))) continue;
            if (taken.Contains(name) || built.ByName.ContainsKey(name))
            {
                built.Skipped.Add($"{name}: {info.QualifiedName} (already {(taken.Contains(name) ? "an existing name" : built.ByName[name].Command)})");
                continue;
            }
            Add(built, new Entry
            {
                Name = name, Area = area, Member = member, Command = info.QualifiedName, IsProperty = info.IsProperty,
                Category = info.Category, Description = info.Description, Signature = info.Signature,
            });
            if (!perArea.TryGetValue(area, out var members)) perArea[area] = members = new(StringComparer.Ordinal);
            members[member] = info;
        }
        foreach ((string area, Dictionary<string, PgslCommandInfo> members) in perArea)
            AddPairs(built, area, members, methods, taken);
        // Engine commands after the pairs: a value's property keeps its name (Engine.Audio.MasterVolume).
        foreach ((string bare, _, string category, MethodInfo method) in PgslNamespaceResolver.EngineCommands()
            .OrderBy(command => command.Bare, StringComparer.Ordinal))
        {
            EngineCommandAttribute? attribute = method.GetCustomAttribute<EngineCommandAttribute>();
            if (attribute is null || !attribute.IsImplemented || !TryPlace(bare, category, out string area, out string member)) continue;
            string name = "Engine." + area + "." + member;
            if (taken.Contains(name) || built.ByName.ContainsKey(name))
            {
                if (!taken.Contains(name)) built.Skipped.Add($"{name}: Engine.{bare} (already {built.ByName[name].Command})");
                continue;
            }
            Add(built, new Entry
            {
                Name = name, Area = area, Member = member, Command = "Engine." + bare,
                Category = category, Description = attribute.Description ?? "", Signature = attribute.Signature,
            });
        }

        built.Entries.Sort((a, b) =>
        {
            int byArea = string.CompareOrdinal(a.Area, b.Area);
            return byArea != 0 ? byArea : string.CompareOrdinal(a.Member, b.Member);
        });
        return built;
    }

    // A value with a getter (GetX(), IsX() or X()) or a setter (SetX(value)) in the area is the property X.
    private static void AddPairs(Built built, string area, Dictionary<string, PgslCommandInfo> members,
        Dictionary<string, MethodInfo[]> methods, HashSet<string> taken)
    {
        var values = new SortedDictionary<string, (PgslCommandInfo? Get, PgslCommandInfo? Set)>(StringComparer.Ordinal);
        foreach ((string member, PgslCommandInfo info) in members)
        {
            if (info.IsProperty) continue;
            foreach (string verb in Verbs)
            {
                if (!member.StartsWith(verb, StringComparison.Ordinal)) continue;
                string value = member.Length == verb.Length ? "Name" : member[verb.Length..];
                if (member.Length > verb.Length && !char.IsUpper(value[0])) break;
                bool setter = verb == "Set";
                if (setter ? !TakesOneValue(info, methods) : !GivesAValue(info, methods)) break;
                values.TryGetValue(value, out var pair);
                values[value] = setter ? (pair.Get, info) : (pair.Get ?? info, pair.Set);
                break;
            }
        }
        // A command named for its value (BurstLimit()) reads the value SetBurstLimit sets.
        foreach (string value in values.Keys.ToList())
        {
            var pair = values[value];
            if (pair.Get == null && members.TryGetValue(value, out PgslCommandInfo? plain) && !plain.IsProperty && GivesAValue(plain, methods))
                values[value] = (plain, pair.Set);
        }

        foreach ((string value, var pair) in values)
        {
            string name = "Engine." + area + "." + value;
            if (taken.Contains(name)) { built.Skipped.Add($"{name}: property (already an existing name)"); continue; }
            if (built.ByName.TryGetValue(name, out Entry? existing))
            {
                // Only the value's own command (BurstLimit()) gives way to the property.
                if (existing.IsProperty || existing.Command != pair.Get?.Name)
                {
                    built.Skipped.Add($"{name}: property (already {existing.Command})");
                    continue;
                }
                built.Entries.Remove(existing);
                built.ByName.Remove(name);
            }
            PgslCommandInfo described = pair.Get ?? pair.Set!;
            Add(built, new Entry
            {
                Name = name, Area = area, Member = value, IsProperty = true,
                GetterCommand = pair.Get?.Name, SetterCommand = pair.Set?.Name,
                GetterMember = pair.Get?.CSharpMember, SetterMember = pair.Set?.CSharpMember,
                Category = described.Category,
                Description = pair.Get != null && pair.Set != null ? $"Reads ({pair.Get.Name}) and sets ({pair.Set.Name}): {described.Description}"
                    : pair.Get != null ? $"Reads ({pair.Get.Name}): {described.Description}" : $"Sets ({pair.Set!.Name}): {described.Description}",
                Signature = name + (pair.Set != null ? " = value" : ""),
            });
        }
    }

    private static void Add(Built built, Entry entry)
    {
        built.ByName[entry.Name] = entry;
        built.Entries.Add(entry);
    }

    private static MethodInfo[] Overloads(PgslCommandInfo info, Dictionary<string, MethodInfo[]> methods)
    {
        string member = info.CSharpMember ?? "";
        member = member[(member.LastIndexOf('.') + 1)..];
        return methods.TryGetValue(member, out MethodInfo[]? found) ? found : Array.Empty<MethodInfo>();
    }

    private static int Required(MethodInfo method) =>
        method.GetParameters().Count(parameter => !parameter.IsOptional && parameter.GetCustomAttribute<ParamArrayAttribute>() == null);

    private static bool GivesAValue(PgslCommandInfo info, Dictionary<string, MethodInfo[]> methods) =>
        Overloads(info, methods).Any(method => method.ReturnType != typeof(void) && Required(method) == 0);

    private static bool TakesOneValue(PgslCommandInfo info, Dictionary<string, MethodInfo[]> methods) =>
        Overloads(info, methods).Any(method => Required(method) == 1 && method.GetParameters().Length >= 1
            && method.GetParameters()[0].GetCustomAttribute<ParamArrayAttribute>() == null);
}
