using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ohanami.Core.Power;

/// <summary>
/// スリープからの復帰の通知(ADR 0026)。<c>PowerRegisterSuspendResumeNotification</c>(<c>DEVICE_NOTIFY_CALLBACK</c>)で、ウィンドウなしに受ける
/// (NuGet の <c>Microsoft.Win32.SystemEvents</c> は使わない=依存を足さない)。復帰を検知したら <see cref="Resumed"/> を起こす(OS のスレッドから)。
/// 使う側(VlmRunner)は、llama-server に「疑わしい」の印を付け、次の画像の前に <c>/health</c> を確かめる。通知を取りこぼしても、HTTP の失敗で同じ復旧が働く。
/// 登録できなくても(古い OS など)、例外にはせず <see cref="Registered"/> が false になる(通知なしで動く)。
/// </summary>
internal sealed class PowerEvents : IDisposable
{
    /// <summary>PBT_APMRESUMESUSPEND(利用者の操作で復帰)。</summary>
    internal const uint ResumeSuspend = 7;

    /// <summary>PBT_APMRESUMEAUTOMATIC(自動の復帰。利用者の操作が無くても来る)。</summary>
    internal const uint ResumeAutomatic = 18;

    /// <summary>PBT_APMSUSPEND(スリープに入る直前)。</summary>
    internal const uint Suspend = 4;

    private const uint DeviceNotifyCallback = 2;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);

    // OS が呼ぶ間、デリゲートと引数の構造体を生かしておく
    private readonly DeviceNotifyCallbackRoutine _callback;
    private IntPtr _parameters;
    private IntPtr _registration;
    private int _disposed;
    private long _resumeCount;

    /// <summary>復帰したとき(OS のスレッドから)。購読者は軽い処理だけをする(印を付けるなど)。</summary>
    public event Action? Resumed;

    public PowerEvents()
    {
        _callback = OnNotify;
        try
        {
            _parameters = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceNotifySubscribeParameters>());
            Marshal.StructureToPtr(new DeviceNotifySubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(_callback), Context = IntPtr.Zero }, _parameters, fDeleteOld: false);
            uint error = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _parameters, out IntPtr handle);
            if (error != 0) throw new Win32Exception((int)error, "PowerRegisterSuspendResumeNotification に失敗");
            _registration = handle;
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            // 登録できなくても、通知なしで動く(HTTP の失敗でも復旧できる)
            RegistrationError = e.Message;
            FreeParameters();
        }
    }

    /// <summary>OS への登録に成功したか。</summary>
    public bool Registered => _registration != IntPtr.Zero;

    /// <summary>登録に失敗したときの理由(成功なら null)。</summary>
    public string? RegistrationError { get; }

    /// <summary>これまでに通知された復帰の回数。</summary>
    public long ResumeCount => Interlocked.Read(ref _resumeCount);

    private uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        Handle(type);
        return 0;
    }

    /// <summary>通知の種類を解釈する(テストからも呼ぶ。OS のコールバックと同じ入口)。復帰の 2 種類だけを <see cref="Resumed"/> にする。</summary>
    internal void Handle(uint type)
    {
        if (type is not (ResumeSuspend or ResumeAutomatic)) return;
        Interlocked.Increment(ref _resumeCount);
        try { Resumed?.Invoke(); }
        catch (Exception) { /* 購読者の例外で、OS のコールバックを壊さない */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IntPtr registration = _registration;
        _registration = IntPtr.Zero;
        if (registration != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(registration);
        FreeParameters();
        Resumed = null;
    }

    private void FreeParameters()
    {
        IntPtr p = _parameters;
        _parameters = IntPtr.Zero;
        if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
    }
}
