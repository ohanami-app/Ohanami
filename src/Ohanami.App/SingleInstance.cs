using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Ohanami.App;

/// <summary>
/// アプリの単一インスタンス。同じデータのディレクトリを 2 つのプロセスが開かないようにする
/// (DB 側の busy_timeout は最後の砦)。名前付き Mutex(Windows のログオンのセッション内)で所有を決め、2 つ目の起動は名前付きイベントで
/// 1 つ目に「前に出して」と伝える。名前はデータのディレクトリから作る(OHANAMI_DATA_DIR が違えば別のインスタンス=手元の確認が本物と衝突しない)。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly Thread _listener;
    private volatile bool _stop;
    private readonly bool _owner;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, bool owner)
    {
        _mutex = mutex;
        _activate = activate;
        _owner = owner;
        _listener = new Thread(Listen) { IsBackground = true, Name = "single-instance-listener" };
    }

    /// <summary>1 つ目の起動(所有している)なら true。2 つ目なら false(この場合、既存のインスタンスに前に出すよう伝え済み)。</summary>
    public bool IsOwner => _owner;

    /// <summary>2 つ目の起動から「前に出して」と伝えられたとき(所有者のバックグラウンドのスレッドで呼ばれる)。</summary>
    public event Action? ActivationRequested;

    public static SingleInstance Acquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        string id = NameFor(dataDirectory);
        var mutex = new Mutex(initiallyOwned: false, @"Local\Ohanami.Instance." + id, out _);
        bool owner;
        try { owner = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owner = true; } // 前のプロセスが落ちて所有を手放していた。引き継ぐ
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Ohanami.Activate." + id);
        var instance = new SingleInstance(mutex, activate, owner);
        if (owner) instance._listener.Start();
        else activate.Set(); // 既存のインスタンスに、ウィンドウを前に出すよう伝える
        return instance;
    }

    /// <summary>データのディレクトリ(大文字小文字・末尾の区切りを無視)から、名前に使える短い文字列を作る。</summary>
    internal static string NameFor(string dataDirectory)
    {
        string normalized = System.IO.Path.GetFullPath(dataDirectory).TrimEnd('\\', '/').ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLower(CultureInfo.InvariantCulture);
    }

    private void Listen()
    {
        while (!_stop)
        {
            try
            {
                if (_activate.WaitOne(500) && !_stop) ActivationRequested?.Invoke();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_owner)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* 別のスレッドで取得していた場合など。プロセスの終了で解放される */ }
        }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
