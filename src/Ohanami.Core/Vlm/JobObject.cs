using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ohanami.Core.Vlm;

/// <summary>
/// Windows のジョブオブジェクト(<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>)。ここに入れた子プロセスは、このオブジェクトのハンドルが閉じられると(親が強制終了されたときも、
/// OS がハンドルを閉じる)OS が終わらせる。孤児を防ぐ 2 層(ADR 0026)の 1 層目(llama-server が VRAM 約 6GB を握ったまま残らないように)。
/// NuGet 不要の P/Invoke だけ。<b>このファイルは、System の型だけに依存する</b>(テスト用の偽サーバのプロジェクトが、ソースのまま取り込んで「親が落ちたら子も消える」を確かめるため)。
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;

    private JobObject(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>ハンドルが閉じられたとき、入っているプロセスを終わらせるジョブを作る。作れなければ <see cref="Win32Exception"/>。</summary>
    public static JobObject CreateKillOnClose()
    {
        IntPtr handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject に失敗");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(handle);
                throw new Win32Exception(error, "SetInformationJobObject(KILL_ON_JOB_CLOSE)に失敗");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return new JobObject(handle);
    }

    /// <summary>プロセスをジョブに入れる。入れられなければ(すでに別のジョブに入っていて入れ子にできない、権限など)<see cref="Win32Exception"/>。</summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        IntPtr handle = _handle;
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        if (!AssignProcessToJobObject(handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"AssignProcessToJobObject に失敗(pid {process.Id})");
    }

    /// <summary>ハンドルを閉じる。ジョブに残っているプロセスは OS が終わらせる。</summary>
    public void Dispose()
    {
        IntPtr handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
