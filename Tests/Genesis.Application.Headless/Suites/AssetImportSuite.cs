using System.Drawing;
using System.Drawing.Imaging;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Shared.Audio;

namespace Genesis.Application.Headless.Suites;

/// <summary>What a game's own pictures and sounds become when they are brought into a project.</summary>
internal static class AssetImportSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "AssetImport");
        string parent = Path.Combine(ctx.Workspace, "AssetImport");
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "Import", "Blank");
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
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
