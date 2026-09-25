using System.Diagnostics;
using System.Runtime.InteropServices;
using DevProfiler.Models;
using System.IO;

namespace DevProfiler.Services;

public sealed class ProcessTreeService
{
    private sealed record RawProcess(int Pid, int ParentPid, string Name, uint Threads);

    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> _cpuPrevious = [];
    private readonly Dictionary<int, (ulong Read, ulong Write, DateTimeOffset At)> _ioPrevious = [];

    public void Reset()
    {
        _cpuPrevious.Clear();
        _ioPrevious.Clear();
    }

    public IReadOnlyList<ProcessSnapshot> CaptureTree(
        int rootProcessId,
        IReadOnlyCollection<int>? ownedProcessIds = null)
    {
        var all = EnumerateProcesses();
        var selected = FindDescendants(rootProcessId, all).ToDictionary(x => x.Pid);
        if (ownedProcessIds is not null)
        {
            var byPid = all.ToDictionary(x => x.Pid);
            foreach (int processId in ownedProcessIds)
            {
                if (byPid.TryGetValue(processId, out RawProcess? process))
                    selected[processId] = process;
            }
        }

        var result = new List<ProcessSnapshot>();
        foreach (RawProcess raw in selected.Values)
        {
            try
            {
                using Process process = Process.GetProcessById(raw.Pid);
                process.Refresh();

                string path = string.Empty;
                try { path = process.MainModule?.FileName ?? string.Empty; } catch { }

                double cpu = CalculateCpu(process);
                (double readRate, double writeRate, double readTotal, double writeTotal) = CalculateIo(process);

                result.Add(new ProcessSnapshot
                {
                    ProcessId = raw.Pid,
                    ParentProcessId = raw.ParentPid,
                    Name = process.ProcessName,
                    Path = path,
                    CpuPercent = cpu,
                    PrivateMemoryMb = process.PrivateMemorySize64 / 1024d / 1024d,
                    WorkingSetMb = process.WorkingSet64 / 1024d / 1024d,
                    Threads = process.Threads.Count,
                    Handles = SafeGetHandleCount(process),
                    ReadMb = readTotal,
                    WriteMb = writeTotal,
                    ReadMbPerSecond = readRate,
                    WriteMbPerSecond = writeRate,
                    HasExited = process.HasExited
                });
            }
            catch
            {
                result.Add(new ProcessSnapshot
                {
                    ProcessId = raw.Pid,
                    ParentProcessId = raw.ParentPid,
                    Name = raw.Name,
                    Threads = (int)raw.Threads,
                    HasExited = true
                });
            }
        }

        var active = result.Where(x => !x.HasExited).Select(x => x.ProcessId).ToHashSet();
        foreach (int stale in _cpuPrevious.Keys.Where(x => !active.Contains(x)).ToArray())
            _cpuPrevious.Remove(stale);
        foreach (int stale in _ioPrevious.Keys.Where(x => !active.Contains(x)).ToArray())
            _ioPrevious.Remove(stale);

        return result;
    }

    public (double ReadMbPerSecond, double WriteMbPerSecond) GetLatestIoRates(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        // Rates are already calculated internally; derive from total deltas is intentionally omitted from rows.
        // This method remains as a stable extension point for a future per-process I/O-rate column.
        return (0, 0);
    }

    private double CalculateCpu(Process process)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan current = process.TotalProcessorTime;
        double value = 0;

        if (_cpuPrevious.TryGetValue(process.Id, out var previous))
        {
            double wallMs = (now - previous.At).TotalMilliseconds;
            double cpuMs = (current - previous.Cpu).TotalMilliseconds;
            if (wallMs > 0)
                value = Math.Max(0, cpuMs / (wallMs * Environment.ProcessorCount) * 100d);
        }

        _cpuPrevious[process.Id] = (current, now);
        return value;
    }

    private (double ReadRate, double WriteRate, double ReadTotal, double WriteTotal) CalculateIo(Process process)
    {
        if (!NativeMethods.GetProcessIoCounters(process.Handle, out NativeMethods.IO_COUNTERS counters))
            return (0, 0, 0, 0);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        double readRate = 0;
        double writeRate = 0;

        if (_ioPrevious.TryGetValue(process.Id, out var previous))
        {
            double seconds = (now - previous.At).TotalSeconds;
            if (seconds > 0)
            {
                readRate = (counters.ReadTransferCount - previous.Read) / 1024d / 1024d / seconds;
                writeRate = (counters.WriteTransferCount - previous.Write) / 1024d / 1024d / seconds;
            }
        }

        _ioPrevious[process.Id] = (counters.ReadTransferCount, counters.WriteTransferCount, now);
        return (
            readRate,
            writeRate,
            counters.ReadTransferCount / 1024d / 1024d,
            counters.WriteTransferCount / 1024d / 1024d);
    }

    private static int SafeGetHandleCount(Process process)
    {
        try { return process.HandleCount; }
        catch { return 0; }
    }

    private static IReadOnlyList<RawProcess> EnumerateProcesses()
    {
        var result = new List<RawProcess>();
        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == new IntPtr(-1))
            return result;

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32>(),
                szExeFile = string.Empty
            };

            if (!NativeMethods.Process32First(snapshot, ref entry))
                return result;

            do
            {
                result.Add(new RawProcess(
                    (int)entry.th32ProcessID,
                    (int)entry.th32ParentProcessID,
                    entry.szExeFile,
                    entry.cntThreads));
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return result;
    }

    private static IReadOnlyList<RawProcess> FindDescendants(int rootPid, IReadOnlyList<RawProcess> all)
    {
        var byParent = all.GroupBy(x => x.ParentPid).ToDictionary(x => x.Key, x => x.ToList());
        var byPid = all.ToDictionary(x => x.Pid);
        var result = new List<RawProcess>();
        var queue = new Queue<int>();
        var visited = new HashSet<int>();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            if (!visited.Add(pid))
                continue;

            if (byPid.TryGetValue(pid, out RawProcess? process))
                result.Add(process);

            if (!byParent.TryGetValue(pid, out List<RawProcess>? children))
                continue;

            foreach (RawProcess child in children)
                queue.Enqueue(child.Pid);
        }

        return result;
    }
}
