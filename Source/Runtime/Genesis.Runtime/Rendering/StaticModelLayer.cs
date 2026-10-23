using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Models placed once (PGSL ModelStaticAdd, ModelStaticAddFromNumList): props, rocks and trees by
    /// the thousand, with no Object or ECS entity each. Entries are kept in cells of the ground; each
    /// camera pass skips the cells out of view, then each entry out of view by its bounds, and draws
    /// the copies of one model in view together, one instanced draw per part of the model (as the
    /// terrain's scattered models are drawn), so a hundred thousand placed models cost about what is
    /// in view. They do not animate.
    /// </summary>
    public static class StaticModelLayer
    {
        public const float CellSize = 64f;

        private sealed class Group
        {
            public string Model;
            public bool Shadows;
            /// <summary>The copies in view this pass, by level of detail (0 full to 3 coarsest).</summary>
            public readonly List<MeshInstanceData>[] Visible = [new(), new(), new(), new()];
            public bool BoundsKnown;
            public Vector3 Min, Max;
        }

        private sealed class Entry
        {
            public int Id;
            public Group Group;
            public Matrix4x4 World;
            public RenderColor Tint = RenderColor.White;
            public bool BoundsKnown;
            public Vector3 Min, Max, Centre;
            public float Radius;
            public Cell Cell;
        }

        private sealed class Cell
        {
            public readonly List<Entry> Entries = new();
            public Vector3 Min, Max;
            public bool HasUnknown;
            public bool Dirty = true;
            public long Key;
        }

        private static readonly Dictionary<int, Entry> Entries = new();
        private static readonly Dictionary<(string, bool), Group> Groups = new();
        private static readonly List<Group> GroupList = new();
        private static readonly Dictionary<long, Cell> CellsByKey = new();
        private static readonly List<Cell> Cells = new();
        private static MeshDrawCall[] _templates = new MeshDrawCall[8];
        private static MeshInstanceData[] _combined = new MeshInstanceData[256];
        private static int _next;

        public static int Count => Entries.Count;
        /// <summary>Copies drawn and skipped by the last camera pass, and cells skipped whole.</summary>
        public static int LastDrawn { get; private set; }
        public static int LastCulled { get; private set; }
        public static int LastCellsCulled { get; private set; }

        /// <summary>Places a model; <paramref name="world"/> is its scale, turn and position (its pivot at the origin).</summary>
        public static int Add(string model, Matrix4x4 world, bool castShadows)
        {
            if (string.IsNullOrWhiteSpace(model)) return 0;
            var key = (model.Trim(), castShadows);
            if (!Groups.TryGetValue(key, out Group group))
            {
                group = new Group { Model = key.Item1, Shadows = castShadows };
                Groups[key] = group;
                GroupList.Add(group);
            }
            int id = ++_next;
            var entry = new Entry { Id = id, Group = group, World = world };
            Entries[id] = entry;
            Place(entry);
            return id;
        }

        public static bool Remove(int id)
        {
            if (!Entries.Remove(id, out Entry entry)) return false;
            Unplace(entry);
            return true;
        }

        public static void Clear()
        {
            Entries.Clear();
            Groups.Clear();
            GroupList.Clear();
            CellsByKey.Clear();
            Cells.Clear();
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

        private static void Measure(Cell cell, string projectPath)
        {
            cell.Dirty = false;
            cell.HasUnknown = false;
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            foreach (Entry entry in cell.Entries)
            {
                if (!entry.BoundsKnown && !MeasureEntry(entry, projectPath)) { cell.HasUnknown = true; continue; }
                lo = Vector3.Min(lo, entry.Min);
                hi = Vector3.Max(hi, entry.Max);
            }
            (cell.Min, cell.Max) = (lo, hi);
        }

        // An entry's box from its model's bounds (pivot at the origin, as the model is drawn).
        private static bool MeasureEntry(Entry entry, string projectPath)
        {
            Group group = entry.Group;
            if (!group.BoundsKnown)
            {
                GModelAsset asset = ObjectDrawPass.Models.LoadAsset(projectPath, group.Model);
                if (asset == null || !asset.HasRenderableMeshes || asset.ImportRequired) return false;
                Vector3 pivot = asset.Pivot?.Position ?? Vector3.Zero;
                group.Min = (asset.Bounds?.Min ?? new Vector3(-0.5f)) - pivot;
                group.Max = (asset.Bounds?.Max ?? new Vector3(0.5f)) - pivot;
                group.BoundsKnown = true;
            }
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 p = new((corner & 1) == 0 ? group.Min.X : group.Max.X, (corner & 2) == 0 ? group.Min.Y : group.Max.Y, (corner & 4) == 0 ? group.Min.Z : group.Max.Z);
                p = Vector3.Transform(p, entry.World);
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
            }
            (entry.Min, entry.Max) = (lo, hi);
            entry.Centre = (lo + hi) * 0.5f;
            entry.Radius = MathF.Max(0.01f, Vector3.Distance(lo, hi) * 0.5f);
            entry.BoundsKnown = true;
            return true;
        }

        /// <summary>
        /// Draws the placed models inside the camera's view; the host calls it once per camera pass.
        /// With <paramref name="projectionScale"/> (the projection's M22) each copy is drawn at the
        /// level of detail its size on screen calls for, as models are (ModelLodView), and one too
        /// small to see is not drawn.
        /// </summary>
        public static void Submit(IRenderController renderer, in Matrix4x4 viewProjection, string projectPath,
            Vector3 cameraPosition = default, float projectionScale = 0f)
        {
            LastDrawn = 0; LastCulled = 0; LastCellsCulled = 0;
            if (Entries.Count == 0 || renderer == null) return;
            var frustum = new CameraFrustum(viewProjection);
            bool detail = projectionScale > 1e-6f && ModelLodView.Enabled;
            foreach (Group group in GroupList) foreach (List<MeshInstanceData> level in group.Visible) level.Clear();
            foreach (Cell cell in Cells)
            {
                if (cell.Dirty || cell.HasUnknown) Measure(cell, projectPath);
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
                    if (!entry.BoundsKnown) continue; // its model is not loaded yet
                    if (!inside && !frustum.IntersectsAabb(entry.Min, entry.Max)) { LastCulled++; continue; }
                    int level = 0;
                    if (detail)
                    {
                        float distance = MathF.Max(Vector3.Distance(entry.Centre, cameraPosition), entry.Radius);
                        level = ModelLodView.LevelForSize(entry.Radius * projectionScale / distance);
                        if (level < 0) { LastCulled++; continue; } // a pixel or two: not worth its draw
                    }
                    entry.Group.Visible[level].Add(new MeshInstanceData(entry.World, entry.Tint));
                }
            }
            foreach (Group group in GroupList)
                for (int level = 0; level < group.Visible.Length; level++)
                    if (group.Visible[level].Count > 0) SubmitLevel(renderer, projectPath, group, level);
        }

        // One level's copies of a model: one instanced draw for each part of it at that level.
        private static void SubmitLevel(IRenderController renderer, string projectPath, Group group, int level)
        {
            {
                ModelRenderQueue queue = ModelRenderQueue.Rent();
                if (!ObjectDrawPass.Models.EnqueueAtLevel(queue, projectPath, group.Model, level, group.Shadows, renderer)) return;
                if (_templates.Length < queue.Count) _templates = new MeshDrawCall[queue.Count * 2];
                int templates = queue.CopyTo(_templates, 0);
                ReadOnlySpan<MeshInstanceData> copies = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(group.Visible[level]);
                for (int t = 0; t < templates; t++)
                {
                    MeshDrawCall template = _templates[t];
                    if (!group.Shadows) template.Flags |= MeshDrawFlags.NoShadow;
                    if (template.World.IsIdentity && template.Tint.R == 1f && template.Tint.G == 1f && template.Tint.B == 1f)
                    {
                        renderer.DrawMeshInstances(template, copies);
                        continue;
                    }
                    // A part with a transform or colour of its own: each copy's matrix and tint combined with it.
                    if (_combined.Length < copies.Length) _combined = new MeshInstanceData[Math.Max(copies.Length, _combined.Length * 2)];
                    RenderColor tint = template.Tint;
                    for (int i = 0; i < copies.Length; i++)
                    {
                        RenderColor tone = copies[i].Tint;
                        _combined[i] = new MeshInstanceData(template.World * copies[i].World,
                            new RenderColor(tint.R * tone.R, tint.G * tone.G, tint.B * tone.B, template.Alpha));
                    }
                    renderer.DrawMeshInstances(template, _combined.AsSpan(0, copies.Length));
                }
                LastDrawn += copies.Length;
            }
        }
    }
}
