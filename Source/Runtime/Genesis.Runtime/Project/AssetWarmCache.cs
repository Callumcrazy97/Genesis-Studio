using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Genesis.Shared.Interfaces;
using Genesis.Streaming.Packs;

namespace Genesis.Runtime.Project
{
    /// <summary>Recursive runtime preparation. A job advances only after successful work.</summary>
    public sealed partial class AssetWarmCache
    {
        private readonly List<string> _criticalPaths = new();
        private long _bytesTotal, _bytesDone;
        private int _jobsDone;
        public int JobsDone => _jobsDone;
        public int JobsTotal => _criticalPaths.Count;
        public long BytesDone => _bytesDone;
        public long BytesTotal => _bytesTotal;
        public IReadOnlyList<string> CriticalPaths => _criticalPaths;
        public float Progress => JobsTotal == 0 ? 1f : _jobsDone / (float)JobsTotal;
        public string CurrentPath { get; private set; } = "";

        public void BuildCriticalList(string projectPath, string startRoomName)
        {
            _criticalPaths.Clear(); _bytesTotal = 0; _bytesDone = 0; _jobsDone = 0;
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
                throw new DirectoryNotFoundException("The game's content directory does not exist: " + projectPath);
            // Only what the first room uses is prepared before it starts; see AssetWarmCache.References.
            if (TryCollectFirstRoom(projectPath, startRoomName)) return;
            Scope = "every game asset";
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".git", ".vs", "bin", "obj", "Saves", "SaveGames", "Logs", "Cache", "Debug", ".genesis", "Build", "Packages", "ProjectSettings", "TestResults" };
            // Warm the game's content, not the whole project folder. A project also holds tools,
            // documents, source art and archives; walking those put reference photos on the GPU and
            // read gigabytes the game never uses, at four files a frame, before the first room ran.
            // A content directory with no Assets folder (an exported layout) is walked whole.
            string assets = Path.Combine(projectPath, "Assets");
            var pending = new Stack<string>(); pending.Push(Directory.Exists(assets) ? assets : projectPath);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked game content is not supported: " + dir);
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Linked game asset is not supported: " + file);
                    if (!IsWarmCandidate(file)) continue;
                    _criticalPaths.Add(file); _bytesTotal = checked(_bytesTotal + new FileInfo(file).Length);
                }
                foreach (string child in Directory.EnumerateDirectories(dir))
                    if (!excluded.Contains(Path.GetFileName(child))) pending.Push(child);
            }
            _criticalPaths.Sort(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>GPU calls stay on the render thread. No fixed limit on how many files are discovered.</summary>
        public void WarmStep(IRenderController renderer, int maxItems = 256)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            var budget = Stopwatch.StartNew();
            int end = Math.Min(JobsTotal, _jobsDone + Math.Max(1, maxItems));
            while (_jobsDone < end)
            {
                string path = _criticalPaths[_jobsDone]; CurrentPath = path;
                long bytes = new FileInfo(path).Length;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga")
                {
                    TextureHandle texture = renderer.LoadTexture(path);
                    if (!texture.IsValid) throw new InvalidDataException("The renderer could not prepare texture: " + path);
                }
                else
                {
                    // Non-texture parsing remains the scene/game loader's responsibility. Do not
                    // claim that reading model/audio/shader bytes creates a GPU or audio resource.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.SequentialScan);
                    stream.CopyTo(Stream.Null, 64 * 1024);
                }
                _bytesDone = checked(_bytesDone + bytes);
                _jobsDone++;
                if (budget.ElapsedMilliseconds >= 6) break;
            }
        }

        [Obsolete("Streaming readiness must be acknowledged by the actual world provider; it is not an asset job.")]
        public void MarkStreamingRingWarm(System.Numerics.Vector3 cameraPos, float ringRadius)
        {
            // Compatibility no-op: the old method falsely incremented the completed asset count.
        }
        public static bool VerifyPackRoundTrip(byte[] sample) => ZstdChunkPack.IdentityRoundTrip(sample ?? Array.Empty<byte>());
        private static bool IsWarmCandidate(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            // Model source files (.glb, .gltf, .obj, .fbx) are not listed: the game reads the
            // imported .gmodel, and a source is opened only by the importer when a reimport is due.
            return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".wav" or ".ogg" or ".mp3"
                or ".flac" or ".gmodel" or ".json" or ".hlsl" or ".glsl"
                or ".spv" or ".dds" or ".ktx" or ".ktx2" or ".bin" or ".pak" or ".pack";
        }
    }
}
