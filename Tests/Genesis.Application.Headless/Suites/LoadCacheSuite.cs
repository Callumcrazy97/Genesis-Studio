using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using Genesis.Physics;
using Genesis.Rendering.Textures;
using Genesis.Shared.Diagnostics;
using StbImageSharp;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The caches that keep a game's second start quick: decoded pictures and prepared terrain
/// collision kept in a project's <c>.genesis/Cache</c> folder, and the load profile that found
/// them. A cached load must give exactly what a fresh one gives, and a changed or damaged source
/// must never be answered from an old entry.
/// </summary>
internal static class LoadCacheSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.LoadCache.Textures.CachedDecodeMatchesFreshDecode", () => CachedDecodeMatches(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.LoadCache.Textures.ChangedPictureIsDecodedAgain", () => ChangedPicture(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.LoadCache.Textures.DamagedOrForeignEntriesAreIgnored", () => DamagedEntry(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.LoadCache.Colliders.CachedTreeMatchesBuiltTree", () => CachedCollider(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.LoadCache.Colliders.ChangedOrDamagedTrianglesBuildAgain", () => ChangedCollider(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.LoadProfile.SpansNestAndRecordNothingWhenOff", LoadProfileTree);
    }

    /// <summary>A folder laid out as a project: Assets beside the project's .genesis folder.</summary>
    private static string NewProject(HeadlessContext ctx, string name)
    {
        string root = Path.Combine(ctx.Workspace, name);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(Path.Combine(root, ".genesis"));
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Textures"));
        return root;
    }

    /// <summary>A noisy picture, so it is large enough on disk to be worth an entry.</summary>
    private static string WritePicture(string path, int seed, int size = 160)
    {
        var random = new Random(seed);
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256), random.Next(256)));
        bitmap.Save(path, ImageFormat.Png);
        HeadlessHarness.Assert(new FileInfo(path).Length >= DecodedTextureCache.MinimumSourceBytes,
            "The test picture is too small to be cached.");
        return path;
    }

    private static (int Width, int Height, byte[] Pixels) Decode(string path)
    {
        using FileStream stream = File.OpenRead(path);
        StbImageSharp.ImageResult image = StbImageSharp.ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return (image.Width, image.Height, image.Data);
    }

    private static void CachedDecodeMatches(HeadlessContext ctx)
    {
        string root = NewProject(ctx, "LoadCacheTextures");
        string picture = WritePicture(Path.Combine(root, "Assets", "Textures", "Noise.png"), 7);
        (int width, int height, byte[] pixels) = Decode(picture);
        byte[] chain = TextureMipBuilder.BuildChain(pixels, width, height, srgb: true, out int levels);

        HeadlessHarness.Assert(!DecodedTextureCache.TryLoad(picture, DecodedTextureKind.ColourMips, out _, out _, out _, out _),
            "A picture that was never decoded had an entry.");
        HeadlessHarness.Assert(DecodedTextureCache.Save(picture, DecodedTextureKind.ColourMips, width, height, levels, chain)
            && DecodedTextureCache.Save(picture, DecodedTextureKind.Pixels, width, height, 1, pixels),
            "The entries could not be written.");
        string entry = DecodedTextureCache.PathFor(picture, DecodedTextureKind.ColourMips);
        HeadlessHarness.Assert(entry != null && entry.StartsWith(Path.Combine(root, ".genesis", "Cache", "Textures"), StringComparison.OrdinalIgnoreCase)
            && File.Exists(entry), $"The entry is not in the project's cache folder: {entry}");

        HeadlessHarness.Assert(DecodedTextureCache.TryLoad(picture, DecodedTextureKind.ColourMips, out int w, out int h, out int l, out byte[] cached),
            "The entry just written was not read back.");
        HeadlessHarness.Assert(w == width && h == height && l == levels && cached.AsSpan().SequenceEqual(chain),
            "The cached mip chain differs from a fresh decode.");
        HeadlessHarness.Assert(DecodedTextureCache.TryLoad(picture, DecodedTextureKind.Pixels, out _, out _, out int one, out byte[] cachedPixels)
            && one == 1 && cachedPixels.AsSpan().SequenceEqual(pixels), "The cached pixels differ from a fresh decode.");
        HeadlessHarness.Assert(!DecodedTextureCache.TryLoad(picture, DecodedTextureKind.DataMips, out _, out _, out _, out _),
            "A colour entry answered for a data picture: their mips are averaged differently.");

        // A picture outside any project (the engine's own) has nowhere to keep an entry.
        string loose = Path.Combine(ctx.Workspace, "LooseAssets", "Assets");
        Directory.CreateDirectory(loose);
        string enginePicture = WritePicture(Path.Combine(loose, "Engine.png"), 3);
        HeadlessHarness.Assert(DecodedTextureCache.PathFor(enginePicture, DecodedTextureKind.Pixels) == null
            && !DecodedTextureCache.Save(enginePicture, DecodedTextureKind.Pixels, 1, 1, 1, new byte[4]),
            "A picture outside a project was given an entry beside it.");
    }

    private static void ChangedPicture(HeadlessContext ctx)
    {
        string root = NewProject(ctx, "LoadCacheChanged");
        string picture = WritePicture(Path.Combine(root, "Assets", "Textures", "Changing.png"), 11);
        (int width, int height, byte[] before) = Decode(picture);
        HeadlessHarness.Assert(DecodedTextureCache.Save(picture, DecodedTextureKind.Pixels, width, height, 1, before),
            "The entry could not be written.");

        // Saved again with other pixels: the entry for the old ones must not answer.
        WritePicture(picture, 12);
        File.SetLastWriteTimeUtc(picture, File.GetLastWriteTimeUtc(picture).AddSeconds(5));
        HeadlessHarness.Assert(!DecodedTextureCache.TryLoad(picture, DecodedTextureKind.Pixels, out _, out _, out _, out _),
            "A changed picture was answered from the entry made before it changed.");
        (int w2, int h2, byte[] after) = Decode(picture);
        HeadlessHarness.Assert(!after.AsSpan().SequenceEqual(before), "The test picture did not change.");
        HeadlessHarness.Assert(DecodedTextureCache.Save(picture, DecodedTextureKind.Pixels, w2, h2, 1, after)
            && DecodedTextureCache.TryLoad(picture, DecodedTextureKind.Pixels, out _, out _, out _, out byte[] rebuilt)
            && rebuilt.AsSpan().SequenceEqual(after), "The rebuilt entry does not hold the new pixels.");

        // The stamp is taken before decoding, so a picture saved during the decode is not stamped
        // with a time its pixels did not come from.
        (long Length, long Ticks)? stamp = DecodedTextureCache.StampForSaving(picture);
        HeadlessHarness.Assert(stamp is { } taken && taken.Length == new FileInfo(picture).Length,
            "No stamp was taken for a picture inside a project.");
    }

    private static void DamagedEntry(HeadlessContext ctx)
    {
        string root = NewProject(ctx, "LoadCacheDamaged");
        string picture = WritePicture(Path.Combine(root, "Assets", "Textures", "Damaged.png"), 21);
        (int width, int height, byte[] pixels) = Decode(picture);
        HeadlessHarness.Assert(DecodedTextureCache.Save(picture, DecodedTextureKind.Pixels, width, height, 1, pixels),
            "The entry could not be written.");
        string entry = DecodedTextureCache.PathFor(picture, DecodedTextureKind.Pixels)!;
        byte[] bytes = File.ReadAllBytes(entry);
        for (int i = bytes.Length / 2; i < Math.Min(bytes.Length, bytes.Length / 2 + 64); i++) bytes[i] ^= 0x5A;
        File.WriteAllBytes(entry, bytes);
        HeadlessHarness.Assert(!DecodedTextureCache.TryLoad(picture, DecodedTextureKind.Pixels, out _, out _, out _, out _),
            "A damaged entry was read as the picture.");
        File.WriteAllBytes(entry, bytes.AsSpan(0, 20).ToArray());
        HeadlessHarness.Assert(!DecodedTextureCache.TryLoad(picture, DecodedTextureKind.Pixels, out _, out _, out _, out _),
            "A truncated entry was read as the picture.");

        bool enabled = DecodedTextureCache.Enabled;
        try
        {
            DecodedTextureCache.Enabled = false;
            HeadlessHarness.Assert(DecodedTextureCache.StampForSaving(picture) == null
                && !DecodedTextureCache.TryLoad(picture, DecodedTextureKind.Pixels, out _, out _, out _, out _),
                "GENESIS_TEXTURE_CACHE=0 still used the cache.");
        }
        finally { DecodedTextureCache.Enabled = enabled; }
    }

    /// <summary>A rolling height field large enough for a real search tree.</summary>
    private static (Vector3[] Vertices, int[] Indices) Terrain(int cells, float bump)
    {
        int side = cells + 1;
        var vertices = new Vector3[side * side];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
            vertices[z * side + x] = new Vector3(x, MathF.Sin(x * 0.3f) * 2f + MathF.Cos(z * 0.2f) * 1.5f, z);
        vertices[side * (side / 2) + side / 2].Y += bump;
        var indices = new List<int>(cells * cells * 6);
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            int a = z * side + x, b = a + 1, c = a + side, d = c + 1;
            indices.AddRange([a, c, b, b, c, d]);
        }
        return (vertices, indices.ToArray());
    }

    private static bool WaitFor(Func<bool> condition, int milliseconds = 10_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > milliseconds) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    private static void CachedCollider(HeadlessContext ctx)
    {
        string cache = Path.Combine(NewProject(ctx, "LoadCacheColliders"), ".genesis", "Cache", "Colliders");
        (Vector3[] vertices, int[] indices) = Terrain(96, 0f);
        Vector3 scale = new(1.5f, 1f, 1.5f);

        using PhysicsWorld.PreparedStaticMesh plain = PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, scale);
        int writes = PhysicsWorld.PreparedMeshCacheWrites, hits = PhysicsWorld.PreparedMeshCacheHits;
        using PhysicsWorld.PreparedStaticMesh built = PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, scale, cache);
        HeadlessHarness.Assert(PhysicsWorld.PreparedMeshCacheHits == hits, "A mesh never prepared before was read from the cache.");
        HeadlessHarness.Assert(WaitFor(() => PhysicsWorld.PreparedMeshCacheWrites > writes && Directory.GetFiles(cache, "*.gcm").Length == 1),
            "The prepared mesh was not written to the project's cache folder.");

        using PhysicsWorld.PreparedStaticMesh read = PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, scale, cache);
        HeadlessHarness.Assert(PhysicsWorld.PreparedMeshCacheHits == hits + 1, "The same triangles were prepared again instead of read back.");
        HeadlessHarness.Assert(read.TriangleCount == built.TriangleCount && read.TriangleCount == indices.Length / 3,
            "The cached mesh has a different number of triangles.");
        byte[] expected = plain.ToBytes();
        HeadlessHarness.Assert(built.ToBytes().AsSpan().SequenceEqual(expected) && read.ToBytes().AsSpan().SequenceEqual(expected),
            "The cached mesh (triangles, scale and tree) differs from one built afresh.");

        // A list that is not an array is prepared the ordinary way and nothing is written for it.
        int before = PhysicsWorld.PreparedMeshCacheWrites;
        using PhysicsWorld.PreparedStaticMesh listed = PhysicsWorld.PrepareStaticTriangleMesh(
            new List<Vector3>(vertices), new List<int>(indices), scale, cache);
        HeadlessHarness.Assert(listed.ToBytes().AsSpan().SequenceEqual(expected) && PhysicsWorld.PreparedMeshCacheWrites == before,
            "A mesh given as lists was cached or built differently.");
    }

    private static void ChangedCollider(HeadlessContext ctx)
    {
        string cache = Path.Combine(NewProject(ctx, "LoadCacheCollidersChanged"), ".genesis", "Cache", "Colliders");
        (Vector3[] vertices, int[] indices) = Terrain(64, 0f);
        Vector3 scale = Vector3.One;
        int writes = PhysicsWorld.PreparedMeshCacheWrites;
        using (PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, scale, cache)) { }
        HeadlessHarness.Assert(WaitFor(() => PhysicsWorld.PreparedMeshCacheWrites > writes), "The first mesh was not cached.");

        // One raised vertex: a different terrain, so a different entry and a new tree.
        (Vector3[] raised, _) = Terrain(64, 3f);
        int hits = PhysicsWorld.PreparedMeshCacheHits;
        using PhysicsWorld.PreparedStaticMesh changed = PhysicsWorld.PrepareStaticTriangleMesh(raised, indices, scale, cache);
        HeadlessHarness.Assert(PhysicsWorld.PreparedMeshCacheHits == hits, "A changed terrain was answered from the old terrain's tree.");
        using (PhysicsWorld.PreparedStaticMesh fresh = PhysicsWorld.PrepareStaticTriangleMesh(raised, indices, scale))
            HeadlessHarness.Assert(changed.ToBytes().AsSpan().SequenceEqual(fresh.ToBytes()), "The changed terrain's mesh is not the one built afresh.");
        HeadlessHarness.Assert(WaitFor(() => Directory.GetFiles(cache, "*.gcm").Length == 2), "The changed terrain was not cached beside the first.");

        // A different scale is a different mesh too.
        hits = PhysicsWorld.PreparedMeshCacheHits;
        using (PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, new Vector3(2f, 1f, 2f), cache)) { }
        HeadlessHarness.Assert(PhysicsWorld.PreparedMeshCacheHits == hits, "A mesh at another scale was answered from the cache.");

        // A damaged entry fails its hash and the mesh is built again, never handed to the engine.
        foreach (string entry in Directory.GetFiles(cache, "*.gcm"))
        {
            byte[] bytes = File.ReadAllBytes(entry);
            bytes[^10] ^= 0xFF;
            File.WriteAllBytes(entry, bytes);
        }
        hits = PhysicsWorld.PreparedMeshCacheHits;
        using PhysicsWorld.PreparedStaticMesh again = PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, scale, cache);
        using PhysicsWorld.PreparedStaticMesh reference = PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, scale);
        HeadlessHarness.Assert(PhysicsWorld.PreparedMeshCacheHits == hits && again.ToBytes().AsSpan().SequenceEqual(reference.ToBytes()),
            "A damaged entry was read instead of the mesh being built again.");
    }

    private static void LoadProfileTree()
    {
        bool enabled = LoadProfile.Enabled;
        try
        {
            LoadProfile.Enabled = false;
            using (LoadProfile.Begin("never recorded")) { }
            LoadProfile.Add("never recorded either", 5);
            HeadlessHarness.Assert(LoadProfile.TakeReport("off").Count == 0, "The load profile recorded while it was off.");

            LoadProfile.Enabled = true;
            LoadProfile.Reset();
            LoadProfile.UseCurrentThreadAsGameThread();
            using (LoadProfile.Begin("outer"))
            {
                using (LoadProfile.Begin("inner")) Thread.Sleep(15);
                using (LoadProfile.Begin("inner")) Thread.Sleep(5);
                LoadProfile.Add("added", 3);
            }
            Task.Run(() => { using (LoadProfile.Begin("on a worker")) Thread.Sleep(5); }).Wait();
            LoadProfile.Mark("done");
            List<string> lines = LoadProfile.TakeReport("test");
            string text = string.Join("\n", lines);
            int outer = lines.FindIndex(line => line.TrimStart().StartsWith("outer ", StringComparison.Ordinal));
            int inner = lines.FindIndex(line => line.TrimStart().StartsWith("inner ", StringComparison.Ordinal));
            HeadlessHarness.Assert(outer >= 0 && inner > outer
                && lines[inner].Length - lines[inner].TrimStart().Length > lines[outer].Length - lines[outer].TrimStart().Length,
                "A span opened inside another was not listed beneath it:\n" + text);
            HeadlessHarness.Assert(lines[inner].Contains("(2 times", StringComparison.Ordinal), "Repeated spans were not counted together:\n" + text);
            HeadlessHarness.Assert(text.Contains("worker threads", StringComparison.Ordinal) && text.Contains("on a worker", StringComparison.Ordinal),
                "A worker thread's span was not listed under the workers:\n" + text);
            HeadlessHarness.Assert(text.Contains("done ", StringComparison.Ordinal), "The moment noted was not reported:\n" + text);
            HeadlessHarness.Assert(LoadProfile.TakeReport("again").All(line => !line.Contains("outer", StringComparison.Ordinal)),
                "A report did not start a new tree.");
        }
        finally
        {
            LoadProfile.Reset();
            LoadProfile.Enabled = enabled;
        }
    }
}
