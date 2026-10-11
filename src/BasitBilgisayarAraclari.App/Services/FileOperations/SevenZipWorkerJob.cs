using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BasitBilgisayarAraclari.App.Services.FileOperations;

/// <summary>Owns the Windows resource limits and lifetime of one archive worker.</summary>
internal sealed class SevenZipWorkerJob : IDisposable
{
    private const uint LimitActiveProcess = 0x00000008;
    private const uint LimitProcessMemory = 0x00000100;
    private const uint LimitJobMemory = 0x00000200;
    private const uint LimitKillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformationClass = 9;
    private readonly SafeJobHandle _handle;

    public SevenZipWorkerJob(long memoryLimitBytes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Archive worker jobs require Windows.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryLimitBytes);
        var memoryLimit = checked((nuint)memoryLimitBytes);

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Could not create the archive worker job.");
        }

        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = LimitActiveProcess | LimitProcessMemory | LimitJobMemory | LimitKillOnJobClose,
                    ActiveProcessLimit = 1
                },
                ProcessMemoryLimit = memoryLimit,
                JobMemoryLimit = memoryLimit
            };
            if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, ref limits,
                    checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not apply archive worker job limits.");

            _handle = handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Assigns a started worker. The caller must withhold archive input until this succeeds,
    /// and terminate the worker if assignment fails.
    /// </summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the archive worker to its job.");
    }

    public void Terminate(uint exitCode = 1)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (!TerminateJobObject(_handle, exitCode))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not terminate the archive worker job.");
    }

    public void Dispose() => _handle.Dispose();

    // SIZE_T and ULONG_PTR fields must retain native pointer width; default native
    // alignment gives the extended structure its 144-byte layout on x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
