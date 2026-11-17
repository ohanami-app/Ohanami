using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ohanami.Core.Power;

/// <summary>
/// ジョブの実行中だけ、システムのスリープ(アイドルによる)を抑止する(ADR 0028)。
/// <c>SetThreadExecutionState</c> はスレッド単位なので、専用スレッドで <c>ES_CONTINUOUS | ES_SYSTEM_REQUIRED</c>(画面は消えてよい)を設定し、
/// 解除(<c>ES_CONTINUOUS</c> だけで呼び直し)も同じスレッドで行う。async の継続は別のスレッドで走るため、呼び出し側のスレッドは使わない。
/// 蓋を閉じる・利用者が明示的にスリープさせる、などの操作は止められない(スリープからの復帰は <see cref="PowerEvents"/>)。
/// </summary>
internal sealed class SleepGuard : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private Exception? _startError;
    private int _disposed;

    private SleepGuard()
    {
        _thread = new Thread(Body) { IsBackground = true, Name = "sleep-guard" };
    }

    /// <summary>スリープの抑止を始める。設定できなければ <see cref="InvalidOperationException"/>。</summary>
    public static SleepGuard Start()
    {
        var g = new SleepGuard();
        g._thread.Start();
        g._started.Wait();
        if (g._startError is not null)
        {
            g.Dispose();
            throw new InvalidOperationException("スリープ抑止(SetThreadExecutionState)を設定できなかった: " + g._startError.Message, g._startError);
        }
        return g;
    }

    private void Body()
    {
        try
        {
            if (SetThreadExecutionState(EsContinuous | EsSystemRequired) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception e)
        {
            _startError = e;
            _started.Set();
            return;
        }
        _started.Set();
        _release.Wait();
        // 終了時に ES_CONTINUOUS だけで呼び直して、元に戻す(同じスレッドで)
        SetThreadExecutionState(EsContinuous);
    }

    /// <summary>抑止を解除する。2 回呼んでもよい。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        _thread.Join(5000);
        _started.Dispose();
        _release.Dispose();
    }
}
