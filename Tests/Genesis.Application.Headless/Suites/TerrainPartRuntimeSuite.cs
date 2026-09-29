using System.Drawing;
using System.Numerics;
using System.Text.Json;
using System.Windows.Forms;
using System.Collections;
using System.Reflection;
using Genesis.Audio;
using Genesis.Runtime.Core;
using Genesis.Runtime.Project;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Runtime.Assets;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Genesis.Shared.Audio;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS.Components;
using Genesis.World.Terrain;
using Newtonsoft.Json;

namespace Genesis.Application.Headless.Suites;

internal static class TerrainPartRuntimeSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.SavedScriptAndConditionDriveGameplay", () => Gameplay(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.PlacementAndDisabledComponents", () => Placement(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.PrivateModelRefreshAndDependencies", () => Dependencies(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.RoomPreviewShowsOwnedGeometry", () => Preview(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.PreviewIsolationAndLivePlacement", () => PreviewIsolation(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.AuthoredSpatialAudioUsesNativeMixer", () => Audio(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.PhysicsEnterExitScriptsUseRealContacts", () => Contacts(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.MeshColliderUsesGeometryPivotAndScale", () => MeshCollider(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.SavedImageMaterialsAnimateAndBindModels", () => ImageMaterials(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.TextureModesAndLiveImageMatchAuthoringViews", () => TextureModes(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Parts.SavedScriptControlsActualParticleEmission", () => Particles(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Terrain.Loading.CreateAndRoomStartSeeSavedGround", () => CreateGround(ctx));
    }

    private static void CreateGround(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "CreateGround");
        VMEngine.Initialize();
        string obj = fixture.Resources.CreateResource(fixture.Resources.AssetsRoot, ResourceKind.GameObject, "Ground follower");
        File.WriteAllText(obj, """
            { "dimension": "ThreeD", "sprite": "", "model": "", "events": ["Create", "RoomStart"],
              "components": [{ "type": "ScriptComponent", "props": { "ScriptClass": "Ground follower" } }] }
            """);
        string events = Path.Combine(Path.GetDirectoryName(obj)!, "Ground follower");
        Directory.CreateDirectory(events);
        File.WriteAllText(Path.Combine(events, "Create.pgsl"), "createHeight = GetTerrainHeight(x, z); y = createHeight;");
        File.WriteAllText(Path.Combine(events, "RoomStart.pgsl"), "observedRoomGround = GetTerrainHeight(x, z);");
        fixture.Room.Nodes[0].Transform.Y = 5;
        fixture.Room.Nodes.Add(new RoomNode { Id = "follower", Kind = RoomNodeKind.GameObject,
            LayerId = fixture.Room.Layers[0].Id, Transform = new RoomTransform { X = 2, Y = 40, Z = 2 },
            GameObject = new RoomGameObjectData { Prefab = ResourceNames.Name(fixture.Root, obj, ResourceType.Object) } });
        string roomPath = fixture.Resources.CreateResource(fixture.Resources.AssetsRoot, ResourceKind.Room, "Ground lifecycle");
        RoomAssetLoader.Save(fixture.Room, roomPath);
        using RuntimeScene scene = new();
        ScriptHostSystem scripts = new();
        ProjectGameContext game = new(fixture.Root, scene, null, null, fixture.Room, null);
        scripts.SetContext(game);
        List<ScriptDiagnostic> diagnostics = [];
        scripts.DiagnosticReported += diagnostics.Add;
        foreach (float ground in new[] { -8f, 14f })
        {
            TerrainAsset terrain = new(17, 17, 1, -8, -8, -20, 30);
            for (int z = 0; z < 17; z++) for (int x = 0; x < 17; x++) terrain.SetHeight(x, z, ground);
            terrain.Save(fixture.Terrain + ".gterrain");
            RoomAsset reopened = RoomAssetLoader.Parse(roomPath);
            RoomBuildResult built = ProjectRoomLoader.Build(fixture.Root, scene, reopened, scripts, game, beginGame: ground < 0);
            var entity = built.EntitiesByNodeId["follower"];
            var behavior = (PgslBehavior)scripts.FindBehaviorForEntity(entity);
            var variables = behavior.GetVariablesSnapshot();
            float expected = ground + 5;
            Assert(Math.Abs(scene.World.GetRef<TransformComponent>(entity).Y - expected) < .002f
                && Math.Abs(Convert.ToDouble(variables["createHeight"]) - expected) < .002
                && Math.Abs(Convert.ToDouble(variables["observedRoomGround"]) - expected) < .002,
                "Saved terrain was unavailable to Create or RoomStart during initial load or room replacement.");
            Assert(scene.Subsystems.OfType<RoomTerrainSubsystem>().Count() == 1 && diagnostics.Count == 0,
                "Room loading duplicated its terrain service or reported script errors.");
            scripts.EndRoom();
            scene.UnloadRoomContent(scripts, _ => false, preservePersistent: false);
        }
        scripts.Shutdown();
    }

    private static void Gameplay(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Gameplay");
        VMEngine.Initialize();
        ScriptHostSystem host = new();
        using RuntimeScene scene = new();
        host.SetContext(new NullGameContext { World = scene.World, Room = fixture.Room, ProjectPath = fixture.Root });
        RoomBuildResult built = new RoomSceneBuilder(fixture.Root, host).Build(scene, fixture.Room);
        var entity = built.EntitiesByNodeId[fixture.NodeId];
        Assert(built.SpawnedEntities.Count == 1, "The saved private terrain part was omitted or spawned twice.");
        Assert(scene.World.GetRef<TransformComponent>(entity).X == 4, "Create changed placement before the first Step.");
        PgslBehavior behavior = host.FindBehaviorForEntity(entity) as PgslBehavior
            ?? throw new InvalidDataException("The private part did not attach its PGSL VM.");
        host.Update(1f / 60);
        TransformComponent transform = scene.World.GetRef<TransformComponent>(entity);
        var variables = behavior.GetVariablesSnapshot();
        Assert(transform.X == 6 && transform.Z == 2 && variables["label"].ToString() == "Forest patrol"
            && Convert.ToBoolean(variables["active"]), "Saved PGSL code or typed part overrides did not execute in gameplay.");
        Assert(scene.World.GetRef<ModelAnimatorComponent>(entity).ClipName == "Wave", "The condition's visual animation action did not target the live Model.");
        host.Update(1f / 60);
        Assert(scene.World.GetRef<TransformComponent>(entity).X == 8, "The saved part script did not run on subsequent Steps.");
        Assert(File.ReadAllText(fixture.Script) == fixture.ScriptSource, "Runtime modified the reusable saved script.");
        Assert(scene.World.GetRef<ParticleComponent>(entity).Asset == "Part particles"
            && scene.World.GetRef<AudioComponent>(entity).Asset == "Part audio", "Particle or audio component references were lost.");
        Assert(ObjectDrawAssetRegistry.TryGet(entity, out var assets) && assets.InstanceName == "Forest prop"
            && assets.ShaderParameters["Strength"][0] == .75f, "Part identity or exposed shader values were lost.");
        host.Shutdown();
    }

    private static void Placement(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Placement");
        fixture.Room.Nodes[0].Transform = new RoomTransform { X = 10, Y = 3, RotationY = 90, ScaleX = 2, ScaleY = 2, ScaleZ = 2 };
        TerrainNatureDocument nature = TerrainNatureSerializer.LoadOrDefault(fixture.Terrain);
        TerrainPlacedEntity placed = nature.PlacedEntities.Single();
        placed.Pitch = 15; placed.Yaw = 30; placed.Roll = 5; placed.Scale = 1.5f;
        TerrainNatureSerializer.Save(fixture.Terrain, nature);
        using RuntimeScene scene = new();
        RoomBuildResult built = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room);
        var entity = built.EntitiesByNodeId[fixture.NodeId];
        RoomTransform expected = RoomHierarchyTransforms.Compose(fixture.Room.Nodes[0].Transform, new RoomTransform
        {
            X = placed.Position.X, Y = placed.Position.Y, Z = placed.Position.Z,
            RotationX = placed.Pitch, RotationY = placed.Yaw, RotationZ = placed.Roll,
            ScaleX = placed.Scale, ScaleY = placed.Scale, ScaleZ = placed.Scale,
        });
        Transform3DComponent transform = scene.World.GetRef<Transform3DComponent>(entity);
        Assert(Vector3.Distance(transform.Position, new(expected.X, expected.Y, expected.Z)) < .001f
            && Vector3.Distance(transform.Scale, new(expected.ScaleX, expected.ScaleY, expected.ScaleZ)) < .001f,
            "The private part did not inherit the terrain's world transform exactly once.");
        RigidBodyComponent body = scene.World.GetRef<RigidBodyComponent>(entity);
        Assert(body.Motion == PhysicsMotionType.Static && !body.UseGravity && Math.Abs(body.Size.X - 3) < .001f
            && body.Friction == .35f && body.Restitution == .2f, "Saved collider properties or model/placement scale were not consumed.");
        ModelAnimatorComponent animator = scene.World.GetRef<ModelAnimatorComponent>(entity);
        Assert(animator.ClipFps == 24 && !animator.Loop, "New placement lost its authored animation rate or looping choice.");
        fixture.Document.Components.Where(component => component.Type is "Model" or "Physics").ToList().ForEach(component => component.Enabled = false);
        fixture.SaveDocument();
        using RuntimeScene disabled = new();
        var other = new RoomSceneBuilder(fixture.Root).Build(disabled, fixture.Room).EntitiesByNodeId[fixture.NodeId];
        Assert(!disabled.World.Has<ModelRendererComponent>(other) && !disabled.World.Has<RigidBodyComponent>(other),
            "Disabled terrain part components still entered the runtime.");
    }

    private static void Dependencies(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Dependencies");
        RuntimeModelAssetRegistry registry = new();
        string reference = Path.GetRelativePath(fixture.Root, fixture.Model).Replace('\\', '/');
        GModelAsset original = registry.Load(fixture.Root, reference);
        Assert(!original.ImportRequired && original.Meshes.Count > 0 && original.Bounds.Size.X == 2,
            "The private terrain-owned .gmodel loaded a placeholder.");
        File.WriteAllText(fixture.Model, JsonConvert.SerializeObject(GModelPrimitiveFactory.CreateCube("Edited section", 4)));
        File.SetLastWriteTimeUtc(fixture.Model, DateTime.UtcNow.AddSeconds(2));
        Assert(registry.Load(fixture.Root, reference).Bounds.Size.X == 4, "Saved private model changes remained hidden by the asset cache.");
        AssetDependencyGraph graph = new(fixture.Root); graph.Refresh();
        Assert(graph.DependsOn(fixture.Terrain, fixture.Part) && graph.DependsOn(fixture.Terrain, fixture.Model)
            && graph.DependsOn(fixture.Terrain, fixture.Script), "Terrain's dependency graph omitted private payloads or their reusable scripts.");
        string duplicate = fixture.Resources.Duplicate(fixture.Terrain);
        using TerrainEditorControl editor = new(duplicate, fixture.Root);
        Assert(editor.Nature.PlacedEntities.Single().Entity.Contains(Path.GetFileName(duplicate) + ".parts", StringComparison.Ordinal),
            "Duplicating terrain left placement references bound to the source's private parts.");
        RoomAsset room = RoomAsset.Create("Copied", RoomDimension.ThreeD);
        room.Nodes.Add(new RoomNode { Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = ResourceNames.Name(fixture.Root, duplicate, ResourceType.Terrain) } });
        using RuntimeScene scene = new();
        Assert(new RoomSceneBuilder(fixture.Root).Build(scene, room).SpawnedEntities.Count == 1,
            "A duplicated terrain no longer spawned its owned part.");
    }

    private static void Preview(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Preview");
        string roomFile = fixture.Resources.CreateResource(fixture.Resources.AssetsRoot, ResourceKind.Room, "Parts preview");
        RoomAssetLoader.Save(fixture.Room, roomFile);
        using RoomEditorControl editor = new(roomFile, fixture.Root);
        using Form host = UnattendedWindowing.NewHost(1440, 900);
        editor.Dock = DockStyle.Fill; host.Controls.Add(editor); ThemeService.Apply(host);
        UnattendedWindowing.ShowWithoutFocus(host); editor.Viewport.Host.TimerEnabled = false;
        Vector3 position = TerrainNatureSerializer.LoadOrDefault(fixture.Terrain).PlacedEntities.Single().Position;
        editor.Viewport.Camera.Target = position + Vector3.UnitY;
        editor.Viewport.Camera.Distance = 10; editor.Viewport.Camera.Pitch = -.35f;
        using Bitmap pixels = editor.Viewport.CaptureFrame(3)
            ?? throw new InvalidDataException("The Room preview produced no frame.");
        int redPixels = 0;
        for (int y = 0; y < pixels.Height; y += 2)
        for (int x = 0; x < pixels.Width; x += 2)
        {
            Color pixel = pixels.GetPixel(x, y);
            if (pixel.R > 50 && pixel.R > pixel.G * 1.8 && pixel.R > pixel.B * 1.8) redPixels++;
        }
        pixels.Save(Path.Combine(ctx.Captures, "terrain-owned-part-room-frame.png"));
        ctx.Report.Images.Add(new ImageResult("terrain-owned-part-room-frame", "terrain-owned-part-room-frame.png", pixels.Width, pixels.Height, 0, 0));
        Editor3DInspectionSuite.Capture(ctx, host, "terrain-owned-part-room-layout");
        Assert(redPixels > 100, "The Room preview omitted the private part's red model geometry. " + editor.TerrainPreviewError);
        Vector3 before = editor.AuthoredTerrainDraws.Last().World.Translation;
        editor.Room.Nodes[0].Transform.X += 2;
        using Bitmap moved = editor.Viewport.CaptureFrame(3)
            ?? throw new InvalidDataException("The moved terrain preview produced no frame.");
        Assert(Vector3.Distance(editor.AuthoredTerrainDraws.Last().World.Translation, before + new Vector3(2, 0, 0)) < .001f,
            "Room placement edits did not move the terrain-owned geometry with its parent.");
        moved.Save(Path.Combine(ctx.Captures, "terrain-owned-part-room-moved.png"));
        ctx.Report.Images.Add(new ImageResult("terrain-owned-part-room-moved", "terrain-owned-part-room-moved.png", moved.Width, moved.Height, 0, 0));
    }

    private static void PreviewIsolation(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Isolation");
        using Genesis.Runtime.ECS.World gameplay = new();
        var gameEntity = gameplay.CreateEntity();
        ObjectDrawAssetEntry gameAssets = new() { InstanceName = "Running game" };
        ObjectDrawAssetRegistry.Set(gameEntity, gameAssets);
        ObjectDrawAssetRegistry.PreviewScope scope = new();
        using Genesis.Runtime.ECS.World preview = new();
        RoomBuildResult built;
        using (scope.Activate())
        {
            built = new RoomSceneBuilder(fixture.Root).BuildTerrainParts(preview, fixture.Room);
            var entity = built.EntitiesByNodeId[fixture.NodeId];
            Assert(entity.Id == gameEntity.Id && ObjectDrawAssetRegistry.TryGet(entity, out var assets)
                && assets.InstanceName == "Forest prop", "The preview did not bind its own entity assets.");
            fixture.Room.Nodes[0].Transform.X = 12;
            RoomSceneBuilder.UpdateTerrainPartPreviewTransforms(preview, built);
            Assert(preview.GetRef<TransformComponent>(entity).X == 16, "Live preview placement was stale.");
        }
        Assert(ObjectDrawAssetRegistry.TryGet(gameEntity, out var restored) && ReferenceEquals(restored, gameAssets),
            "Opening a terrain preview overwrote the running game's entity assets.");
        using (scope.Activate()) ObjectDrawAssetRegistry.Clear();
        Assert(ObjectDrawAssetRegistry.TryGet(gameEntity, out restored) && ReferenceEquals(restored, gameAssets),
            "Disposing a preview cleared the running game's assets.");
    }

    private static void Audio(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Audio");
        TerrainEntityComponent authored = fixture.Document.Components.Single(component => component.Type == "AudioEmitter");
        authored.Props["Volume"] = "0"; authored.Props["MinDistance"] = "2";
        authored.Props["MaxDistance"] = "10"; authored.Props["Falloff"] = "Logarithmic";
        fixture.SaveDocument();
        string wave = Path.Combine(fixture.Root, "Assets", "Audio", "Part tone.wav");
        using (BinaryWriter writer = new(File.Create(wave)))
        {
            const int samples = 22050;
            writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(samples);
            writer.Write(samples * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8);
            writer.Write(samples * 2);
            for (int index = 0; index < samples; index++) writer.Write((short)(Math.Sin(index * Math.Tau * 440 / samples) * 1000));
        }
        string asset = ResourceNames.Resolve(fixture.Root, "Part audio", ResourceType.Audio);
        string metadata = "{\"Source\":\"Assets/Audio/Part tone.wav\",\"Volume\":0.5,\"Loop\":true,\"Spatial\":false,\"MinDistance\":1,\"MaxDistance\":20}";
        File.WriteAllText(asset, metadata);
        using XAudioSystem audio = new(fixture.Root);
        using RuntimeScene scene = new();
        var entity = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room).EntitiesByNodeId[fixture.NodeId];
        ObjectCompositionSubsystem composition = scene.AddSubsystem(new ObjectCompositionSubsystem(fixture.Root, audio));
        GameTime time = new(); time.Step(1f / 60);
        composition.Update(scene, time);
        IDictionary channels = (IDictionary)typeof(XAudioSystem).GetField("_channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(audio)!;
        int id = channels.Keys.Cast<int>().Single();
        AudioChannel channel = new(id);
        float Volume(int channelId)
        {
            object state = channels[channelId]!;
            var voice = (Vortice.XAudio2.IXAudio2SourceVoice)state.GetType().GetField("Voice")!.GetValue(state)!;
            return voice.Volume;
        }
        Assert(composition.ActiveAudioCount == 1 && audio.IsPlaying(channel) && Volume(id) == 0,
            "The saved emitter did not start a native voice or discarded its authored mute.");
        TransformComponent transform = scene.World.GetRef<TransformComponent>(entity);
        ref AudioComponent component = ref scene.World.GetRef<AudioComponent>(entity);
        component.Volume = .4f;
        composition.Update(scene, time);
        audio.SetListener(new Vector3(transform.X + 6, transform.Y, transform.Z), -Vector3.UnitZ);
        float expected = .4f * .5f * (1 - MathF.Log10(5.5f));
        Assert(Math.Abs(Volume(id) - expected) < .001f, "The native mixer ignored saved emitter distances, logarithmic falloff or the Audio asset gain.");
        AudioChannel independent = audio.Play(audio.LoadSound("Part audio"), loop: true);
        Assert(Math.Abs(Volume(independent.Id) - .5f) < .001f && File.ReadAllText(asset) == metadata,
            "An emitter override mutated the reusable sound or another playback channel.");
        audio.SetListener(new Vector3(transform.X + 20, transform.Y, transform.Z), -Vector3.UnitZ);
        Assert(Volume(id) == 0 && Math.Abs(Volume(independent.Id) - .5f) < .001f, "Distance attenuation leaked between channels or failed at the outer radius.");
        component.Spatial = false; composition.Update(scene, time);
        Assert(Math.Abs(Volume(id) - .2f) < .001f && channels.Count == 2, "Changing to 2D mode did not remove attenuation without restarting playback.");
        component.Volume = 0; composition.Update(scene, time);
        Assert(Volume(id) == 0 && channels.Count == 2, "A live mute restarted the voice or failed to silence it.");
        scene.World.DestroyEntity(entity); scene.World.FlushDeferred(); composition.Update(scene, time);
        Assert(composition.ActiveAudioCount == 0 && !audio.IsPlaying(channel), "Removing a terrain part left its native audio playing.");
        audio.Stop(independent);
    }

    private static void Contacts(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Contacts");
        File.WriteAllText(fixture.Script, "var entered = 0; var exited = 0;");
        string enter = fixture.Resources.CreateResource(fixture.Resources.AssetsRoot, ResourceKind.PgslScript, "Enter part");
        string leave = fixture.Resources.CreateResource(fixture.Resources.AssetsRoot, ResourceKind.PgslScript, "Leave part");
        File.WriteAllText(enter, "entered += 1;"); File.WriteAllText(leave, "exited += 1;");
        fixture.Document.Components.Single(component => component.Type == "Condition").Enabled = false;
        TerrainEntityComponent scripted = fixture.Document.Components.Single(component => component.Type == "Script");
        scripted.Props = new() { ["Script"] = "Part behavior" };
        TerrainEntityComponent physics = fixture.Document.Components.Single(component => component.Type == "Physics");
        physics.Props["IsTrigger"] = "true"; physics.Props["OnEnterScript"] = "Enter part"; physics.Props["OnExitScript"] = "Leave part";
        fixture.SaveDocument();
        VMEngine.Initialize(); ScriptHostSystem scripts = new();
        using RuntimeScene scene = new();
        scripts.SetContext(new NullGameContext { World = scene.World, Room = fixture.Room, ProjectPath = fixture.Root });
        var part = new RoomSceneBuilder(fixture.Root, scripts).Build(scene, fixture.Room).EntitiesByNodeId[fixture.NodeId];
        scene.AddSubsystem(new ScriptHostSubsystem(scripts));
        scene.UpdateVariable(1f / 60);
        PgslBehavior behavior = (PgslBehavior)scripts.FindBehaviorForEntity(part);
        double Count(string key) => Convert.ToDouble(behavior.GetVariablesSnapshot()[key]);
        Vector3 position = scene.World.GetRef<Transform3DComponent>(part).Position;
        var visitor = scene.World.CreateEntity();
        scene.World.Set(visitor, new EntityLifecycleComponent { Enabled = true });
        scene.World.Set(visitor, new TransformComponent { X = position.X, Y = position.Y, Z = position.Z, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        scene.World.Set(visitor, new Transform3DComponent { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One });
        RigidBodyComponent body = RigidBodyComponent.DynamicBox(new Vector3(.2f)); body.Flags &= ~RigidBodyFlags.UseGravity;
        scene.World.Set(visitor, body);
        scene.UpdateFixed(1f / 60);
        Assert(Count("entered") == 1 && Count("exited") == 0 && scripts.RecentDiagnostics.Count == 0,
            "Actual trigger overlap did not invoke the saved enter script: " + string.Join(';', scripts.RecentDiagnostics));
        for (int frame = 0; frame < 90; frame++) scene.UpdateFixed(1f / 60);
        Assert(Count("entered") == 1 && Count("exited") == 0
            && Vector3.Distance(scene.World.GetRef<Transform3DComponent>(visitor).Position, position) < .001f,
            "A resting trigger fired duplicate transitions, lost a sleeping contact, or applied collision response.");
        int registration = scene.World.GetRef<RigidBodyComponent>(visitor).RegistrationId;
        scene.Physics!.SetBodyPose(scene.World, registration, position + new Vector3(10, 0, 0), Quaternion.Identity);
        scene.UpdateFixed(1f / 60); scene.UpdateFixed(1f / 60);
        Assert(Count("entered") == 1 && Count("exited") == 1, "Leaving a trigger did not invoke exactly one saved exit script.");
        scene.Physics.SetBodyPose(scene.World, registration, position, Quaternion.Identity); scene.UpdateFixed(1f / 60);
        Assert(Count("entered") == 2 && Count("exited") == 1, "Returning to a trigger did not begin a new overlap.");
        for (int frame = 0; frame < 90; frame++) scene.UpdateFixed(1f / 60);
        scene.World.GetRef<RigidBodyComponent>(part).CollisionMask = 1;
        scene.World.GetRef<RigidBodyComponent>(visitor).CollisionLayer = 6;
        scene.UpdateFixed(1f / 60);
        Assert(Count("entered") == 2 && Count("exited") == 2, "Changed collision filters retained a sleeping overlap.");
        scene.World.GetRef<RigidBodyComponent>(part).CollisionMask = 0x7F;
        scene.Physics.SetBodyPose(scene.World, registration, position, Quaternion.Identity); scene.UpdateFixed(1f / 60);
        Assert(Count("entered") == 3, "Restoring collision filters did not permit a new overlap.");
        scene.World.DestroyEntity(visitor); scene.World.FlushDeferred(); scene.UpdateFixed(1f / 60);
        Assert(Count("exited") == 3 && scene.Physics.ContactChanges.Any(contact => contact.Phase == Genesis.Physics.PhysicsContactPhase.Exit),
            "Removing an overlapping entity left a ghost collider or omitted its exit event.");
        var kinematic = scene.World.CreateEntity();
        scene.World.Set(kinematic, new EntityLifecycleComponent { Enabled = true });
        scene.World.Set(kinematic, new Transform3DComponent { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One });
        body.Motion = PhysicsMotionType.Kinematic; body.RegistrationId = 0;
        scene.World.Set(kinematic, body); scene.UpdateFixed(1f / 60);
        Assert(Count("entered") == 4 && scene.Physics.Raycast(scene.World, position + new Vector3(0, 0, 5), -Vector3.UnitZ, 10, out var hit, part)
            && hit.Entity == kinematic && !hit.IsStatic, "A kinematic body lost its identity in trigger events or raycasts.");
    }

    private static void MeshCollider(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Mesh");
        Vector3[] positions = [new(0, 0, -1), new(2, 0, -1), new(2, 2, -1), new(0, 0, 1), new(2, 0, 1), new(2, 2, 1)];
        GModelAsset wedge = new() { Name = "Saved ramp", Pivot = new() { Position = new(.25f, .1f, .2f) } };
        wedge.Meshes.Add(new GModelMesh
        {
            Vertices = positions.Select(position => new Genesis.Shared.Interfaces.MeshVertex { Position = position, Normal = Vector3.UnitY }).ToArray(),
            Indices = [0, 2, 1, 3, 4, 5, 0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4, 0, 3, 5, 0, 5, 2],
        });
        wedge.RecalculateBounds(); File.WriteAllText(fixture.Model, JsonConvert.SerializeObject(wedge));
        fixture.Document.Components.Single(component => component.Type == "Model").Props["Scale"] = "1.5";
        TerrainEntityComponent physics = fixture.Document.Components.Single(component => component.Type == "Physics");
        physics.Props["Shape"] = "Mesh"; fixture.SaveDocument();
        TerrainNatureDocument nature = TerrainNatureSerializer.LoadOrDefault(fixture.Terrain);
        nature.PlacedEntities.Single().Scale = 2; nature.PlacedEntities.Single().Yaw = 90;
        TerrainNatureSerializer.Save(fixture.Terrain, nature);
        using (RuntimeScene scene = new())
        {
            var entity = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room).EntitiesByNodeId[fixture.NodeId];
            scene.UpdateFixed(1f / 60);
            Transform3DComponent transform = scene.World.GetRef<Transform3DComponent>(entity);
            Vector3 expected = Vector3.Transform((new Vector3(.5f, .5f, 0) - wedge.Pivot.Position) * 3, transform.Rotation) + transform.Position;
            Assert(scene.Physics!.RaycastDown(scene.World, expected + Vector3.UnitY * 10, 20, out var hit)
                && hit.Entity == entity && Vector3.Distance(hit.Point, expected) < .001f,
                "Mesh collision used a bounding box or lost the saved pivot, model scale, placement scale or rotation.");
            Assert(Math.Abs(scene.World.GetRef<RigidBodyComponent>(entity).Mass - 216) < .01f,
                "Mesh density used the bounding box volume instead of the closed ramp volume.");
        }
        physics.Props["AffectedByGravity"] = "true"; fixture.SaveDocument();
        using (RuntimeScene scene = new())
        {
            var entity = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room).EntitiesByNodeId[fixture.NodeId];
            scene.Physics!.GravityStrength = 0; scene.Physics.AirDrag = 0;
            Vector3 original = scene.World.GetRef<Transform3DComponent>(entity).Position;
            for (int frame = 0; frame < 4; frame++) scene.UpdateFixed(1f / 60);
            int id = scene.World.GetRef<RigidBodyComponent>(entity).RegistrationId;
            Assert(Vector3.Distance(scene.Physics.GetBodyPosition(id), original) < .001f
                && Vector3.Distance(scene.World.GetRef<Transform3DComponent>(entity).Position, original) < .001f,
                "Dynamic mesh recentering moved the authored entity origin.");
            scene.Physics.SetBodyPose(scene.World, id, original + Vector3.UnitY * 2, Quaternion.Identity);
            scene.UpdateFixed(1f / 60);
            Assert(Vector3.Distance(scene.World.GetRef<Transform3DComponent>(entity).Position, original + Vector3.UnitY * 2) < .001f,
                "Moving a dynamic mesh applied its center offset twice.");
            scene.Physics.ApplyLinearImpulse(scene.World, id, Vector3.UnitY * 216); scene.UpdateFixed(1f / 60);
            Assert(scene.World.GetRef<Transform3DComponent>(entity).Position.Y > original.Y + 2.01f,
                "The authored dynamic mesh did not respond to a native physics impulse.");
        }
        physics.Props["Shape"] = "Box"; physics.Props["AffectedByGravity"] = "false"; fixture.SaveDocument();
        using (RuntimeScene scene = new())
        {
            var entity = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room).EntitiesByNodeId[fixture.NodeId];
            scene.UpdateFixed(1f / 60);
            Transform3DComponent transform = scene.World.GetRef<Transform3DComponent>(entity);
            Vector3 expected = Vector3.Transform((new Vector3(1, 2, 0) - wedge.Pivot.Position) * 3, transform.Rotation) + transform.Position;
            Assert(scene.Physics!.RaycastDown(scene.World, expected + Vector3.UnitY * 10, 20, out var hit)
                && hit.Entity == entity && Vector3.Distance(hit.Point, expected) < .001f,
                "The primitive collider ignored the off-center model bounds or pivot.");
        }
    }

    private static void ImageMaterials(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Materials");
        string image = fixture.Resources.CreateResource(fixture.Resources.AssetsRoot, ResourceKind.Image, "Part material");
        ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(image).Document, image, ImageDocumentAccess.Editor);
        ImageWorkspace workspace = ImageWorkspace.CreateBlank(16, 16, Color.Red);
        workspace.AddLayer("Normal").Channel = ImageMaterialChannel.Normal;
        workspace.AddLayer("Roughness").Channel = ImageMaterialChannel.Roughness;
        workspace.AddLayer("AO").Channel = ImageMaterialChannel.Occlusion;
        workspace.AddLayer("Metallic").Channel = ImageMaterialChannel.Metallic;
        workspace.AddFrame(duplicateCurrent: true);
        byte[] Solid(byte red, byte green, byte blue)
        {
            byte[] pixels = new byte[16 * 16 * 4];
            for (int index = 0; index < pixels.Length; index += 4)
            { pixels[index] = red; pixels[index + 1] = green; pixels[index + 2] = blue; pixels[index + 3] = 255; }
            return pixels;
        }
        for (int frame = 0; frame < 2; frame++)
        {
            foreach (ImageLayerBuffer layer in workspace.Frames[frame].Layers)
                layer.Pixels = layer.Channel switch
                {
                    ImageMaterialChannel.Color => frame == 0 ? Solid(255, 0, 0) : Solid(0, 0, 255),
                    ImageMaterialChannel.Normal => frame == 0 ? Solid(128, 128, 255) : Solid(128, 255, 128),
                    ImageMaterialChannel.Roughness => frame == 0 ? Solid(60, 60, 60) : Solid(180, 180, 180),
                    ImageMaterialChannel.Occlusion => Solid(210, 210, 210),
                    _ => Solid(30, 30, 30),
                };
        }
        ImageWorkspaceStorage.Save(session, workspace);
        for (int frame = 0; frame < 2; frame++)
        {
            ImageMaterialPixels pixels = ImageMaterialAssetLoader.Load(fixture.Root, "Part material", frame);
            Assert(pixels.Albedo.SequenceEqual(workspace.CompositeCurrentFrameFor(frame).Pixels)
                && pixels.Normal.SequenceEqual(workspace.CompositeCurrentFrameFor(frame, channel: ImageMaterialChannel.Normal).Pixels)
                && pixels.Orm[0] == 210 && pixels.Orm[1] == (frame == 0 ? 60 : 180) && pixels.Orm[2] == 30,
                "Saved Image channel pixels did not reach the runtime material for frame " + frame);
        }
        foreach (TerrainEntityComponent component in fixture.Document.Components) component.Enabled = false;
        fixture.Document.Components.Add(new() { Type = "Texture", Props = new()
        { ["Texture"] = "Part material", ["Mode"] = "Plane3D", ["Scale"] = "2", ["AnimationFps"] = "2" } });
        fixture.SaveDocument();
        using Form renderHost = UnattendedWindowing.NewHost(320, 240);
        UnattendedWindowing.ShowWithoutFocus(renderHost);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.Software);
        renderer.Initialize(renderHost.Handle, 320, 240);
        using RuntimeScene scene = new();
        var entity = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room).EntitiesByNodeId[fixture.NodeId];
        ObjectCompositionSubsystem composition = scene.AddSubsystem(new ObjectCompositionSubsystem(fixture.Root));
        Vector3 position = scene.World.GetRef<Transform3DComponent>(entity).Position;
        Vector3 eye = position + new Vector3(0, 1, 5), target = position + Vector3.UnitY;
        MeshDrawCall[] draws = new MeshDrawCall[64];
        MeshDrawCall Render(string name, bool blue)
        {
            renderer.BeginFrame(); renderer.Clear(.03f, .03f, .03f); renderer.Set3DFrameActive(true);
            renderer.SetCamera3D(Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY), Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 4f / 3, .1f, 100));
            int count = 0;
            ObjectDrawPass.SubmitMeshes3D(scene.World, fixture.Root, draws, ref count, renderer, eye, Vector3.Normalize(target - eye));
            Assert(count == 1 && draws[0].NormalMap.IsValid && draws[0].OrmMap.IsValid, "The Image plane lost its material channels or gained duplicate geometry.");
            renderer.DrawMesh(draws[0]); renderer.EndFrame();
            Assert(renderer.TryReadFramePixels(out int width, out int height, out byte[] pixels), "The material runtime produced no pixels.");
            int colored = 0;
            for (int index = 0; index < pixels.Length; index += 4)
                if (blue ? pixels[index] > 80 && pixels[index] > pixels[index + 2] * 2 : pixels[index + 2] > 80 && pixels[index + 2] > pixels[index] * 2) colored++;
            using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { bitmap.UnlockBits(data); }
            bitmap.Save(Path.Combine(ctx.Captures, name + ".png"));
            ctx.Report.Images.Add(new ImageResult(name, name + ".png", width, height, 0, 0));
            Assert(colored > 500, "The saved animated Image frame was not visible in gameplay: " + name);
            return draws[0];
        }
        MeshDrawCall first = Render("terrain-image-runtime-red", blue: false);
        GameTime time = new();
        for (int frame = 0; frame < 13; frame++) { time.Step(.05f); composition.Update(scene, time); }
        Assert(scene.World.GetRef<SpriteComponent>(entity).ImageIndex == 1, "Terrain texture playback ignored its authored frame rate.");
        MeshDrawCall second = Render("terrain-image-runtime-blue", blue: true);
        Assert(!second.Texture.Equals(first.Texture) && !second.NormalMap.Equals(first.NormalMap) && !second.OrmMap.Equals(first.OrmMap),
            "Material animation advanced albedo without its frame-specific normal/ORM channels.");
        string saved = File.ReadAllText(image); File.WriteAllText(image, "{"); File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddSeconds(3));
        MeshDrawCall retained = Render("terrain-image-invalid-keeps-last-frame", blue: true);
        Assert(retained.Texture.Equals(second.Texture) && retained.NormalMap.Equals(second.NormalMap) && retained.OrmMap.Equals(second.OrmMap),
            "An invalid live Image replacement discarded the last valid material frame.");
        File.WriteAllText(image, saved); File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddSeconds(4));
        MeshDrawCall recovered = Render("terrain-image-repaired-frame", blue: true);
        Assert(!recovered.Texture.Equals(second.Texture), "Repairing the saved Image did not resume live material updates.");
        fixture.Document.Components.Single(component => component.Type == "Model").Enabled = true; fixture.SaveDocument();
        using RuntimeScene modeled = new();
        var modelEntity = new RoomSceneBuilder(fixture.Root).Build(modeled, fixture.Room).EntitiesByNodeId[fixture.NodeId];
        int modelCount = 0;
        ObjectDrawPass.SubmitMeshes3D(modeled.World, fixture.Root, draws, ref modelCount, renderer, eye, Vector3.Normalize(target - eye));
        Assert(modelCount == 1 && draws[0].Texture.IsValid && draws[0].NormalMap.IsValid && draws[0].OrmMap.IsValid,
            "Combining Model and Texture added an extra plane or failed to apply the Image material to the Model.");
        ObjectDrawPass.InvalidateAssets(renderer);
    }

    private static void TextureModes(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "TextureModes");
        string image = fixture.Resources.CreateResource(fixture.Resources.AssetsRoot, ResourceKind.Image, "Sprite prop");
        ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(image).Document, image, ImageDocumentAccess.Editor);
        ImageWorkspace workspace = ImageWorkspace.CreateBlank(32, 16, Color.Transparent);
        byte[] pixels = workspace.Frames[0].Layers[0].Pixels;
        for (int y = 2; y < 14; y++)
        for (int x = 2; x < 30; x++)
        {
            if (x is > 10 and < 21 && y is > 5 and < 10) continue;
            int index = (y * 32 + x) * 4; pixels[index] = 255; pixels[index + 3] = 255;
        }
        ImageWorkspaceStorage.Save(session, workspace);
        var extrusion = TerrainTextureGeometry.Extruded(32, 16, pixels);
        Assert(extrusion.Vertices.Any(vertex => vertex.Position.Z < 0) && extrusion.Vertices.Any(vertex => vertex.Position.Z > 0)
            && extrusion.Vertices.Any(vertex => vertex.Normal == Vector3.UnitX)
            && !extrusion.Vertices.Any(vertex => vertex.Position.X > -.2f && vertex.Position.X < .2f
                && vertex.Position.Y > .4f && vertex.Position.Y < .6f && vertex.Normal == Vector3.UnitZ),
            "Extruded sprites lost their depth, side faces or transparent opening.");
        Assert(extrusion.Vertices.Max(vertex => vertex.Position.X) - extrusion.Vertices.Min(vertex => vertex.Position.X) > 1.5f,
            "A wide sprite was squashed into a square.");
        foreach (TerrainEntityComponent component in fixture.Document.Components) component.Enabled = false;
        TerrainEntityComponent texture = new() { Type = "Texture", Props = new()
        { ["Texture"] = "Sprite prop", ["Mode"] = "Plane3D", ["Scale"] = "2" } };
        fixture.Document.Components.Add(texture); fixture.SaveDocument();
        fixture.Room.Nodes[0].Transform.RotationY = 90;
        using RuntimeScene scene = new();
        var entity = new RoomSceneBuilder(fixture.Root).Build(scene, fixture.Room).EntitiesByNodeId[fixture.NodeId];
        ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets);
        Vector3 position = scene.World.GetRef<Transform3DComponent>(entity).Position;
        using TerrainAssetPreview preview = new(fixture.Root, () => fixture.Document);
        preview.Viewport.BackendOverride = RenderBackendOption.SilkNetDx11;
        using Form host = UnattendedWindowing.NewHost(800, 620);
        host.Controls.Add(preview); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
        preview.Viewport.Host.TimerEnabled = false;
        preview.Viewport.Camera.Target = Vector3.UnitY; preview.Viewport.Camera.Distance = 5;
        preview.Viewport.Camera.Pitch = -.15f; preview.Viewport.Camera.Yaw = .8f;
        MeshDrawCall? previous = null;
        foreach (string mode in new[] { "Plane3D", "Diagonal2D", "Billboard2D", "Extruded2D" })
        {
            texture.Set("Mode", mode); assets.TerrainTextureMode = mode;
            using Bitmap capture = CaptureReady(preview.Viewport, blue: false);
            SaveImage(ctx, capture, "terrain-texture-wizard-" + mode);
            object list = typeof(TerrainAssetPreview).GetField("_draws", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
            var calls = (List<MeshDrawCall>)list.GetType().GetField("Items")!.GetValue(list)!;
            Assert(calls.Count == 1 && calls[0].Texture.IsValid && calls[0].NormalMap.IsValid && calls[0].OrmMap.IsValid,
                "The live wizard lost texture material maps or duplicated geometry in " + mode);
            if (mode == "Extruded2D") Assert(previous.HasValue && !previous.Value.Mesh.Equals(calls[0].Mesh), "Extruded still uses the wizard's flat plane.");
            previous = calls[0];
            MeshDrawCall[] draws = new MeshDrawCall[16]; int count = 0;
            Vector3 eye = position + new Vector3(4, 1, 4);
            ObjectDrawPass.SubmitMeshes3D(scene.World, fixture.Root, draws, ref count, preview.Viewport.Host.Renderer!, eye, -Vector3.UnitZ);
            Assert(count == 1 && draws[0].Mesh.IsValid, "A runtime texture mode produced missing or duplicate geometry: " + mode);
            if (mode == "Billboard2D")
            {
                Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, draws[0].World));
                Assert(Vector3.Dot(normal, Vector3.Normalize(new Vector3(4, 0, 4))) > .999f,
                    "The billboard applied the terrain's yaw twice and faced away from the camera.");
            }
        }
        texture.Set("Mode", "Extruded2D"); fixture.SaveDocument();
        using (TerrainEditorControl terrain = new(fixture.Terrain, fixture.Root))
        using (Form terrainHost = UnattendedWindowing.NewHost(1440, 900))
        {
            terrain.Viewport.BackendOverride = RenderBackendOption.SilkNetDx11;
            terrain.Dock = DockStyle.Fill; terrainHost.Controls.Add(terrain); ThemeService.Apply(terrainHost);
            UnattendedWindowing.ShowWithoutFocus(terrainHost); terrain.Viewport.Host.TimerEnabled = false;
            Vector3 local = TerrainNatureSerializer.LoadOrDefault(fixture.Terrain).PlacedEntities.Single().Position;
            terrain.Viewport.Camera.Target = local + Vector3.UnitY; terrain.Viewport.Camera.Distance = 6;
            terrain.Viewport.Camera.Yaw = .8f; terrain.Viewport.Camera.Pitch = -.15f;
            using Bitmap red = CaptureReady(terrain.Viewport, blue: false); SaveImage(ctx, red, "terrain-texture-canvas-extruded");
            for (int index = 0; index < pixels.Length; index += 4) { pixels[index + 2] = pixels[index]; pixels[index] = 0; }
            ImageWorkspaceStorage.Save(session, workspace);
            File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddSeconds(2));
            using Bitmap blue = CaptureReady(terrain.Viewport, blue: true); SaveImage(ctx, blue, "terrain-texture-canvas-live-blue");
            using Bitmap wizardBlue = CaptureReady(preview.Viewport, blue: true); SaveImage(ctx, wizardBlue, "terrain-texture-wizard-live-blue");
            MethodInfo pick = typeof(TerrainEditorControl).GetMethod("PickScene", BindingFlags.Instance | BindingFlags.NonPublic)!;
            string placedId = terrain.Nature.PlacedEntities.Single().Id;
            string? Pick(Vector3 world)
            {
                Vector3 surface = terrain.Viewport.WorldToSurface(world);
                return ((TerrainComponentsPanel.ComponentKind? Kind, string? Id))pick.Invoke(terrain, [new Point((int)surface.X, (int)surface.Y)])! is var result ? result.Id : null;
            }
            Assert(Pick(local + new Vector3(-1.4f, 1, .12f)) == placedId, "The wide extruded sprite cannot be selected where it is visibly drawn.");
            Assert(Pick(local + new Vector3(0, 1, .12f)) != placedId, "Clicking through the transparent opening still selects an invisible rectangle.");
        }
        ObjectDrawPass.InvalidateAssets(preview.Viewport.Host.Renderer!);

        static Bitmap CaptureReady(Genesis.Application.Editors.Suite.EditorViewport3D viewport, bool blue)
        {
            var deadline = DateTime.UtcNow.AddSeconds(6);
            do
            {
                System.Windows.Forms.Application.DoEvents();
                Bitmap? frame = viewport.CaptureFrame(2);
                if (frame is not null)
                {
                    int colored = 0;
                    for (int y = 70; y < frame.Height - 50; y += 3)
                    for (int x = 40; x < frame.Width - 40; x += 3)
                    {
                        Color color = frame.GetPixel(x, y);
                        if (blue ? color.B > 60 && color.B > color.R * 1.8 && color.B > color.G * 1.5
                            : color.R > 60 && color.R > color.B * 1.8 && color.R > color.G * 1.5) colored++;
                    }
                    if (colored > 30) return frame;
                    frame.Dispose();
                }
                Thread.Sleep(25);
            } while (DateTime.UtcNow < deadline);
            throw new InvalidDataException("A saved texture did not reach the native authoring preview: " + (blue ? "blue" : "red"));
        }
    }

    private static void Particles(HeadlessContext ctx)
    {
        using Fixture fixture = new(ctx, "Particles");
        foreach (TerrainEntityComponent component in fixture.Document.Components) component.Enabled = component.Type is "ParticleEmitter" or "Script";
        fixture.SaveDocument();
        File.WriteAllText(fixture.Script, "if (VariableGet(\"initialized\") == 0) { VariableSet(\"rate\", 40); VariableSetBool(\"emitting\", true); VariableSet(\"initialized\", 1); }\nParticleAttachedSetRate(VariableGet(\"rate\")); ParticleAttachedSetEmitting(VariableGetBool(\"emitting\"));\n");
        string particleFile = ResourceNames.Resolve(fixture.Root, "Part particles", ResourceType.Particle);
        ParticleConfig config = new() { EmitRate = 5, Lifetime = .3, LifetimeVariance = 0, StartSize = .3, EndSize = .2,
            Speed = 0, SpeedVariance = 0, Gravity = 0, EmitRadius = .4, Shape = ParticleEmitShape.Disc, MaxParticles = 200 };
        File.WriteAllText(particleFile, System.Text.Json.JsonSerializer.Serialize(config));
        VMEngine.Initialize(); ScriptHostSystem scripts = new();
        using RuntimeScene scene = new();
        ProjectGameContext game = new(fixture.Root, scene, null, null, fixture.Room, null);
        scripts.SetContext(game);
        var entity = new RoomSceneBuilder(fixture.Root, scripts).Build(scene, fixture.Room).EntitiesByNodeId[fixture.NodeId];
        PgslBehavior behavior = (PgslBehavior)scripts.FindBehaviorForEntity(entity);
        ObjectCompositionSubsystem composition = scene.AddSubsystem(new ObjectCompositionSubsystem(fixture.Root));
        Vector3 position = scene.World.GetRef<Transform3DComponent>(entity).Position;
        scene.Camera3D.Position = position + new Vector3(0, 1, 5); scene.Camera3D.Pitch = -.15f;
        using Form host = UnattendedWindowing.NewHost(320, 240); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.Software); renderer.Initialize(host.Handle, 320, 240);
        GameTime time = new(); MeshDrawCall[] draws = new MeshDrawCall[16];
        void Step(int frames)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                time.Step(1f / 60); scripts.Update(1f / 60); composition.Update(scene, time);
                renderer.BeginFrame(); renderer.Clear(.02f, .02f, .02f); renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(scene.Camera3D.ViewMatrix, Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 4f / 3, .1f, 100));
                int count = 0; composition.SubmitMeshes(scene, draws, ref count, renderer); renderer.EndFrame();
            }
        }
        Step(60);
        Assert(composition.ParticleEmitterCount == 1 && composition.ActiveParticleCount >= 8,
            "A saved private part script did not drive actual attached particle emission.");
        Assert(behavior.TrySetLiveValue("rate", 0d), "The retained part script cannot change its particle rate.");
        Step(60);
        Assert(composition.ParticleEmitterCount == 1 && composition.ActiveParticleCount == 0,
            $"Setting a live particle rate to zero ignored the command or restarted the emitter: emitters={composition.ParticleEmitterCount}, live={composition.ActiveParticleCount}, component={scene.World.GetRef<ParticleComponent>(entity).EmitRate}, override={scene.World.GetRef<ParticleComponent>(entity).HasEmitRateOverride}, script={behavior.GetVariablesSnapshot()["rate"]}.");
        Assert(behavior.TrySetLiveValue("rate", 80d), "The retained script lost its live rate variable.");
        Step(60);
        Assert(composition.ActiveParticleCount >= 16, "A live rate edit did not increase existing emitter output.");
        Assert(behavior.TrySetLiveValue("emitting", false), "The retained script lost its live emission toggle."); Step(1);
        Assert(composition.ParticleEmitterCount == 0, "The saved script could not stop its attached emitter.");
        Assert(behavior.TrySetLiveValue("emitting", true), "The retained script lost its emission toggle after stopping."); Step(30);
        Assert(composition.ActiveParticleCount >= 16, "The saved script could not restart its attached emitter.");
        scene.World.DestroyEntity(entity); scene.World.FlushDeferred(); Step(1);
        Assert(composition.ParticleEmitterCount == 0 && composition.ActiveParticleCount == 0,
            "Destroying a private part left its particle effect alive.");
        Assert(ParticleAssetLoader.Load(fixture.Root, "Part particles").EmitRate == 5, "Runtime rate edits modified the reusable particle resource.");
        scripts.Shutdown();
    }

    private static void SaveImage(HeadlessContext ctx, Bitmap bitmap, string name)
    {
        bitmap.Save(Path.Combine(ctx.Captures, name + ".png"));
        ctx.Report.Images.Add(new ImageResult(name, name + ".png", bitmap.Width, bitmap.Height, 0, 0));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(HeadlessContext ctx, string suffix)
        {
            ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "PartRuntime" + suffix), "Terrain parts " + suffix);
            Root = project.RootPath; Resources = new ResourceService(project);
            Terrain = Resources.CreateResource(Resources.AssetsRoot, ResourceKind.Terrain, "Landscape");
            Script = Resources.CreateResource(Resources.AssetsRoot, ResourceKind.PgslScript, "Part behavior");
            File.WriteAllText(Script, ScriptSource);
            Resources.CreateResource(Resources.AssetsRoot, ResourceKind.Particle, "Part particles");
            Resources.CreateResource(Resources.AssetsRoot, ResourceKind.Audio, "Part audio");
            Resources.CreateResource(Resources.AssetsRoot, ResourceKind.Shader, "Part shader");
            using (TerrainEditorControl editor = new(Terrain, Root))
            {
                Part = editor.CreateTerrainEntity(TerrainEntityType.Object);
                editor.ActiveEntityWizard!.SaveAndCloseForTest(); editor.Save();
            }
            Model = Path.Combine(Terrain + ".parts", "owned-section.gmodel");
            GModelAsset model = GModelPrimitiveFactory.CreateCube("Owned section", 2);
            model.Materials[0].BaseColor = new Vector4(.9f, .12f, .08f, 1);
            File.WriteAllText(Model, JsonConvert.SerializeObject(model));
            Document = new TerrainEntityDocument { Name = "Forest prop", Type = TerrainEntityType.Object, Components =
            [
                Component("Model", ("Model", Path.GetRelativePath(Root, Model).Replace('\\', '/')), ("AnimationClip", "Idle"), ("AnimationFps", "24"), ("Loop", "false")),
                Component("Physics", ("Shape", "Box"), ("AffectedByGravity", "false"), ("Friction", ".35"), ("Restitution", ".2"), ("Density", "2")),
                Component("Script", ("Script", "Part behavior"), ("Variables.speed", "2"), ("Variables.active", "true"), ("Variables.label", "Forest patrol")),
                Component("Condition", ("If", "active"), ("ThenSource", "AnimationPlay(\"Wave\", true);"), ("ElseSource", "AnimationPlay(\"Idle\", false);")),
                Component("ParticleEmitter", ("Particle", "Part particles")), Component("AudioEmitter", ("Audio", "Part audio")),
                Component("Shader", ("Shader", "Part shader"), ("Parameter.Strength.0", ".75")),
            ] };
            SaveDocument();
            using (TerrainEditorControl editor = new(Terrain, Root))
            {
                string id = editor.PlaceTerrainEntity(Part, 4, 3); NodeId = "landscape:" + id; editor.Save();
            }
            Room = RoomAsset.Create("Terrain parts", RoomDimension.ThreeD);
            Room.Environment.DynamicSky = false;
            Room.Nodes.Add(new RoomNode { Id = "landscape", Name = "Landscape", Kind = RoomNodeKind.Terrain,
                LayerId = Room.Layers[0].Id, Terrain = new RoomTerrainData { Asset = ResourceNames.Name(Root, Terrain, ResourceType.Terrain) } });
        }
        private static TerrainEntityComponent Component(string type, params (string Key, string Value)[] props) =>
            new() { Type = type, Props = props.ToDictionary(item => item.Key, item => item.Value) };
        public string Root { get; }
        public ResourceService Resources { get; }
        public string Terrain { get; }
        public string Part { get; }
        public string Model { get; }
        public string Script { get; }
        public string NodeId { get; }
        public RoomAsset Room { get; }
        public TerrainEntityDocument Document { get; }
        public string ScriptSource => "var speed = 1; var active = false; var label = \"Default\";\nx += speed; z = speed;\n";
        public void SaveDocument() => File.WriteAllText(Part, System.Text.Json.JsonSerializer.Serialize(Document));
        public void Dispose() => ObjectDrawAssetRegistry.Clear();
    }
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
