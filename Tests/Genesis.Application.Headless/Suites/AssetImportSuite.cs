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
        });
    }

    private static float At(Matrix4x4 m, int i) => m[i / 4, i % 4];

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
