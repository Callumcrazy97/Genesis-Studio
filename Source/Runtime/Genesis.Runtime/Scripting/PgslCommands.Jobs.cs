using System;
using System.Runtime.CompilerServices;
using System.Text;
using Genesis.Runtime.Project;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    private static readonly ConditionalWeakTable<PgslContext, NativeJobPool> JobPools = new();
    private static NativeJobPool Jobs => JobPools.GetValue(Ctx, _ => new());

    /// <summary>Cancel outstanding native work when its owning Object is destroyed.</summary>
    internal static void ReleaseJobs(PgslContext context)
    {
        if (context != null && JobPools.TryGetValue(context, out NativeJobPool pool)) pool.ReleaseAll();
    }

    private static int JobId(double id) => double.IsFinite(id) && id >= 1 && id <= int.MaxValue && Math.Truncate(id) == id ? (int)id : 0;
    private static NativeJobPool.Snapshot JobSnapshot(double id) => GetContext() != null && JobPools.TryGetValue(GetContext(), out NativeJobPool pool)
        ? pool.Read(JobId(id)) : new("invalid", "", null, "No job with this handle in the current Object.");
    private static void SetJobError(string error) { if (Store != null) Store["__job_error"] = error; }

    [PgslCommand("FileReadTextAsync", "FileReadTextAsync(path) -> job", "Queue a bounded UTF-8 read; poll JobStatus, read JobResultString, then JobRelease; 0 on enqueue failure", "Files")]
    public static double FileReadTextAsync(string path)
    {
        try
        {
            string resolved = ProjectTextFiles.ResolvePath(PersistenceProjectPath, path);
            int id = Jobs.Start("string", token => new("string", ProjectTextFiles.Read(resolved, token)));
            SetFileError(""); return id;
        }
        catch (Exception error) when (ProjectTextFiles.IsFileError(error)) { SetFileError(error.Message); return 0; }
    }

    [PgslCommand("FileWriteTextAsync", "FileWriteTextAsync(path, text) -> job", "Queue atomic UTF-8 replacement; poll JobStatus, read JobResultBool, then JobRelease; 0 on enqueue failure", "Files")]
    public static double FileWriteTextAsync(string path, string text)
    {
        try
        {
            string resolved = ProjectTextFiles.ResolvePath(PersistenceProjectPath, path);
            if (text == null || new UTF8Encoding(false, true).GetByteCount(text) > ProjectTextFiles.MaximumTextBytes)
                throw new ArgumentException("Text is missing or exceeds the 4 MiB limit.");
            int id = Jobs.Start("boolean", token => { ProjectTextFiles.Write(resolved, text, token); return new("boolean", true); });
            SetFileError(""); return id;
        }
        catch (Exception error) when (ProjectTextFiles.IsFileError(error)) { SetFileError(error.Message); return 0; }
    }

    [PgslCommand("JobStatus", "JobStatus(job) -> string", "queued, running, succeeded, failed, cancelled or invalid; never blocks the game loop", "Native Jobs")]
    public static string JobStatus(double id) => JobSnapshot(id).State;
    [PgslCommand("JobResultKind", "JobResultKind(job) -> string", "Declared result type; empty for an invalid handle", "Native Jobs")]
    public static string JobResultKind(double id) => JobSnapshot(id).Kind;
    [PgslCommand("JobError", "JobError(job) -> string", "Operation failure, or an invalid-handle error; empty while pending or after success", "Native Jobs")]
    public static string JobError(double id) => JobSnapshot(id).Error;
    [PgslCommand("JobLastError", "JobLastError() -> string", "Last job API misuse, such as reading a pending/wrong-type result", "Native Jobs")]
    public static string JobLastError() => Store?.TryGetValue("__job_error", out object error) == true ? error as string ?? "" : "No active Object context.";

    private static object JobResult(double id, string kind)
    {
        var snapshot = JobSnapshot(id);
        if (snapshot.State != "succeeded") { SetJobError("Job has no successful result: " + snapshot.State + ". " + snapshot.Error); return null; }
        if (snapshot.Kind != kind) { SetJobError("Job result is " + snapshot.Kind + ", not " + kind + "."); return null; }
        SetJobError(""); return snapshot.Value;
    }
    [PgslCommand("JobResultString", "JobResultString(job) -> string", "Read a successful string result; check JobLastError for pending/wrong-type reads", "Native Jobs")]
    public static string JobResultString(double id) => JobResult(id, "string") as string ?? "";
    [PgslCommand("JobResultBool", "JobResultBool(job) -> bool", "Read a successful boolean result; false on API failure", "Native Jobs")]
    public static bool JobResultBool(double id) => JobResult(id, "boolean") is true;
    [PgslCommand("JobResultNumber", "JobResultNumber(job) -> number", "Read a successful numeric native result; 0 on API failure", "Native Jobs")]
    public static double JobResultNumber(double id) => JobResult(id, "number") is double value ? value : 0;
    [PgslCommand("JobCancel", "JobCancel(job) -> bool", "Request cancellation of queued/running work; an already committed atomic write remains successful", "Native Jobs")]
    public static bool JobCancel(double id)
    {
        bool cancelled = GetContext() != null && JobPools.TryGetValue(GetContext(), out NativeJobPool pool) && pool.Cancel(JobId(id));
        SetJobError(cancelled ? "" : "Job is invalid or already finished."); return cancelled;
    }
    [PgslCommand("JobRelease", "JobRelease(job) -> bool", "Release the handle/result; cancel pending work; capacity is returned when running work stops", "Native Jobs")]
    public static bool JobRelease(double id)
    {
        bool released = GetContext() != null && JobPools.TryGetValue(GetContext(), out NativeJobPool pool) && pool.Release(JobId(id));
        SetJobError(released ? "" : "Unknown or released job handle."); return released;
    }
}
