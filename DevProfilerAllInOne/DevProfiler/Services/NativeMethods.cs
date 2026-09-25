using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevProfiler.Services;

internal static class NativeMethods
{
    internal const uint TH32CS_SNAPPROCESS = 0x00000002;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint PROCESS_SET_QUOTA = 0x0100;
    internal const uint PROCESS_TERMINATE = 0x0001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32
    {
        internal uint dwSize;
        internal uint cntUsage;
        internal uint th32ProcessID;
        internal IntPtr th32DefaultHeapID;
        internal uint th32ModuleID;
        internal uint cntThreads;
        internal uint th32ParentProcessID;
        internal int pcPriClassBase;
        internal uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal IntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS_JOB
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        internal long TotalUserTime;
        internal long TotalKernelTime;
        internal long ThisPeriodTotalUserTime;
        internal long ThisPeriodTotalKernelTime;
        internal uint TotalPageFaultCount;
        internal uint TotalProcesses;
        internal uint ActiveProcesses;
        internal uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        internal JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        internal IO_COUNTERS_JOB IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    internal const int JobObjectBasicAccountingInformation = 1;
    internal const int JobObjectBasicProcessIdList = 3;
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const int ERROR_MORE_DATA = 234;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetInformationJobObject(
        SafeFileHandle hJob,
        int JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool QueryInformationJobObject(
        SafeFileHandle hJob,
        int JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);
}

public sealed class JobObject : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public JobObject()
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create Windows Job Object.");

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        int length = Marshal.SizeOf(info);
        IntPtr pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!NativeMethods.SetInformationJobObject(
                    _handle,
                    NativeMethods.JobObjectExtendedLimitInformation,
                    pointer,
                    (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure Windows Job Object.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public bool AddProcess(Process process)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(JobObject));

        if (NativeMethods.AssignProcessToJobObject(_handle, process.Handle))
            return true;

        int error = Marshal.GetLastWin32Error();
        // ERROR_ACCESS_DENIED commonly means the process already belongs to a non-breakaway job.
        if (error == 5)
            return false;

        throw new Win32Exception(error, "Unable to assign process to Windows Job Object.");
    }

    public IReadOnlyList<int> GetProcessIds()
    {
        if (_disposed)
            return [];

        int capacity = 16;
        while (true)
        {
            int length = checked(8 + capacity * IntPtr.Size);
            IntPtr pointer = Marshal.AllocHGlobal(length);
            try
            {
                for (int index = 0; index < length; index++)
                    Marshal.WriteByte(pointer, index, 0);

                if (NativeMethods.QueryInformationJobObject(
                        _handle,
                        NativeMethods.JobObjectBasicProcessIdList,
                        pointer,
                        (uint)length,
                        out _))
                {
                    int count = Marshal.ReadInt32(pointer, 4);
                    var result = new List<int>(count);
                    for (int index = 0; index < count; index++)
                    {
                        long value = IntPtr.Size == 8
                            ? Marshal.ReadInt64(pointer, 8 + index * IntPtr.Size)
                            : Marshal.ReadInt32(pointer, 8 + index * IntPtr.Size);
                        if (value is > 0 and <= int.MaxValue)
                            result.Add((int)value);
                    }
                    return result;
                }

                int error = Marshal.GetLastWin32Error();
                if (error != NativeMethods.ERROR_MORE_DATA)
                    throw new Win32Exception(error, "Unable to query Windows Job Object process list.");

                int assigned = Math.Max(0, Marshal.ReadInt32(pointer, 0));
                capacity = Math.Max(capacity * 2, assigned + 8);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    public int ActiveProcessCount
    {
        get
        {
            if (_disposed)
                return 0;

            int length = Marshal.SizeOf<NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
            IntPtr pointer = Marshal.AllocHGlobal(length);
            try
            {
                if (!NativeMethods.QueryInformationJobObject(
                        _handle,
                        NativeMethods.JobObjectBasicAccountingInformation,
                        pointer,
                        (uint)length,
                        out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query Windows Job Object state.");
                }

                var info = Marshal.PtrToStructure<NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(pointer);
                return checked((int)info.ActiveProcesses);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    public void Terminate(uint exitCode = 1)
    {
        if (!_disposed && !_handle.IsInvalid)
            NativeMethods.TerminateJobObject(_handle, exitCode);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handle.Dispose();
    }
}
