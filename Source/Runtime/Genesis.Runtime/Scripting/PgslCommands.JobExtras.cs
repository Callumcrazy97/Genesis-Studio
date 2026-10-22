using System;
using System.Collections.Generic;
using System.Threading;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// More for worker jobs: building a mesh in a job and handing it over finished (JobScriptMesh), a job
// that waits for others and reads what they made (JobScriptAfter), caches a worker keeps from one job
// to the next (JobCache*), and ranges of lists and grids copied in one call (usable in jobs and on
// the game's thread alike).
public static partial class PgslCommands
{
    #region Job extras

    [PgslCommand("JobScriptMesh", "JobScriptMesh(job, mesh) -> bool",
        "Let a prepared job build a mesh (MeshCreate's handle): in the job the mesh starts empty and the MeshAdd commands build it in arrays of its own; JobTake hands the finished mesh over (no copy), and it is uploaded within the frame's mesh budget",
        "Native Jobs")]
    public static bool JobScriptMesh(double job, double mesh)
    {
        ScriptJobSetup setup = PreparedJob(job);
        if (setup == null) return Refuse("Not a prepared script job (meshes are given before JobScriptStart).");
        if (!double.IsFinite(mesh) || !ScriptMeshes.Exists((int)mesh)) return Refuse($"No mesh with handle {StringOf(mesh)} (MeshCreate makes one).");
        if (!setup.Meshes.Contains((int)mesh)) setup.Meshes.Add((int)mesh);
        SetJobError(string.Empty);
        return true;
    }

    [PgslCommand("JobScriptAfter", "JobScriptAfter(job, otherJob) -> bool",
        "Make a prepared job wait for another job of this Object: started, it reads \"waiting\" until the other has finished, then runs seeing what the other changed and marked to come back (grids, lists, maps under the same handles). A job waits for any number of others; if one fails, this one fails too",
        "Native Jobs")]
    public static bool JobScriptAfter(double job, double otherJob)
    {
        ScriptJobSetup setup = PreparedJob(job);
        if (setup == null) return Refuse("Not a prepared script job (what it waits for is given before JobScriptStart).");
        if (JobId(otherJob) == 0 || JobId(otherJob) == JobId(job) || JobSnapshot(otherJob).State == "invalid")
            return Refuse($"No other job with handle {StringOf(otherJob)} in this Object to wait for.");
        if (!setup.After.Contains(JobId(otherJob))) setup.After.Add(JobId(otherJob));
        SetJobError(string.Empty);
        return true;
    }

    // ── Worker caches ───────────────────────────────────────────────────────────
    // Each worker thread (and the game's thread) keeps a cache of its own from one job to the next: a
    // noise table, a palette, a lookup worked out once. A job never sees another worker's cache, so it
    // must give the same result whether the cache is filled or not (fill it when it is empty).
    // JobCacheReset empties every cache before the next job reads it (a new game, changed settings).

    private static int _jobCacheGeneration;
    [ThreadStatic] private static Dictionary<string, object> _jobCache;
    [ThreadStatic] private static int _jobCacheSeen;

    private static Dictionary<string, object> JobCache()
    {
        int generation = Volatile.Read(ref _jobCacheGeneration);
        if (_jobCache == null || _jobCacheSeen != generation)
        {
            _jobCache = new Dictionary<string, object>(StringComparer.Ordinal);
            _jobCacheSeen = generation;
        }
        return _jobCache;
    }

    [PgslCommand("JobCacheNumList", "JobCacheNumList(name, size) -> numList",
        "A number list this worker keeps from job to job under a name (made with size zeros the first time; check its contents to know whether it was filled): give the result to the NumList commands. Each worker has its own",
        "Native Jobs")]
    public static double JobCacheNumList(string name, double size)
    {
        if (string.IsNullOrEmpty(name) || Store is null) return 0;
        Dictionary<string, object> cache = JobCache();
        if (!cache.TryGetValue("n:" + name, out object kept) || kept is not PgslNumberList list)
        {
            int count = double.IsFinite(size) ? (int)Math.Clamp(size, 0, MaxCollectionElements) : 0;
            list = new PgslNumberList(count);
            list.Resize(count, MaxCollectionElements);
            cache["n:" + name] = list;
        }
        // Bound under a handle of this job (or Object): the same list every run on this worker.
        int handle = NextHandle("nlist");
        if (handle == 0) return 0;
        Bind("nlist", handle, list);
        return handle;
    }

