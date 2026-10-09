using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Ties helper processes (the shader compiler) to this one: they are ended when it ends.
    /// </summary>
    /// <remarks>
    /// A game that quit while a shader was compiling on a worker left dxc.exe running, and with it
    /// the copies of this process's own output pipes it had inherited — whoever launched the game
    /// and read its output then waited for a compiler nobody needed any more (a test case took 16 s
    /// longer than its game). A Windows job object that kills its processes when its last handle
    /// closes does this without any shutdown code: the handle is held for the life of the process
    /// and the system closes it on exit, however the process ends.
    /// </remarks>
    internal static class ChildProcessJob
    {
        private static readonly object Gate = new();
        private static IntPtr _job;
        private static bool _unavailable;

        /// <summary>Ends <paramref name="process"/> with this one. Best effort: a failure changes nothing else.</summary>
        public static void Adopt(Process process)
        {
            if (process == null || !OperatingSystem.IsWindows()) return;
            try
            {
                IntPtr job = Job();
                if (job != IntPtr.Zero) AssignProcessToJobObject(job, process.Handle);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already exited, or not ours to place: nothing to tie.
            }
        }

        private static IntPtr Job()
        {
            lock (Gate)
            {
                if (_job != IntPtr.Zero || _unavailable) return _job;
                IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero) { _unavailable = true; return IntPtr.Zero; }
                var limits = new JobObjectExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)size))
                {
                    CloseHandle(job);
                    _unavailable = true;
                    return IntPtr.Zero;
                }

                _job = job; // Never closed: the system closes it when this process ends.
                return _job;
            }
        }

        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
