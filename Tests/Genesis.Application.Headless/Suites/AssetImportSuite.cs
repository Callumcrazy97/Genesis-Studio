using System.Drawing;
using System.Drawing.Imaging;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Audio;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>What a game's own pictures and sounds become when they are brought into a project.</summary>
internal static class AssetImportSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "AssetImport");
        string parent = Path.Combine(ctx.Workspace, "AssetImport");
        Directory.CreateDirectory(parent);
        ProjectService service = new();
        ProjectSession project = service.CreateProject(parent, "Import", "Blank");
        ResourceService resources = new(project);

        HeadlessHarness.RunCase(ctx.Report, "Engine.Assets.AnImportedPictureKeepsItsOwnSize", () =>
        {
            string picture = Path.Combine(parent, "Banner.png");
            using (Bitmap bitmap = new(200, 120))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.SteelBlue);
                bitmap.Save(picture, ImageFormat.Png);
            }
            string imported = resources.ImportFiles(resources.AssetsRoot, [picture]).Single();
            ImageDocument document = ImageDocumentSerializer.Deserialize(File.ReadAllText(imported)).Document;
            Check(document.Canvas.Width == 200 && document.Canvas.Height == 120,
                $"An imported 200 x 120 picture got a {document.Canvas.Width} x {document.Canvas.Height} canvas.");

            // And a script asking the Image its size gets the same.
            string name = Path.GetFileName(imported).Split('.')[0];
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(project.RootPath);
            Check(Genesis.Runtime.Rendering.ObjectDrawPass.TryGetImageFrameSize(project.RootPath, name, out int width, out int height)
                && width == 200 && height == 120, $"SpriteWidth/SpriteHeight of '{name}' gave {width} x {height}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Audio.OggVorbisIsDecodedLikeWav", () =>
        {
            string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-440-660.ogg");
            Check(File.Exists(fixture), "The Ogg Vorbis test tone is missing from the test output: " + fixture);
            PcmAudioClip clip = PcmAudioClip.Load(fixture);
            Check(clip.Channels == 2 && clip.SampleRate == 22050 && Math.Abs(clip.Duration - 0.5) < 0.02,
                $"The tone decoded as {clip.Channels} channels at {clip.SampleRate} Hz for {clip.Duration:F3} s (2, 22050, 0.5).");
            double peak = clip.Samples.Max(sample => Math.Abs((int)sample)) / (double)short.MaxValue;
            Check(peak > 0.3 && peak < 0.5, $"The decoded tone peaks at {peak:F2} of full scale; it was written at 0.40.");

            // Imported through Assets it becomes an Audio resource whose sound the game can read.
            string copy = Path.Combine(parent, "Tone.ogg");
            File.Copy(fixture, copy, overwrite: true);
            string audio = resources.ImportFiles(resources.AssetsRoot, [copy]).Single();
            string source = Directory.EnumerateFiles(Path.GetDirectoryName(audio)!, "*.ogg", SearchOption.AllDirectories)
                .FirstOrDefault(path => !string.Equals(path, copy, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The imported Audio resource kept no copy of its sound.");
            Check(PcmAudioClip.Load(source).Samples.Length == clip.Samples.Length, "The imported sound decodes differently from its source.");
        });

        RunHandedness(ctx, parent, service, project);
        RunFirstRoomPreparation(ctx, parent, project, resources);
        RunAnimationLibraries(ctx, parent, resources);
    }

    private static void RunHandedness(HeadlessContext ctx, string parent, ProjectService service, ProjectSession project)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Models.RightHandedImportIsMirroredAlongZ", () =>
        {
            // One triangle at z = 1 under a node moved 2 along z, in a glTF with an embedded buffer.
            byte[] buffer = new byte[44];
            float[] positions = [0, 0, 1, 1, 0, 1, 0, 1, 1];
            Buffer.BlockCopy(positions, 0, buffer, 0, 36);
            ushort[] indices = [0, 1, 2];
            Buffer.BlockCopy(indices, 0, buffer, 36, 6);
            string gltf = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
                "\"nodes\":[{\"mesh\":0,\"translation\":[0,0,2]}]," +
                "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]," +
                "\"buffers\":[{\"byteLength\":44,\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(buffer) + "\"}]," +
                "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":6}]," +
                "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,1],\"max\":[1,1,1]}," +
                "{\"bufferView\":1,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]}";
            string source = Path.Combine(parent, "Tri.gltf");
            File.WriteAllText(source, gltf);
            string resource = Path.Combine(parent, "Tri.model.json");
            GModelAsset plain = ExternalModelImporter.Import(source, parent, resource);
            GModelAsset mirrored = ExternalModelImporter.Import(source, parent, resource, convertRightHanded: true);
            MeshVertex[] a = plain.Meshes[0].Vertices, b = mirrored.Meshes[0].Vertices;
            ushort[] ia = plain.Meshes[0].Indices, ib = mirrored.Meshes[0].Indices;
            Check(!plain.ImportSettings.ConvertRightHanded && mirrored.ImportSettings.ConvertRightHanded,
                "The import settings do not record whether the model was mirrored.");
            Check(a.Length == b.Length && a.Zip(b).All(p => p.First.Position == new Vector3(p.Second.Position.X, p.Second.Position.Y, -p.Second.Position.Z)),
                "Mirrored vertex positions are not the plain ones with z negated.");
            Check(a.Zip(b).All(p => Vector3.Distance(p.First.Normal, new Vector3(p.Second.Normal.X, p.Second.Normal.Y, -p.Second.Normal.Z)) < 1e-4f),
                "Mirrored normals are not the plain ones with z negated.");
            Check(ia[0] == ib[0] && ia[1] == ib[2] && ia[2] == ib[1], "Mirrored triangles keep their winding, so their fronts face inward.");
            Check(Math.Abs(mirrored.Nodes[0].LocalTransform.Translation.Z + 2) < 1e-5f, "The node's position was not mirrored.");

            // Skinned and animated transforms stay consistent: mirroring the parts mirrors the result.
            Matrix4x4 bind = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(0.3f, -1f, 2f);
            Matrix4x4 frame = Matrix4x4.CreateRotationX(0.4f) * Matrix4x4.CreateTranslation(1f, 2f, -0.5f);
            Vector3 point = new(0.2f, 1.1f, 0.6f);
            Quaternion turn = Quaternion.Normalize(new Quaternion(0.2f, 0.5f, -0.3f, 0.8f));
            GModelAsset rigged = new() { Rig = new GModelRig { InverseBindMatrices = [bind] } };
            rigged.Meshes.Add(new GModelMesh { SkinnedVertices = [new SkinnedMeshVertex { Position = point }], Indices = [0, 0, 0] });
            rigged.Animations.Add(new GModelAnimationClip
            {
                Frames = [new GModelAnimationFrame { LocalBoneTransforms = [frame] }],
                Tracks = [new GModelAnimationTrack { Keys = [new GModelTrsKey { Rotation = turn }] }],
            });
            Vector3 before = Vector3.Transform(Vector3.Transform(point, bind), frame);
            GModelHandedness.ConvertFromRightHanded(rigged);
            Vector3 after = Vector3.Transform(Vector3.Transform(rigged.Meshes[0].SkinnedVertices[0].Position,
                rigged.Rig.InverseBindMatrices[0]), rigged.Animations[0].Frames[0].LocalBoneTransforms[0]);
            Check(Vector3.Distance(after, new Vector3(before.X, before.Y, -before.Z)) < 1e-4f,
                $"A skinned point moved to {after} after mirroring; expected {new Vector3(before.X, before.Y, -before.Z)}.");
            Matrix4x4 mirror = Matrix4x4.CreateScale(1, 1, -1);
            Matrix4x4 expected = mirror * Matrix4x4.CreateFromQuaternion(turn) * mirror;
            Matrix4x4 actual = Matrix4x4.CreateFromQuaternion(rigged.Animations[0].Tracks[0].Keys[0].Rotation);
            Check(Enumerable.Range(0, 16).All(i => Math.Abs(At(actual, i) - At(expected, i)) < 1e-4f),
                "A mirrored rotation key does not match its mirrored matrix.");

            // The project switch: off by default, and saved under the name the importer reads.
            Check(!Genesis.Runtime.Project.ProjectPaths.ReadConvertRightHandedModels(project.RootPath),
                "A new project mirrors imported models.");
            project.Manifest.ConvertRightHandedModels = true;
            service.Save(project);
            Check(Genesis.Runtime.Project.ProjectPaths.ReadConvertRightHandedModels(project.RootPath),
                "The project's convertRightHandedModels setting did not reach the importer.");
            project.Manifest.ConvertRightHandedModels = false;
            service.Save(project);
        });
        HeadlessHarness.RunCase(ctx.Report, "Engine.Models.GltfMaterialColourAndFactorsAreKept", () =>
        {
            // glTF's baseColorFactor is linear; a material colour is sRGB, so 0.9 / 0.25 / 0.12 is stored
            // as about 0.955 / 0.537 / 0.381 and draws as the same colour a glTF viewer shows.
            string source = AnimatedGlbFixture.Write(Path.Combine(parent, "GltfFactors"));
            GModelAsset imported = ExternalModelImporter.Import(source, parent, Path.Combine(parent, "GltfFactors", "Intake.model.json"));
            GModelMaterial warm = imported.Materials.First(m => m.Name == "Warm Cloth");
            Check(Math.Abs(warm.BaseColor.X - 0.9547f) < 2e-3f && Math.Abs(warm.BaseColor.Y - 0.5371f) < 2e-3f
                && Math.Abs(warm.BaseColor.Z - 0.3811f) < 2e-3f && warm.BaseColor.W == 1f,
                $"The linear base colour factor was stored as {warm.BaseColor}, not converted to sRGB.");
            Check(imported.Schema == GModelAsset.ImportSchema && imported.DrawsMaterialFactors(),
                $"A new import ({imported.Schema}) does not draw its materials' own factors.");
            Check(Math.Abs(warm.RoughnessFactor - 0.8f) < 1e-5f && warm.MetallicFactor == 0f,
                "The material's roughness and metallic factors were not imported.");
            Check(!new GModelAsset { Schema = "genesis.gmodel/2" }.DrawsMaterialFactors() && !new GModelAsset().DrawsMaterialFactors(),
                "A model imported before schema 3 changed how its materials draw.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Engine.Audio.AProjectNamesItsOwnBuses", () =>
        {
            Check(Genesis.Runtime.Project.ProjectPaths.ReadAudioBuses(project.RootPath).Count == 0,
                "A new project has audio buses of its own.");
            project.Manifest.AudioBuses = ["UI", "ambient", "ui"];
            service.Save(project);
            Check(Genesis.Runtime.Project.ProjectPaths.ReadAudioBuses(project.RootPath).SequenceEqual(["ui", "ambient"]),
                "The project's audioBuses did not read back once each, in lower case.");
            project.Manifest.AudioBuses = [];
            service.Save(project);
        });
        HeadlessHarness.RunCase(ctx.Report, "Engine.Models.ReimportFollowsSourceContentNotFileTimes", () =>
        {
            static string Triangle(float z)
            {
                byte[] buffer = new byte[44];
                float[] positions = [0, 0, z, 1, 0, z, 0, 1, z];
                Buffer.BlockCopy(positions, 0, buffer, 0, 36);
                ushort[] indices = [0, 1, 2];
                Buffer.BlockCopy(indices, 0, buffer, 36, 6);
                string zText = z.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                    "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]," +
                    "\"buffers\":[{\"byteLength\":44,\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(buffer) + "\"}]," +
                    "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":6}]," +
                    "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0," + zText + "],\"max\":[1,1," + zText + "]}," +
                    "{\"bufferView\":1,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]}";
            }
            string folder = Path.Combine(project.RootPath, "Assets", "Models", "Hashed");
            Directory.CreateDirectory(folder);
            string source = Path.Combine(folder, "source.gltf");
            string resource = Path.Combine(folder, "Hashed.model.json");
            File.WriteAllText(source, Triangle(1));
            File.WriteAllText(resource, "{ \"source\": \"source.gltf\" }");
            GModelAsset first = StudioModelResourceLoader.Load(resource);
            Check(first.Meshes.Count == 1 && File.ReadAllText(resource).Contains("sourceHash", StringComparison.Ordinal),
                "The first import did not record its source's hash.");

            // An edit to the cooked model, then a checkout that only gives the source a newer time.
            first.Materials.Add(new GModelMaterial { Name = "Edited" });
            StudioModelResourceLoader.SaveCanonical(resource, first);
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(5));
            Check(StudioModelResourceLoader.Load(resource).Materials.Any(m => m.Name == "Edited"),
                "A newer file time alone cooked the model again and lost its edits.");

            // A source whose content changed is cooked again.
            File.WriteAllText(source, Triangle(2));
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(10));
            Check(!StudioModelResourceLoader.Load(resource).Materials.Any(m => m.Name == "Edited"),
                "A changed source was not cooked again.");

            // The Player plays a changed source and writes nothing into the project.
            string canonical = StudioModelResourceLoader.CanonicalPath(resource);
            byte[] cooked = File.ReadAllBytes(canonical);
            string descriptor = File.ReadAllText(resource);
            File.WriteAllText(source, Triangle(3));
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(15));
            StudioModelResourceLoader.WriteReimportsToProject = false;
            try
            {
                GModelAsset played = StudioModelResourceLoader.Load(resource);
                Check(played.Meshes.Count == 1 && Math.Abs(played.Meshes[0].Vertices[0].Position.Z - 3f) < 1e-4f,
                    "The Player did not play the changed source.");
                Check(File.ReadAllBytes(canonical).AsSpan().SequenceEqual(cooked) && File.ReadAllText(resource) == descriptor,
                    "The Player wrote a model it cooked again into the project.");
            }
            finally
            {
                StudioModelResourceLoader.WriteReimportsToProject = true;
            }
        });
    }

    private static void RunFirstRoomPreparation(HeadlessContext ctx, string parent, ProjectSession project, ResourceService resources)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Startup.OnlyTheFirstRoomsAssetsArePreparedBeforeIt", () =>
        {
            string Picture(string name, Color color)
            {
                string file = Path.Combine(parent, name + ".png");
                using (Bitmap bitmap = new(16, 16))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
                    bitmap.Save(file, ImageFormat.Png);
                }
                return resources.ImportFiles(resources.AssetsRoot, [file]).Single();
            }
            string used = Picture("Backdrop", Color.SkyBlue);
            string unused = Picture("Leftover", Color.OrangeRed);

            // A Room whose one node shows the Backdrop Image.
            string room = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Arena");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(room))!.AsObject();
            string relative = Path.GetRelativePath(project.RootPath, used).Replace('\\', '/');
            json["nodes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "backdrop",
                ["kind"] = "Background",
                ["name"] = "Backdrop",
                ["enabled"] = true,
                ["background"] = new System.Text.Json.Nodes.JsonObject { ["asset"] = relative, ["mode"] = "TwoD" },
            });
            File.WriteAllText(room, json.ToJsonString());
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(project.RootPath);

            static bool Has(IReadOnlyList<string> paths, string stem) =>
                paths.Any(path => Path.GetFileName(path).StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    || path.Contains(Path.DirectorySeparatorChar + stem, StringComparison.OrdinalIgnoreCase));
            var warm = new Genesis.Runtime.Project.AssetWarmCache();
            warm.BuildCriticalList(project.RootPath, "Arena");
            Check(Has(warm.CriticalPaths, "Backdrop") && warm.CriticalPaths.Any(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)),
                "The first room's Image and its picture were not prepared: " + string.Join(", ", warm.CriticalPaths.Select(Path.GetFileName)));
            Check(!Has(warm.CriticalPaths, "Leftover"),
                "An Image no room uses was prepared before the first room.");
            Check(warm.Scope.Contains("Arena", StringComparison.Ordinal), "The preparation does not say it covers the first room.");

            string? previous = Environment.GetEnvironmentVariable(Genesis.Runtime.Project.AssetWarmCache.PreloadAllEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(Genesis.Runtime.Project.AssetWarmCache.PreloadAllEnvironmentVariable, "1");
                var all = new Genesis.Runtime.Project.AssetWarmCache();
                all.BuildCriticalList(project.RootPath, "Arena");
                Check(Has(all.CriticalPaths, "Leftover") && Has(all.CriticalPaths, "Backdrop"),
                    "GENESIS_PRELOAD_ALL=1 no longer prepares every asset.");
            }
            finally { Environment.SetEnvironmentVariable(Genesis.Runtime.Project.AssetWarmCache.PreloadAllEnvironmentVariable, previous); }

            // A model texture is not packed into the sprite sheets.
            var descriptor = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(unused))!.AsObject();
            descriptor["usage"]!["allowed"] = "texture";
            File.WriteAllText(unused, descriptor.ToJsonString());
            Check(Genesis.Runtime.Assets.SpriteAssetLoader.Load(unused).Usage.IsTextureOnly
                && !Genesis.Runtime.Assets.SpriteAssetLoader.Load(used).Usage.IsTextureOnly,
                "An Image allowed only as a texture is not told apart from a sprite.");

            // A GUI image keeps its place between the shapes and text drawn around it.
            var probe = new EditorInteractionRenderProbe();
            var hud = new OrderHud();
            var gui = new Genesis.Runtime.Scripting.PgslRenderDrawSurface(probe, hud, 1280, 720,
                projectPath: project.RootPath, isGui: true);
            gui.FillRectangle(Color.Black, new RectangleF(0, 0, 200, 100));
            gui.DrawSpriteRectangle("Backdrop", new RectangleF(10, 10, 64, 64), 0, Color.White, 1f);
            gui.DrawText("LOCKED", "Arial", 16, Color.White, new Rectangle(10, 10, 100, 20));
            gui.FillRectangle(Color.FromArgb(150, 0, 0, 0), new RectangleF(0, 0, 200, 100));
            Check(string.Join(",", hud.Order) == "rect,sprite,text,rect" && probe.Sprites.Count == 0,
                $"GUI images did not keep their draw order: {string.Join(",", hud.Order)} ({probe.Sprites.Count} drawn beneath).");
        });
    }

    /// <summary>A glTF with one node named Root, optionally a triangle mesh on it and a one-second clip moving it.</summary>
    private static string WriteGltf(string path, bool mesh, bool clip)
    {
        var bytes = new List<byte>();
        void Floats(params float[] values) { foreach (float value in values) bytes.AddRange(BitConverter.GetBytes(value)); }
        Floats(0, 0, 0, 1, 0, 0, 0, 1, 0);                // 0: positions (36 bytes)
        bytes.AddRange(BitConverter.GetBytes((ushort)0)); bytes.AddRange(BitConverter.GetBytes((ushort)1));
        bytes.AddRange(BitConverter.GetBytes((ushort)2)); bytes.AddRange(new byte[2]); // 36: indices (6 + 2 pad)
        Floats(0, 1);                                      // 44: key times (8 bytes)
        Floats(0, 0, 0, 0, 2, 0);                          // 52: translations (24 bytes)
        string nodes = mesh ? "[{\"name\":\"Root\",\"mesh\":0}]" : "[{\"name\":\"Root\"}]";
        string meshes = mesh ? ",\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]" : string.Empty;
        string animations = clip
            ? ",\"animations\":[{\"name\":\"Rise\",\"samplers\":[{\"input\":2,\"output\":3}],\"channels\":[{\"sampler\":0,\"target\":{\"node\":0,\"path\":\"translation\"}}]}]"
            : string.Empty;
        string json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":" + nodes + meshes + animations +
            ",\"buffers\":[{\"byteLength\":" + bytes.Count + ",\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes.ToArray()) + "\"}]," +
            "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":6}," +
            "{\"buffer\":0,\"byteOffset\":44,\"byteLength\":8},{\"buffer\":0,\"byteOffset\":52,\"byteLength\":24}]," +
            "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}," +
            "{\"bufferView\":1,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}," +
            "{\"bufferView\":2,\"componentType\":5126,\"count\":2,\"type\":\"SCALAR\",\"min\":[0],\"max\":[1]}," +
            "{\"bufferView\":3,\"componentType\":5126,\"count\":2,\"type\":\"VEC3\"}]}";
        File.WriteAllText(path, json);
        return path;
    }

    private static void RunAnimationLibraries(HeadlessContext ctx, string parent, ResourceService resources)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Models.ModelsShareClipsFromAnAnimationLibrary", () =>
        {
            // A file of clips with no meshes imports as a clips-only Model.
            string clipsSource = WriteGltf(Path.Combine(parent, "Moves.gltf"), mesh: false, clip: true);
            GModelAsset clipsOnly = ExternalModelImporter.Import(clipsSource, parent, Path.Combine(parent, "Moves.model.json"));
            Check(clipsOnly.Meshes.Count == 0 && clipsOnly.Animations.Count == 1 && clipsOnly.Metadata.ContainsKey("source.clipsOnly"),
                "A glTF of clips without meshes did not import as a clips-only Model.");

            // Same skeleton: the clip object itself is shared. Another rest pose: retargeted.
            GModelAsset same = new();
            same.Nodes.Add(new GModelNode { Name = "Root", LocalTransform = clipsOnly.Nodes[0].LocalTransform });
            Check(ModelAnimationLibraries.AddClips(same, clipsOnly) == 1
                && ReferenceEquals(same.Animations[0], clipsOnly.Animations[0]) && same.LibraryClipNames.Contains("Rise"),
                "A model on the same skeleton did not share the library's clip.");
            GModelAsset taller = new();
            taller.Nodes.Add(new GModelNode { Name = "Root", LocalTransform = Matrix4x4.CreateTranslation(0, 5, 0) });
            ModelAnimationLibraries.AddClips(taller, clipsOnly);
            GModelAnimationClip moved = taller.Animations.Single();
            float startY = moved.Frames[0].LocalBoneTransforms[0].Translation.Y;
            float endY = moved.Frames[^1].LocalBoneTransforms[0].Translation.Y;
            Check(!ReferenceEquals(moved, clipsOnly.Animations[0]) && Math.Abs(startY - 5) < 1e-4f && Math.Abs(endY - 7) < 1e-3f,
                $"A clip on a body with another rest pose did not keep that pose plus the movement: {startY} to {endY} (5 to 7).");

            // Wired through a Model's descriptor; borrowed clips are not saved into its file.
            string library = resources.ImportFiles(resources.AssetsRoot, [clipsSource]).Single();
            string figureSource = WriteGltf(Path.Combine(parent, "Figure.gltf"), mesh: true, clip: false);
            string figure = resources.ImportFiles(resources.AssetsRoot, [figureSource]).Single();
            var descriptor = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(figure))!.AsObject();
            descriptor["animationLibraries"] = new System.Text.Json.Nodes.JsonArray(Path.GetFileName(library).Split('.')[0]);
            descriptor["materialShaders"] = new System.Text.Json.Nodes.JsonObject { ["Material"] = "Glass Fresnel" };
            File.WriteAllText(figure, descriptor.ToJsonString());
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(Path.GetDirectoryName(resources.AssetsRoot)!);
            GModelAsset loaded = StudioModelResourceLoader.Load(figure);
            Check(loaded.Animations.Any(c => c.Name == "Rise") && loaded.LibraryClipNames.Contains("Rise"),
                "A Model did not play the clips of the library its descriptor names: " + string.Join(", ", loaded.Animations.Select(c => c.Name)));
            Check(loaded.Materials.Any(m => m.Shader == "Glass Fresnel"),
                "The descriptor's materialShaders did not reach the model's material: "
                + string.Join(", ", loaded.Materials.Select(m => $"{m.Name}={m.Shader}")));
            string saved = Path.Combine(parent, "Figure-saved.gmodel");
            RuntimeModelStore.Save(saved, loaded);
            Check(!File.ReadAllText(saved).Contains("\"Rise\"", StringComparison.Ordinal) && loaded.Animations.Any(c => c.Name == "Rise"),
                "A borrowed clip was written into the model's own file.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Assets.OneBadFileDoesNotStopAnImport", () =>
        {
            string empty = WriteGltf(Path.Combine(parent, "Nothing.gltf"), mesh: false, clip: false);
            string picture = Path.Combine(parent, "Fine.png");
            using (Bitmap bitmap = new(8, 8)) bitmap.Save(picture, ImageFormat.Png);
            try
            {
                resources.ImportFiles(resources.AssetsRoot, [empty, picture]);
                Check(false, "A file that cannot be imported was not reported.");
            }
            catch (ResourceImportException exception)
            {
                Check(exception.Imported.Count == 1 && exception.Failed.Count == 1
                    && exception.Failed[0].Source.EndsWith("Nothing.gltf", StringComparison.OrdinalIgnoreCase),
                    $"The import stopped or misreported: {exception.Message}");
            }
        });
    }

    private static float At(Matrix4x4 m, int i) => m[i / 4, i % 4];

    private sealed class OrderHud : Genesis.Runtime.Scripting.IHudCanvas
    {
        public List<string> Order { get; } = [];
        public int Width => 1280;
        public int Height => 720;
        public void Text(string text, float x, float y, float size, Vector4 color) => Order.Add("text");
        public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color) => Order.Add("text");
        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) => Order.Add("rect");
        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) => Order.Add("line");
        public bool SupportsSprites => true;
        public void Sprite(in SpriteDrawCall call) => Order.Add("sprite");
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