    [PgslCommand("JobCacheGet", "JobCacheGet(name) -> number", "A number this worker keeps from job to job (0 when not set)", "Native Jobs")]
    public static double JobCacheGet(string name) =>
        !string.IsNullOrEmpty(name) && JobCache().TryGetValue("v:" + name, out object value) && value is double number ? number : 0;

    [PgslCommand("JobCacheSet", "JobCacheSet(name, value)", "Keep a number on this worker for later jobs", "Native Jobs")]
    public static void JobCacheSet(string name, double value)
    {
        if (!string.IsNullOrEmpty(name)) JobCache()["v:" + name] = value;
    }

    [PgslCommand("JobCacheReset", "JobCacheReset()", "Empty every worker's cache before its next job reads it (a new game, changed settings)", "Native Jobs")]
    public static void JobCacheReset() => Interlocked.Increment(ref _jobCacheGeneration);

    // ── Ranges ──────────────────────────────────────────────────────────────────

    [PgslCommand("DsListCopyRange", "DsListCopyRange(destination, destStart, source, sourceStart, count) -> copied",
        "Copy a range of one list into another at destStart (overwriting, and growing the destination when the range runs past its end); returns how many were copied", "Lists")]
    public static double DsListCopyRange(double destination, double destStart, double source, double sourceStart, double count)
    {
        List<object> from = ResolveRead<List<object>>("list", source);
        if (from is null || !NumRange(from.Count, sourceStart, count, out int s, out int n)) return 0;
        if (!double.IsFinite(destStart) || destStart < 0 || destStart + n > MaxCollectionElements) return 0;
        // Taken before writing: the destination may become a copy of its own when a job holds it.
        object[] range = System.Buffers.ArrayPool<object>.Shared.Rent(n);
        try
        {
            from.CopyTo(s, range, 0, n);
            List<object> to = Resolve<List<object>>("list", destination);
            if (to is null) return 0;
            int at = (int)destStart;
            while (to.Count < at) to.Add(0.0);
            for (int i = 0; i < n; i++)
            {
                if (at + i < to.Count) to[at + i] = range[i];
                else to.Add(range[i]);
            }
            return n;
        }
        finally
        {
            Array.Clear(range, 0, n);
            System.Buffers.ArrayPool<object>.Shared.Return(range);
        }
    }

    [PgslCommand("DsListAddRange", "DsListAddRange(destination, source, start, count) -> added", "Add a range of another list at the end of this one; returns how many were added", "Lists")]
    public static double DsListAddRange(double destination, double source, double start, double count)
    {
        List<object> to = ResolveRead<List<object>>("list", destination);
        return to is null ? 0 : DsListCopyRange(destination, to.Count, source, start, count);
    }

    [PgslCommand("DsGridToNumList", "DsGridToNumList(grid, x1, y1, x2, y2, numList) -> number",
        "Replace a number list's entries with a region's cells, row by row (cells outside the grid read 0); returns the entries written", "Grids")]
    public static double DsGridToNumList(double grid, double x1, double y1, double x2, double y2, double numList)
    {
        PgslGrid from = ResolveRead<PgslGrid>("grid", grid);
        PgslNumberList to = NumWrite(numList);
        if (from is null || to is null) return 0;
        (int left, int top, int right, int bottom) = NormaliseRegion(x1, y1, x2, y2);
        long count = ((long)right - left + 1) * ((long)bottom - top + 1);
        if (count <= 0 || count > MaxCollectionElements || !to.EnsureCapacity((int)count, MaxCollectionElements)) return 0;
        int at = 0;
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++) to.Items[at++] = from.Get(x, y);
        to.Count = at;
        return at;
    }

    [PgslCommand("DsGridFromNumList", "DsGridFromNumList(grid, x1, y1, x2, y2, numList) -> number",
        "Write a number list into a region, row by row, until the list runs out (cells outside the grid are skipped); returns the entries read", "Grids")]
    public static double DsGridFromNumList(double grid, double x1, double y1, double x2, double y2, double numList)
    {
        PgslGrid to = Resolve<PgslGrid>("grid", grid);
        PgslNumberList from = NumRead(numList);
        if (to is null || from is null) return 0;
        (int left, int top, int right, int bottom) = NormaliseRegion(x1, y1, x2, y2);
        int read = 0;
        for (long y = top; y <= bottom && read < from.Count; y++)
        {
            for (long x = left; x <= right && read < from.Count; x++)
            {
                double value = from.Items[read++];
                if (y >= 0 && y < to.Height && x >= 0 && x < to.Width) to.Put((int)((y * to.Width) + x), value);
            }
        }
        return read;
    }

    #endregion
}
