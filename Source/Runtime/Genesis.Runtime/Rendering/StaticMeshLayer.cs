using System;
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
    /// when the renderer changes, bounds when the mesh is rebuilt). Entries are kept in cells of the
    /// ground (<see cref="CellSize"/> across): a camera pass skips every cell outside its view, then each
    /// entry outside it, before looking its mesh up, so a hundred thousand placed meshes cost little more
    /// than those in view. The renderer batches and instances what it is given as usual.
    /// </summary>
    public static class StaticMeshLayer
    {
        /// <summary>The width and depth of a cell, in world units.</summary>
        public const float CellSize = 64f;

        private sealed class Entry
        {
            public int Id, Mesh;
            public Matrix4x4 World;
            public string Image = string.Empty, Shader = string.Empty;
            public Color Tint = Color.White;
            public float Alpha = 1;
            public ScriptMeshDrawOptions Options;
            public IRenderController Renderer;
            public MeshDrawCall Call;
            public int Uploads = -1;
            public bool BoundsKnown;
            public Vector3 Min, Max;
            public Cell Cell;
        }

        private sealed class Cell
        {
            public readonly List<Entry> Entries = new();
            public Vector3 Min, Max;
            /// <summary>An entry whose bounds are not known yet (not uploaded): the cell is always looked into.</summary>
            public bool HasUnknown;
            public bool Dirty = true;
            public long Key;
        }

        private static readonly Dictionary<int, Entry> Entries = new();
        private static readonly Dictionary<long, Cell> CellsByKey = new();
        private static readonly List<Cell> Cells = new();
        // The entries of each mesh, so a rebuilt mesh's entries forget their bounds.
        private static readonly Dictionary<int, List<Entry>> ByMesh = new();
        private static readonly List<int> RebuiltScratch = new();
        private static int _next;

        /// <summary>Entries drawn and skipped (outside the view) by the last camera pass, for checks and reports.</summary>
        public static int LastDrawn { get; private set; }
        public static int LastCulled { get; private set; }
        /// <summary>Cells the last camera pass skipped whole.</summary>
        public static int LastCellsCulled { get; private set; }
        public static int Count => Entries.Count;
        public static int CellCount => Cells.Count;

        public static int Add(int mesh, Matrix4x4 world, string image, string shader, Color tint, float alpha, ScriptMeshDrawOptions options)
        {
            int id = ++_next;
            var entry = new Entry { Id = id, Mesh = mesh, World = world, Image = image ?? string.Empty, Shader = shader ?? string.Empty, Tint = tint, Alpha = alpha, Options = options };
            Entries[id] = entry;
            Place(entry);
            if (!ByMesh.TryGetValue(mesh, out List<Entry> same)) ByMesh[mesh] = same = new List<Entry>(1);
            same.Add(entry);
            ScriptMeshes.TrackRebuilds = true;
            return id;
        }

        public static bool Exists(int id) => Entries.ContainsKey(id);

        public static bool Move(int id, Matrix4x4 world)
        {
            if (!Entries.TryGetValue(id, out Entry entry)) return false;
            entry.World = world;
            entry.Call.World = world;
            entry.Uploads = -1; // bounds worked out again
            entry.BoundsKnown = false;
            if (KeyOf(world) != entry.Cell.Key)
            {
                Unplace(entry);
                Place(entry);
            }
            else entry.Cell.Dirty = true;
            return true;
        }

        public static bool Remove(int id)
        {
            if (!Entries.Remove(id, out Entry entry)) return false;
            Unplace(entry);
            if (ByMesh.TryGetValue(entry.Mesh, out List<Entry> same) && same.Remove(entry) && same.Count == 0) ByMesh.Remove(entry.Mesh);
            if (Entries.Count == 0) ScriptMeshes.TrackRebuilds = false;
            return true;
        }

        /// <summary>Forgets every entry (a new game or room, or the script's MeshStaticClear).</summary>
        public static void Clear()
        {
            Entries.Clear();
            CellsByKey.Clear();
            Cells.Clear();
            ByMesh.Clear();
            ScriptMeshes.TrackRebuilds = false;
            LastDrawn = LastCulled = LastCellsCulled = 0;
        }

        private static long KeyOf(in Matrix4x4 world)
        {
            long cx = (long)MathF.Floor(world.M41 / CellSize), cz = (long)MathF.Floor(world.M43 / CellSize);
            return (cx << 32) ^ (cz & 0xFFFFFFFFL);
        }

        private static void Place(Entry entry)
        {
            long key = KeyOf(entry.World);
            if (!CellsByKey.TryGetValue(key, out Cell cell))
            {
                cell = new Cell { Key = key };
                CellsByKey[key] = cell;
                Cells.Add(cell);
            }
            cell.Entries.Add(entry);
            cell.Dirty = true;
            entry.Cell = cell;
        }

        private static void Unplace(Entry entry)
        {
            Cell cell = entry.Cell;
            entry.Cell = null;
            if (cell == null) return;
            cell.Entries.Remove(entry);
            cell.Dirty = true;
            if (cell.Entries.Count > 0) return;
            CellsByKey.Remove(cell.Key);
            Cells.Remove(cell);
        }

        private static void Measure(Cell cell)
        {
            cell.Dirty = false;
            cell.HasUnknown = false;
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            foreach (Entry entry in cell.Entries)
            {
                if (!entry.BoundsKnown) { cell.HasUnknown = true; continue; }
                lo = Vector3.Min(lo, entry.Min);
                hi = Vector3.Max(hi, entry.Max);
            }
            (cell.Min, cell.Max) = (lo, hi);
        }

        /// <summary>Draws every entry inside the camera's view; the host calls it once per camera pass.</summary>
        public static void Submit(IRenderController renderer, IRenderCommandSink sink, in Matrix4x4 viewProjection, string projectPath)
        {
            LastDrawn = 0; LastCulled = 0; LastCellsCulled = 0;
            if (Entries.Count == 0 || renderer == null) return;
            // Meshes rebuilt since: their entries' bounds are worked out again once uploaded.
            RebuiltScratch.Clear();
            ScriptMeshes.TakeRebuilt(RebuiltScratch);
            foreach (int rebuilt in RebuiltScratch)
                if (ByMesh.TryGetValue(rebuilt, out List<Entry> same))
                    foreach (Entry entry in same) { entry.BoundsKnown = false; entry.Cell.Dirty = true; }
            var frustum = new CameraFrustum(viewProjection);
            foreach (Cell cell in Cells)
            {
                if (cell.Dirty) Measure(cell);
                if (!cell.HasUnknown && !frustum.IntersectsAabb(cell.Min, cell.Max))
                {
                    LastCellsCulled++;
                    LastCulled += cell.Entries.Count;
                    continue;
                }
                // A cell wholly in view needs no test for each entry.
                bool inside = !cell.HasUnknown && frustum.ContainsAabbFully(cell.Min, cell.Max);
                foreach (Entry entry in cell.Entries)
                {
                    // Bounds already known are tested before the mesh is looked up (most of a big world is out of view).
                    if (entry.BoundsKnown && !inside && !frustum.IntersectsAabb(entry.Min, entry.Max)) { LastCulled++; continue; }
                    MeshHandle mesh = ScriptMeshes.Resolve(entry.Mesh, renderer, out int uploads);
                    if (!mesh.IsValid) continue;
                    if (!ReferenceEquals(entry.Renderer, renderer))
                    {
                        entry.Call = Genesis.Runtime.Scripting.PgslRenderDrawSurface.BuildScriptMeshCall(renderer, projectPath, mesh, entry.World,
                            entry.Image, entry.Shader, entry.Tint, entry.Alpha, entry.Options);
                        entry.Renderer = renderer;
                        entry.Uploads = -1;
                    }
                    if (uploads != entry.Uploads)
                    {
                        if (!ScriptMeshes.TryGetBounds(entry.Mesh, out Vector3 min, out Vector3 max)) min = max = Vector3.Zero;
                        (entry.Min, entry.Max) = Transform(min, max, entry.World);
                        entry.Uploads = uploads;
                        entry.BoundsKnown = true;
                        cell.Dirty = true;
                        if (!frustum.IntersectsAabb(entry.Min, entry.Max)) { LastCulled++; continue; }
                    }
                    entry.Call.Mesh = mesh;
                    if (sink != null) sink.DrawMesh(entry.Call);
                    else renderer.DrawMesh(entry.Call);
                    LastDrawn++;
                }
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
