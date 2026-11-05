using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DropHalo.Engines.Tools;

/// <summary>A per-run Windows job: the process starts suspended so no child can escape before assignment.</summary>
[SupportedOSPlatform("windows")]
internal sealed class KillOnCloseJob : IDisposable
{
    private static readonly Lazy<JobHandle> AppJob = new(CreateJob);
    private readonly JobHandle _job;

    private KillOnCloseJob(JobHandle job, Process process, StreamReader stdout, StreamReader stderr)
    {
        _job = job;
        Process = process;
        StandardOutput = stdout;
        StandardError = stderr;
    }

    public Process Process { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }

    public async Task KillAsync()
    {
        // TerminateJobObject can remove members from accounting before their handles are signaled.
        // Retain and wait for each process first, including children born during an earlier snapshot.
        while (true)
        {
            var ids = MemberIds();
            if (ids.Length == 0)
            {
                Check(TerminateJobObject(_job, 1));
                await Task.Run(Process.WaitForExit).ConfigureAwait(false);
                return;
            }

            var members = new List<SafeProcessHandle>();
            try
            {
                foreach (var id in ids)
                {
                    var handle = OpenProcess(0x00101001, false, id); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION | TERMINATE
                    if (handle.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        if (error == 87 || (error == 5 && !MemberIds().Contains(id)))
                        {
                            continue; // Exited between the snapshot and OpenProcess.
                        }

                        throw new Win32Exception(error);
                    }

                    try
                    {
                        Check(IsProcessInJob(handle, _job, out var belongs));
                        if (belongs)
                        {
                            members.Add(handle);
                            handle = null!; // The retained handle pins this identity until the wait completes.
                        }
                    }
                    finally
                    {
                        handle?.Dispose();
                    }
                }

                foreach (var member in members)
                {
                    if (!TerminateProcess(member, 1) && Marshal.GetLastWin32Error() != 5)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    // With PROCESS_TERMINATE access, error 5 means it already terminated; still await its signal.
                }

                await Task.Run(() =>
                {
                    foreach (var member in members)
                    {
                        Check(WaitForSingleObject(member, uint.MaxValue) == 0);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                foreach (var member in members)
                {
                    member.Dispose();
                }
            }
        }
    }

    private uint[] MemberIds()
    {
        for (var capacity = 16; ; capacity = checked(capacity * 2))
        {
            var size = checked(8 + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(_job, 3, buffer, (uint)size, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 234) // ERROR_MORE_DATA: the job grew while taking the snapshot.
                    {
                        continue;
                    }

                    throw new Win32Exception(error);
                }

                var count = Marshal.ReadInt32(buffer, 4);
                if (Marshal.ReadInt32(buffer) > count)
                {
                    continue;
                }

                var ids = new uint[count];
                for (var i = 0; i < count; i++)
                {
                    ids[i] = checked((uint)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size));
                }

                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Background children of a normally completed tool still belong to the app's parent job.</summary>
    public void PreserveChildren()
    {
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        Check(SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()));
    }

    public void Dispose()
    {
        _job.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
    }

    public static KillOnCloseJob Start(ProcessStartInfo start)
    {
        var job = CreateJob();
        SafeFileHandle? outputRead = null, errorRead = null;
        StreamReader? stdout = null, stderr = null;
        Process? process = null;
        try
        {
            var security = new SECURITY_ATTRIBUTES { Length = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), InheritHandle = true };
            Check(CreatePipe(out outputRead, out var outputWrite, ref security, 0));
            using (outputWrite)
            {
                Check(CreatePipe(out errorRead, out var errorWrite, ref security, 0));
                using (errorWrite)
                using (var input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero))
                {
                    Check(!input.IsInvalid);
                    Check(SetHandleInformation(outputRead, 1, 0));
                    Check(SetHandleInformation(errorRead, 1, 0));
                    var info = CreateSuspended(start, input, outputWrite, errorWrite);
                    using var nativeProcess = new SafeProcessHandle(info.Process, ownsHandle: true);
                    using var thread = new SafeWaitHandle(info.Thread, ownsHandle: true);
                    try
                    {
                        Check(AssignProcessToJobObject(AppJob.Value, nativeProcess));
                        Check(AssignProcessToJobObject(job, nativeProcess));
                        process = Process.GetProcessById((int)info.ProcessId);
                        _ = process.SafeHandle;
                        stdout = new StreamReader(new FileStream(outputRead, FileAccess.Read), start.StandardOutputEncoding ?? Encoding.UTF8);
                        outputRead = null;
                        stderr = new StreamReader(new FileStream(errorRead, FileAccess.Read), start.StandardErrorEncoding ?? Encoding.UTF8);
                        errorRead = null;
                        Check(ResumeThread(thread) != uint.MaxValue);
                    }
                    catch
                    {
                        // A setup failure must not leave a suspended or uncontained process behind.
                        TerminateProcess(nativeProcess, 1);
                        _ = WaitForSingleObject(nativeProcess, uint.MaxValue);
                        throw;
                    }
                }
            }

            return new(job, process!, stdout!, stderr!);
        }
        catch
        {
            job.Dispose();
            process?.Dispose();
            stdout?.Dispose();
            stderr?.Dispose();
            outputRead?.Dispose();
            errorRead?.Dispose();
            throw;
        }
    }

    private static JobHandle CreateJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        try
        {
            Check(!job.IsInvalid);
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()));
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private static PROCESS_INFORMATION CreateSuspended(ProcessStartInfo start, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
    {
        nuint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(checked((nint)size));
        var handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
        var environment = Marshal.StringToHGlobalUni(string.Join('\0', start.Environment
            .Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key + "=" + p.Value)) + "\0\0");
        var initialized = false;
        try
        {
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            initialized = true;
            Marshal.WriteIntPtr(handles, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, output.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, error.DangerousGetHandle());
            // Only this run's standard handles may be inherited, even when two lanes launch together.
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    Size = (uint)Marshal.SizeOf<STARTUPINFOEX>(), Flags = 0x100,
                    StandardInput = input.DangerousGetHandle(), StandardOutput = output.DangerousGetHandle(), StandardError = error.DangerousGetHandle(),
                },
                AttributeList = attributes,
            };
            var command = new StringBuilder();
            AppendArgument(command, start.FileName);
            foreach (var argument in start.ArgumentList)
            {
                command.Append(' ');
                AppendArgument(command, argument);
            }

            // CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW
            Check(CreateProcessW(start.FileName, (command.ToString() + "\0").ToCharArray(), IntPtr.Zero, IntPtr.Zero, true, 0x08080404,
                environment, start.WorkingDirectory, ref startup, out var info));
            return info;
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(environment);
            GC.KeepAlive(input);
            GC.KeepAlive(output);
            GC.KeepAlive(error);
        }
    }

    private static void AppendArgument(StringBuilder command, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"'))
        {
            command.Append(argument);
            return;
        }

        command.Append('"');
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            command.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }

        command.Append('\\', slashes * 2).Append('"');
    }

    private static void Check(bool succeeded)
    {
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeHandle job, SafeHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeHandle job, int infoClass, IntPtr information, uint size, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeHandle process, SafeHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, ref SECURITY_ATTRIBUTES security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, [In, Out] char[] command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class JobHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
    {
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}
