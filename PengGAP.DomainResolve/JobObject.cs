using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PengGAP.DomainResolve
{
    /// <summary>
    /// Windows 作业对象
    /// 把子进程放进设置了 KILL_ON_JOB_CLOSE 的作业里：
    /// 只要父进程退出（无论是正常关闭、崩溃还是被任务管理器强杀），
    /// 系统都会自动结束该作业里的全部进程，从而避免孤儿进程
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class JobObject : IDisposable
    {
        private const int JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private IntPtr handle;

        /// <summary>
        /// 作业对象
        /// </summary>
        public JobObject()
        {
            this.handle = CreateJobObject(IntPtr.Zero, null);
            if (this.handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                Marshal.StructureToPtr(info, buffer, fDeleteOld: false);

                if (SetInformationJobObject(this.handle, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION, buffer, (uint)length) == false)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// 把进程加入作业
        /// </summary>
        /// <param name="process"></param>
        /// <returns></returns>
        public bool TryAssign(Process process)
        {
            if (this.handle == IntPtr.Zero || process.HasExited == true)
            {
                return false;
            }
            return AssignProcessToJobObject(this.handle, process.Handle);
        }

        /// <summary>
        /// 关闭作业句柄
        /// 设置了 KILL_ON_JOB_CLOSE 时，关闭句柄会连带结束作业内的所有进程
        /// </summary>
        public void Dispose()
        {
            if (this.handle != IntPtr.Zero)
            {
                CloseHandle(this.handle);
                this.handle = IntPtr.Zero;
            }
        }
    }
}
