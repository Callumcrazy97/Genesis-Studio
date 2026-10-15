using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Runtime;
using Genesis.Runtime.Particles;
using Genesis.Rendering.Core;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static partial class ParticleWorkbenchSuite
{
    /// <summary>
    /// A Particle resource whose texture is an Image resource: drawn from the Image named (and from
    /// its id) on DX11 and Vulkan, still drawn after the Image is renamed (Rename rewrites the
    /// particle's reference), and shipped by Export Game, where the exported game finds it again.
    /// </summary>
    private static void RunImageResourceCases(HeadlessContext context, Action<string, Action> check)
    {
        check("ImageResource.DrawnFollowsRenameAndExports", () => WithEditor(original =>
        {
            ResourceService resources = ProjectAssetIndex.OpenResourceService(original.ProjectRoot);
            string imagePath = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Image), ResourceKind.Image, "Ember picture");
            string texturePath = Path.ChangeExtension(imagePath, ".png");
            using (Bitmap picture = new(8, 8))
            {
                using Graphics graphics = Graphics.FromImage(picture);
                graphics.Clear(Color.Red);
                picture.Save(texturePath);
            }
            ImageDocument imageDocument = ImageDocument.CreateDefault(8, 8);
            imageDocument.Frames.Add(new ImageFrame { Source = Path.GetFileName(texturePath) });
            File.WriteAllText(imagePath, ImageDocumentSerializer.Serialize(imageDocument));
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(original.ProjectRoot);

            // By name, by id, and a file path, all to the Image's frame.
            string byName = ParticleTextureResolver.ResolveFile(original.ProjectRoot, "Ember picture");
            Genesis.Shared.Assets.NamedResource entry = Genesis.Shared.Assets.ResourceCatalog.For(original.ProjectRoot).Find("Ember picture")
                ?? throw new InvalidOperationException("The Image is not in the catalog.");
            string id = entry.AssetId.ToString("N");
            string byId = ParticleTextureResolver.ResolveFile(original.ProjectRoot, id);
            string byFile = ParticleTextureResolver.ResolveFile(original.ProjectRoot,
                Path.GetRelativePath(original.ProjectRoot, texturePath).Replace('\\', '/'));
            Assert(entry.AssetId != Guid.Empty && PathsEqual(byName, texturePath) && PathsEqual(byId, texturePath) && PathsEqual(byFile, texturePath),
                $"The Image was not resolved to its frame by name ({byName}), id ({byId}) or file ({byFile}).");
            Assert(ParticleTextureResolver.ResolveFile(original.ProjectRoot, Guid.NewGuid().ToString("N")).Length == 0
                && ParticleTextureResolver.ResolveFile(original.ProjectRoot, "No such picture").Length == 0,
                "A missing Image resolved to a file.");

            ParticleConfig config = new()
            {
                Preview2D = true, Shape = ParticleEmitShape.Cone, SpreadDegrees = 0, Speed = 0,
                Gravity = 0, Drag = 0, Loop = false, BurstCount = 1, MaxParticles = 1,
                Lifetime = 4, LifetimeVariance = 0, StartSize = 4, EndSize = 4,
                RotationSpeed = 0, RotationVariance = 0, TexturePath = "Ember picture",
                StartColor = new ParticleColor(1, 1, 1, 1), EndColor = new ParticleColor(1, 1, 1, 1),
            };
            File.WriteAllText(original.ResourcePath, Text(config));
            JObject document;
            using (ParticleEditorControl authoring = new(original.ResourcePath, original.ProjectRoot))
                document = JObject.Parse(File.ReadAllText(authoring.CreateEffectObject("Ember effect")));

            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            Color Centre(RenderBackendOption backend, string label)
            {
                RenderBackendSelection.Configure(backend);
                using Form host = GateSuite.NewHost(800, 600);
                using ObjectCompositionPreviewControl preview = new(original.ProjectRoot, compact: true) { Playing = false };
                host.Controls.Add(preview); GateSuite.ShowHost(host); preview.Reload(document);
                preview.Viewport.Camera2DX = 0; preview.Viewport.Camera2DY = 0; preview.Viewport.Zoom2D = 1;
                using Bitmap frame = preview.CaptureFrame(3)!;
                Color centre = frame.GetPixel(frame.Width / 2, frame.Height / 2);
                string image = $"particle-image-{backend}-{label}.png";
                frame.Save(Path.Combine(context.Captures, image));
                context.Report.Images.Add(ImageResult.From(backend + " particle image " + label, image, VisualCapture.Measure(frame)));
                Assert(preview.Viewport.Host.RenderFaultCount == 0, backend + " faulted drawing the particle.");
                host.Controls.Remove(preview);
                return centre;
            }
            static bool Red(Color c) => c.R >= 200 && c.G <= 40 && c.B <= 40;
            try
            {
                foreach (RenderBackendOption backend in new[] { RenderBackendOption.SilkNetDx11, RenderBackendOption.Vulkan })
                {
                    Color named = Centre(backend, "name");
                    Assert(Red(named), $"{backend} did not draw the particle with its Image (by name): {named}.");
                }

                config.TexturePath = id;
                File.WriteAllText(original.ResourcePath, Text(config));
                Color byIdColour = Centre(RenderBackendOption.SilkNetDx11, "id");
                Assert(Red(byIdColour), "DX11 did not draw the particle with its Image given by id: " + byIdColour);

                // Rename: the reference in the Particle resource follows, and the effect still finds it.
                config.TexturePath = "Ember picture";
                File.WriteAllText(original.ResourcePath, Text(config));
                Genesis.Shared.Assets.ResourceCatalog.Invalidate(original.ProjectRoot);
                resources.Rename(imagePath, "Ember picture renamed");
                Genesis.Shared.Assets.ResourceCatalog.Invalidate(original.ProjectRoot);
                string saved = (string?)JObject.Parse(File.ReadAllText(original.ResourcePath))["texturePath"] ?? string.Empty;
                Assert(saved == "Ember picture renamed", "Rename did not rewrite the particle's Image reference: " + saved);
                Assert(PathsEqual(ParticleTextureResolver.ResolveFile(original.ProjectRoot, saved), texturePath)
                    && ParticleTextureResolver.ResolveFile(original.ProjectRoot, "Ember picture").Length == 0
                    && PathsEqual(ParticleTextureResolver.ResolveFile(original.ProjectRoot, id), texturePath),
                    "After the rename the Image is not found by its new name or its id, or still by the old one.");
                Color renamed = Centre(RenderBackendOption.SilkNetDx11, "renamed");
                Assert(Red(renamed), "DX11 did not draw the particle after its Image was renamed: " + renamed);
            }
            finally { RenderBackendSelection.Configure(previous); }

            // Export: the Image ships, and the exported game finds it by the particle's reference.
            string output = Path.Combine(context.Workspace, "ParticleImageExport");
            GameExportResult package = GameExportService.Export(new(resources.Project, output, GameExportFormat.Folder));
            Assert(package.Success, "Export failed: " + package.ErrorMessage);
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(package.OutputPath);
            string exportedParticle = Path.Combine(package.OutputPath, Path.GetRelativePath(original.ProjectRoot, original.ResourcePath));
            string exportedReference = (string?)JObject.Parse(File.ReadAllText(exportedParticle))["texturePath"] ?? string.Empty;
            string exported = ParticleTextureResolver.ResolveFile(package.OutputPath, exportedReference);
            Assert(exported.Length > 0 && exported.StartsWith(Path.GetFullPath(package.OutputPath), StringComparison.OrdinalIgnoreCase)
                && File.Exists(exported) && File.ReadAllBytes(exported).AsSpan().SequenceEqual(File.ReadAllBytes(texturePath)),
                $"The exported game does not have the particle's Image '{exportedReference}' ({exported}).");
            try { Directory.Delete(package.OutputPath, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }));
    }

    private static bool PathsEqual(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
