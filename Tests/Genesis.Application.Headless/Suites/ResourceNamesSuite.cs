using System.Reflection;
using System.Text.Json;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Imaging;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

/// <summary>Exercises the production namespace, migration, lifecycle, pickers and runtime resolvers.
/// No renderer is required; this suite does not claim a graphics or interactive authoring pass.</summary>
internal static class ResourceNamesSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Resource names");
        void Check(string name, Action<ProjectFixture> test) => HeadlessHarness.RunCase(ctx.Report, "Resource.Names." + name, () =>
        {
            using ProjectFixture fixture = new(); test(fixture);
        });
        Check("NamesAreGlobalAndCaseInsensitive", p =>
        {
            string file = p.Add("Sprites/old-storage.image.json"); p.Identity(file, "Hero");
            Assert(ResourceNames.Resolve(p.Root, "hErO", ResourceType.Image) == file, "Name lookup did not resolve the image.");
            Assert(ResourceNames.Resolve(p.Root, "Hero", ResourceType.Object).Length == 0, "Type checks selected a different namespace.");
        });
        Check("DuplicateNamesAcrossTypesAreRejected", p =>
        {
            p.Add("Sprites/Hero.image.json"); p.Add("Objects/hero.object.json");
            Throws<InvalidDataException>(() => ResourceNames.For(p.Root).Find("Hero"));
        });
        Check("AllSupportedResourceKindsShareOneIndex", p =>
        {
            int index = 0;
            foreach (var definition in ResourceNames.Definitions)
            {
                string name = "Resource" + index++;
                string file = p.Add("Library/" + name + definition.Extension, definition.Type is ResourceType.Script or ResourceType.Note ? "// content" : "{}");
                Assert(ResourceNames.Resolve(p.Root, name, definition.Type) == file, "Kind failed: " + definition.Type);
            }
            Assert(ResourceNames.For(p.Root).Entries.Count == index, "A resource kind was omitted.");
        });
        Check("PayloadsPrivateEventsAndCachesAreNotResources", p =>
        {
            p.Add("Objects/Actor.object.json"); p.Add("Objects/Actor/Create.pgsl", "return;");
            p.Add("Objects/Actor/Step.pgsl", "return;"); p.Add("Sprites/Hero.spritedata/frames/hidden.image.json");
            p.Add("Terrain/Land.terrain.json.parts/private.terrainentity.json"); p.Add("Sprites/pixels.png", "pixels");
            p.Add("Audio/tone.wav", "sound"); p.Add(".cache/hidden.room.json");
            Assert(ResourceNames.For(p.Root).Entries.Select(e => e.Name).SequenceEqual(["Actor"]), "Private files escaped into the global namespace.");
        });
        Check("LegacyReferencesResolveButEmitNames", p =>
        {
            string file = p.Add("Sprites/Subfolder/Hero.image.json");
            Assert(ResourceNames.Name(p.Root, "Assets/Sprites/Subfolder/Hero.image.json") == "Hero", "Legacy reference was exposed to authoring.");
            Assert(ResourceNames.Resolve(p.Root, file) == file, "Private file notifications cannot resolve their owner.");
            Assert(ResourceNames.Resolve(p.Root, "Hero.image.json") == file, "Unique old filename fallback failed.");
        });
        Check("NoFirstMatchForDuplicatePhysicalNames", p =>
        {
            string a = p.Add("Sprites/A/Hero.image.json"), b = p.Add("Sprites/B/Hero.image.json");
            p.Identity(a, "HeroA"); p.Identity(b, "HeroB");
            Assert(ResourceNames.Resolve(p.Root, "Hero.image.json").Length == 0, "Ambiguous private filename resolved to the first directory.");
            Assert(ResourceNames.Resolve(p.Root, "HeroB") == b, "Canonical name lost to physical filename.");
        });
        Check("CatalogIsCachedUntilInvalidated", p =>
        {
            p.Add("Sprites/Hero.image.json"); ResourceCatalog first = ResourceNames.For(p.Root);
            Assert(ReferenceEquals(first, ResourceNames.For(p.Root)), "A resource lookup rescanned its project.");
            ResourceNames.Invalidate(p.Root);
            Assert(!ReferenceEquals(first, ResourceNames.For(p.Root)), "Cache invalidation had no effect.");
        });
        Check("PublicNamesRejectPathsAndExtensions", _ =>
        {
            foreach (string invalid in new[] { "Hero.image.json", "Hero.image", "Hero.json", "Assets/Hero", "Assets\\Hero", "Hero.pgsl", "Hero.png", "", "CON" })
                Throws<ArgumentException>(() => ResourceNames.ValidateName(invalid));
            Assert(ResourceNames.ValidateName("Blue Ghost") == "Blue Ghost", "Spaced names are not supported.");
            Assert(ResourceNames.ValidateName("Hero.Run") == "Hero.Run", "A dot within a logical name was mistaken for a file suffix.");
        });
        Check("TraversalAndWrongTypeCannotBypassResolution", p =>
        {
            string file = p.Add("Sprites/Hero.image.json");
            Assert(ResourceNames.ResolveFile(p.Root, file, ResourceType.Audio).Length == 0, "Wrong-type descriptors fell through to raw-file loading.");
            Assert(ResourceNames.Resolve(p.Root, "../Other/Hero.image.json").Length == 0, "A resource escaped the project.");
        });
        Check("JsonReferencesNormalizeButPayloadsAndProseDoNot", p =>
        {
            p.Add("Sprites/Hero.image.json");
            string text = "{\"sprite\":\"Assets/Sprites/Hero.image.json\",\"source\":\"Hero.spritedata/frames/a.png\",\"text\":\"Assets/Sprites/Hero.image.json\"}";
            JObject result = JObject.Parse(ResourceReferenceRewriter.Normalize(p.Root, p.PathOf("Objects/Actor.object.json"), text));
            Assert((string?)result["sprite"] == "Hero", "Sprite field retained its storage path.");
            Assert((string?)result["source"] == "Hero.spritedata/frames/a.png", "Private pixel source was rewritten.");
            Assert((string?)result["text"] == "Assets/Sprites/Hero.image.json", "Dialogue was rewritten as a resource.");
        });
        Check("PgslLiteralsNormalizeWithoutRewritingComments", p =>
        {
            p.Add("Sprites/Hero.image.json");
            string code = "// SpriteSet(\"Assets/Sprites/Hero.image.json\");\nSpriteSet(\"Assets/Sprites/Hero.image.json\");";
            string result = ResourceReferenceRewriter.Normalize(p.Root, p.PathOf("Scripts/Move.pgsl"), code);
            Assert(result == "// SpriteSet(\"Assets/Sprites/Hero.image.json\");\nSpriteSet(\"Hero\");", "Comments or PGSL bindings were damaged.");
        });
        Check("InlineObjectEventsNormalize", p =>
        {
            p.Add("Sprites/Hero.image.json");
            var doc = new JObject { ["events"] = new JObject { ["Create"] = "// start\nSpriteSet(\"Assets/Sprites/Hero.image.json\");" } };
            string text = ResourceReferenceRewriter.Normalize(p.Root, p.PathOf("Objects/Actor.object.json"), doc.ToString());
            Assert((string?)JObject.Parse(text)["events"]?["Create"] == "// start\nSpriteSet(\"Hero\");", "Inline events bypassed normalization.");
        });
        Check("CSharpTypedRigReferencesRename", _ =>
        {
            string code = "SpriteRigRuntime.Bind(world, entity, root, \"Hero\", \"Upper Body\", out error);";
            string result = ResourceReferenceRewriter.RewriteCode(code, (name, type) => name == "Hero" && type == ResourceType.Image ? "Explorer" : name);
            Assert(result.Contains("\"Explorer\"", StringComparison.Ordinal) && result.Contains("\"Upper Body\"", StringComparison.Ordinal), "C# binding did not separate resource and rig names.");
        });
        Check("MetadataControlsPickerAndBrowserNames", p =>
        {
            string file = p.Add("Sprites/storage.image.json"); p.Identity(file, "Hero");
            ProjectAssetEntry entry = ProjectAssetIndex.Enumerate(p.Root, ResourceKind.Image).Single();
            Assert(entry.Reference == "Hero" && entry.DisplayName == "Hero" && entry.FullPath == file, "A picker exposes storage as a reference.");
            Assert(ResourceDisplayName.Format(file) == "Hero", "Display helper ignored canonical identity.");
        });
        Check("CreateEnforcesGlobalUniquenessAcrossKinds", p =>
        {
            ResourceService service = new(p.Session);
            string image = service.CreateResource(ResourceFolderPolicy.RootFor(p.Session, ResourceKind.Image), ResourceKind.Image, "Hero");
            string obj = service.CreateResource(ResourceFolderPolicy.RootFor(p.Session, ResourceKind.GameObject), ResourceKind.GameObject, "Hero");
            Assert(ResourceNames.Name(p.Root, image) == "Hero" && ResourceNames.Name(p.Root, obj) != "Hero", "Create allowed same-named resources of different kinds.");
            Assert(!ResourceNames.For(p.Root).Conflicts.Any(), "Creation left a name conflict.");
        });
        Check("MigrationDisambiguatesAndRewritesTypedReferences", p =>
        {
            string image = p.Add("Sprites/Hero.image.json"); string obj = p.Add("Objects/Hero.object.json", "{\"sprite\":\"Hero\"}");
            string code = p.Add("Objects/Hero/Create.pgsl", "SpriteSet(\"Hero\"); CreateInstance(\"Hero\", 0, 0);");
            Assert(ResourceNameMigration.Upgrade(p.Session), "Legacy project was not upgraded.");
            Assert(ResourceNames.Name(p.Root, image) == "HeroSprite" && ResourceNames.Name(p.Root, obj) == "Hero", "Duplicate allocation is not deterministic.");
            Assert((string?)JObject.Parse(File.ReadAllText(obj))["sprite"] == "HeroSprite", "Typed sprite binding was confused with its same-named object.");
            Assert(File.ReadAllText(code) == "SpriteSet(\"HeroSprite\"); CreateInstance(\"Hero\", 0, 0);", "Typed PGSL arguments were not disambiguated.");
            Assert(!ResourceNames.For(p.Root).Conflicts.Any(), "Migration left duplicate public names.");
        });
        Check("MigrationPreservesPixelsAndProvidesBackupIndex", p =>
        {
            p.Add("Sprites/Hero.image.json"); string payload = p.Add("Sprites/Hero.spritedata/frames/a.png", "ORIGINAL PIXELS");
            byte[] original = File.ReadAllBytes(payload); ResourceNameMigration.Upgrade(p.Session);
            Assert(File.ReadAllBytes(payload).SequenceEqual(original), "Migration modified media.");
            string index = Directory.EnumerateFiles(p.Session.InternalPath, "Index.json", SearchOption.AllDirectories).Single();
            Assert(JObject.Parse(File.ReadAllText(index))["files"] is JArray { Count: > 0 }, "Migration backup has no restore index.");
        });
        Check("MigrationIsIdempotent", p =>
        {
            p.Add("Sprites/Hero.image.json"); ResourceNameMigration.Upgrade(p.Session);
            string manifest = File.ReadAllText(p.Session.ProjectFile);
            Assert(!ResourceNameMigration.Upgrade(p.Session), "An already-migrated project was rewritten.");
            Assert(File.ReadAllText(p.Session.ProjectFile) == manifest, "Idempotent load changed the manifest.");
        });
        Check("InvalidMigrationDoesNotPartiallyWrite", p =>
        {
            string file = p.Add("Sprites/Hero.image.json", "NOT JSON"); string manifest = File.ReadAllText(p.Session.ProjectFile);
            Throws<Newtonsoft.Json.JsonReaderException>(() => ResourceNameMigration.Upgrade(p.Session));
            Assert(!File.Exists(file + ".meta") && File.ReadAllText(p.Session.ProjectFile) == manifest, "Migration wrote live metadata before validating all resources.");
        });
        Check("RenameUpdatesReferencesWithoutMovingStorage", p =>
        {
            string image = p.Add("Sprites/Hero.image.json"); string obj = p.Add("Objects/Actor.object.json", "{\"sprite\":\"Hero\"}");
            p.Identity(image, "Hero"); string metadata = File.ReadAllText(image + ".meta");
            string script = p.Add("Scripts/Move.pgsl", "SpriteSet(\"Hero\");");
            ResourceReferenceOperations.Rename(p.Session, image, "Explorer");
            Assert(File.Exists(image) && ResourceNames.Resolve(p.Root, "Explorer") == image, "Rename moved private storage or failed to update identity.");
            Assert(File.ReadAllText(script) == "SpriteSet(\"Explorer\");" && (string?)JObject.Parse(File.ReadAllText(obj))["sprite"] == "Explorer", "Rename missed stored references.");
            Assert((string?)JObject.Parse(File.ReadAllText(image + ".meta"))["guid"] == (string?)JObject.Parse(metadata)["guid"], "Rename changed the resource GUID.");
        });
        Check("RenameCollisionIsRejectedBeforeWriting", p =>
        {
            string image = p.Add("Sprites/Hero.image.json"); p.Add("Audio/Bell.audio.json");
            Throws<InvalidOperationException>(() => ResourceReferenceOperations.Rename(p.Session, image, "Bell"));
            Assert(ResourceNames.Name(p.Root, image) == "Hero", "Rejected rename changed the resource.");
        });
        Check("RenameGuardPreservesUnsavedWork", p =>
        {
            string file = p.Add("Sprites/Hero.image.json"); ResourceService service = new(p.Session);
            service.BeforeReferenceEdit = () => throw new InvalidOperationException("Save first");
            Throws<InvalidOperationException>(() => service.Rename(file, "Explorer"));
            Assert(ResourceNames.Name(p.Root, file) == "Hero", "Dirty-document guard ran after the rename.");
        });
        Check("DuplicateGetsFreshIdentityAndRetargetsSelfReferences", p =>
        {
            string obj = p.Add("Objects/Actor.object.json", "{\"parent\":\"\",\"name\":\"Actor\"}"); p.Identity(obj, "Actor");
            p.Add("Objects/Actor/Create.pgsl", "CreateInstance(\"Actor\",0,0);");
            ResourceService service = new(p.Session); string copy = service.Duplicate(obj); string name = ResourceNames.Name(p.Root, copy);
            Assert(name != "Actor" && !ResourceNames.For(p.Root).Conflicts.Any(), "Duplicate reused a public identity.");
            string program = Path.Combine(ObjectEventStore.FolderFor(copy), "Create.pgsl");
            Assert(File.ReadAllText(program).Contains("\"" + name + "\"", StringComparison.Ordinal), "Copied self-reference still names the original.");
        });
        Check("ObjectEventSaveUsesNames", p =>
        {
            p.Add("Sprites/Hero.image.json"); string obj = p.Add("Objects/Actor.object.json");
            ObjectEventStore.Save(obj, new Dictionary<string, string> { ["Create"] = "SpriteSet(\"Assets/Sprites/Hero.image.json\");" });
            Assert(File.ReadAllText(Path.Combine(ObjectEventStore.FolderFor(obj), "Create.pgsl")) == "SpriteSet(\"Hero\");", "Event persistence bypassed normalization.");
        });
        Check("RuntimeSpriteAndRigLoaderAcceptNames", p =>
        {
            string file = p.Add("Sprites/storage.image.json", ImageDocumentSerializer.Serialize(ImageDocument.CreateDefault())); p.Identity(file, "Hero");
            Assert(SpriteAssetLoader.ResolveDescriptorPath(p.Root, "Hero") == file, "Runtime image resolver requires storage names.");
            SpriteAssetLoader.Load(p.Root, "Hero");
            try { PixelRigAssetLoader.Load(p.Root, "Hero"); throw new InvalidOperationException("An unrigged image unexpectedly bound."); }
            catch (InvalidDataException error) { Assert(error.Message.Contains("no pixel rig", StringComparison.OrdinalIgnoreCase), "Rig loader failed before resolving the named image."); }
        });
        Check("RuntimeRoomAndPrefabAcceptNames", p =>
        {
            string room = p.Add("Rooms/storage.room.json"); p.Identity(room, "Courtyard");
            string obj = p.Add("Objects/storage.object.json"); p.Identity(obj, "Player");
            Assert(ProjectRoomResolver.ResolveRoomFile(p.Root, "Courtyard") == room, "Room resolver requires a filename.");
            Assert(RoomSceneBuilder.ResolvePrefabPath(p.Root, "Player") == obj, "Object resolver requires a filename.");
        });
        Check("AssetDatabaseUsesSameGlobalIdentity", p =>
        {
            string file = p.Add("Sprites/storage.image.json"); p.Identity(file, "Hero");
            AssetDatabase db = new(p.Root);
            Assert(db.ResolveName("Hero")?.AbsolutePath == file && db.ResolveName(AssetKind.Texture, "Hero")?.Name == "Hero", "Shared asset database has a different name namespace.");
        });
        Check("ExportRetainsIdentityMetadata", _ =>
        {
            MethodInfo method = typeof(GameExportService).GetMethod("ShouldExcludeFile", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert(!(bool)method.Invoke(null, ["Hero.image.json.meta"])!, "Export strips canonical identity metadata.");
        });
        Check("MarkdownResourceLinksAndCodeExamplesRename", _ =>
        {
            string md = "[Open](Hero)\n```pgsl\nSpriteSet(\"Hero\");\n```\nHero is the protagonist.";
            string result = ResourceReferenceRewriter.RewriteDocument("Guide.md", md, (name, type) => name == "Hero" ? "Explorer" : name);
            Assert(result.Contains("[Open](Explorer)", StringComparison.Ordinal) && result.Contains("SpriteSet(\"Explorer\")", StringComparison.Ordinal)
                && result.EndsWith("Hero is the protagonist.", StringComparison.Ordinal), "Note migration confused links/code with prose.");
        });
        Check("LogicalRenameSurvivesFolderMove", p =>
        {
            ResourceService service = new(p.Session);
            string original = service.CreateResource(p.Session.AssetsPath, ResourceKind.Image, "Hero");
            service.Rename(original, "Explorer");
            string folder = service.CreateFolder(Path.GetDirectoryName(original)!, "Characters");
            string moved = service.Move(original, folder);
            Assert(ResourceNames.Resolve(p.Root, "Explorer") == moved && ResourceNames.For(p.Root).Find("Hero") is null,
                "Moving an asset reverted its logical name to its private filename.");
        });
        Check("NamedCSharpBehaviorSurvivesSourceFreeExport", p =>
        {
            string script = p.Add("Scripts/PrivateImplementation.cs", "public sealed class PrivateImplementation : Genesis.Runtime.Scripting.EntityBehavior { }");
            p.Identity(script, "Hero Controller");
            ScriptCompileResult result = CSharpScriptCompiler.Compile([script], "ResourceNameTest", p.Root);
            Assert(result.Success && result.Assembly != null, string.Join(Environment.NewLine, result.Errors));
            File.Delete(script); // Export may remove source; bindings must be in the assembly itself.
            var host = new ScriptHostSystem();
            host.LoadAssembly(result.Assembly!, result.LoadContext);
            Assert(host.AvailableBehaviors.SequenceEqual(["Hero Controller"]), "The Object binding leaked a CLR class or source filename.");
            Assert(result.Assembly!.GetCustomAttributes<ResourceBehaviorAttribute>().Single().ResourceName == "Hero Controller", "Compiled identity missing.");
            host.LoadAssembly(null!);
        });
        Check("DirectScriptCallsRenameToNamesWithSpaces", p =>
        {
            string script = p.Add("Scripts/OldScript.pgsl", "return 42;");
            string caller = p.Add("Scripts/Caller.pgsl", "var value = OldScript(2); OldScript; // OldScript(3);\nPrint(\"OldScript\");");
            ResourceReferenceOperations.Rename(p.Session, script, "New Script");
            string code = File.ReadAllText(caller);
            Assert(code.Contains("ScriptExecute(\"New Script\", 2)", StringComparison.Ordinal)
                && code.Contains("ScriptExecute(\"New Script\");", StringComparison.Ordinal)
                && code.Contains("// OldScript(3)", StringComparison.Ordinal)
                && code.Contains("Print(\"OldScript\")", StringComparison.Ordinal), "Direct call rename changed prose or lost a module call.");
        });
        Check("LocalFunctionsAreNotResourceCalls", p =>
        {
            string script = p.Add("Scripts/OldScript.pgsl", "return 42;");
            string caller = p.Add("Scripts/Caller.pgsl", "function OldScript() { return 5; } var x = OldScript();");
            string before = File.ReadAllText(caller);
            ResourceReferenceOperations.Rename(p.Session, script, "New Script");
            Assert(File.ReadAllText(caller) == before, "Resource rename rewrote a local function declaration or its call.");
        });
        Check("ScriptExecuteIsVariadicAndTyped", p =>
        {
            MethodInfo method = typeof(PgslCommands).GetMethod(nameof(PgslCommands.ScriptExecute))!;
            Assert(method.GetParameters()[1].IsDefined(typeof(ParamArrayAttribute), false), "Named script invocation lost its argument forwarding contract.");
            p.Add("Scripts/Work.pgsl", "return 1;");
            string code = ResourceReferenceRewriter.Normalize(p.Root, p.PathOf("Scripts/Caller.pgsl"), "ScriptExecute(\"Assets/Scripts/Work.pgsl\", 10, 20);");
            Assert(code == "ScriptExecute(\"Work\", 10, 20);", "Named script invocation retained its private path.");
        });
        Check("DottedLogicalNamesAreNotFileExtensions", p =>
        {
            string sprite = p.Add("Sprites/Storage.image.json"); p.Identity(sprite, "Hero.Run");
            Assert(ResourceNames.ValidateName("Hero.Run") == "Hero.Run" && ResourceNames.FormatName("Hero.Run") == "Hero.Run"
                && ResourceNames.Resolve(p.Root, "Hero.Run", ResourceType.Image) == sprite, "A dotted logical name was stripped as a file extension.");
        });
        Check("RoomSaveCanonicalizesPrefabAndTileset", p =>
        {
            string image = p.Add("Sprites/Hero.image.json"); string obj = p.Add("Objects/Actor.object.json");
            string room = p.Add("Rooms/Courtyard.room.json", ResourceDefinitions.Get(ResourceKind.Room).DefaultContent);
            RoomAsset data = RoomAssetLoader.Parse(room);
            string json = "{\"prefab\":{\"name\":\"" + obj.Replace('\\','/') + "\"},\"tileset\":\"" + image.Replace('\\','/') + "\"}";
            JObject result = JObject.Parse(ResourceReferenceRewriter.Normalize(p.Root, room, json));
            Assert((string?)result["prefab"]?["name"] == "Actor" && (string?)result["tileset"] == "Hero", "Room resource fields retain paths.");
            RoomAssetLoader.Save(data, room); Assert(data.Name == "Courtyard", "Room identity was replaced by the document's default label.");
        });
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    private sealed class ProjectFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "genesis-names-" + Guid.NewGuid().ToString("N"));
        public ProjectSession Session { get; }
        public ProjectFixture()
        {
            Directory.CreateDirectory(Root);
            Session = new ProjectSession(Root, Path.Combine(Root, "Test.genesisproj"), new ProjectManifest { ProjectId = Guid.NewGuid().ToString("N"), Name = "Test" });
            File.WriteAllText(Session.ProjectFile, JsonSerializer.Serialize(Session.Manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            Directory.CreateDirectory(Session.AssetsPath);
        }
        public string PathOf(string relative) => Path.Combine(Session.AssetsPath, relative.Replace('/', Path.DirectorySeparatorChar));
        public string Add(string relative, string content = "{}")
        {
            string file = PathOf(relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, content);
            ResourceNames.Invalidate(Root); return file;
        }
        public void Identity(string file, string name)
        {
            File.WriteAllText(file + ".meta", new JObject { ["guid"] = Guid.NewGuid().ToString("N"), ["kind"] = ResourceNames.TypeOf(file).ToString(), ["resourceName"] = name, ["displayName"] = name }.ToString());
            ResourceNames.Invalidate(Root);
        }
        public void Dispose()
        {
            ResourceNames.Invalidate(Root); try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }
}
