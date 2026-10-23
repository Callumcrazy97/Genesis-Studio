using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Script meshes placed once and drawn every frame by the engine (PGSL MeshStaticAdd): a world's
    /// chunks no longer need a DrawMeshShader3D call each frame from a Draw event. Each entry keeps its
    /// world matrix, its bounds and its draw call worked out once (texture and shader looked up again only
    /// when the renderer changes, bounds when the mesh is rebuilt); every camera pass draws the entries
    /// inside its view, and the renderer batches and instances what it is given as usual.
    /// </summary>
    public static class StaticMeshLayer
    {
        private sealed class Entry
        {
            public int Mesh;
            public Matrix4x4 World;
            public string Image = string.Empty, Shader = string.Empty;
            public Color Tint = Color.White;
            public float Alpha = 1;
            public ScriptMeshDrawOptions Options;
            public IRenderController Renderer;
            public MeshDrawCall Call;
            public int Uploads = -1;
            public Vector3 Min, Max;
        }

        private static readonly Dictionary<int, Entry> Entries = new();
        private static int _next;

        /// <summary>Entries drawn and skipped (outside the view) by the last camera pass, for checks and reports.</summary>
        public static int LastDrawn { get; private set; }
        public static int LastCulled { get; private set; }
        public static int Count => Entries.Count;

        public static int Add(int mesh, Matrix4x4 world, string image, string shader, Color tint, float alpha, ScriptMeshDrawOptions options)
        {
            int id = ++_next;
            Entries[id] = new Entry { Mesh = mesh, World = world, Image = image ?? string.Empty, Shader = shader ?? string.Empty, Tint = tint, Alpha = alpha, Options = options };
            return id;
        }

        public static bool Exists(int id) => Entries.ContainsKey(id);

        public static bool Move(int id, Matrix4x4 world)
        {
            if (!Entries.TryGetValue(id, out Entry entry)) return false;
            entry.World = world;
            entry.Call.World = world;
            entry.Uploads = -1; // bounds worked out again
            return true;
        }

        public static bool Remove(int id) => Entries.Remove(id);

        /// <summary>Forgets every entry (a new game, or the script's MeshStaticClear).</summary>
        public static void Clear()
        {
            Entries.Clear();
            LastDrawn = LastCulled = 0;
        }

        /// <summary>Draws every entry inside the camera's view; the host calls it once per camera pass.</summary>
        public static void Submit(IRenderController renderer, IRenderCommandSink sink, in Matrix4x4 viewProjection, string projectPath)
        {
            LastDrawn = 0; LastCulled = 0;
            if (Entries.Count == 0 || renderer == null) return;
            var frustum = new CameraFrustum(viewProjection);
            foreach (Entry entry in Entries.Values)
            {
                MeshHandle mesh = ScriptMeshes.Resolve(entry.Mesh, renderer);
                if (!mesh.IsValid) continue;
                if (!ReferenceEquals(entry.Renderer, renderer))
                {
                    entry.Call = Genesis.Runtime.Scripting.PgslRenderDrawSurface.BuildScriptMeshCall(renderer, projectPath, mesh, entry.World,
                        entry.Image, entry.Shader, entry.Tint, entry.Alpha, entry.Options);
                    entry.Renderer = renderer;
                    entry.Uploads = -1;
                }
                int uploads = ScriptMeshes.UploadCount(entry.Mesh);
                if (uploads != entry.Uploads)
                {
                    if (!ScriptMeshes.TryGetBounds(entry.Mesh, out Vector3 min, out Vector3 max)) min = max = Vector3.Zero;
                    (entry.Min, entry.Max) = Transform(min, max, entry.World);
                    entry.Uploads = uploads;
                }
                if (!frustum.IntersectsAabb(entry.Min, entry.Max)) { LastCulled++; continue; }
                entry.Call.Mesh = mesh;
                if (sink != null) sink.DrawMesh(entry.Call);
                else renderer.DrawMesh(entry.Call);
                LastDrawn++;
            }
        }

        private static (Vector3, Vector3) Transform(Vector3 min, Vector3 max, Matrix4x4 world)
        {
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 p = new((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z);
                p = Vector3.Transform(p, world);
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
            }
            return (lo, hi);
        }
    }
}
