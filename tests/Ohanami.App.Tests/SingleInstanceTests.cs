using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Ohanami.App.Tests;

public sealed class SingleInstanceTests
{
    /// <summary>
    /// 必ず<b>別の新しいスレッド</b>で実行する。Mutex はスレッドが所有するので、同じスレッドでの再取得は成功してしまう(再入)。
    /// スレッドプール(Task.Run)では、空いたスレッド(= 1 つ目を取得したスレッド)で動くことがあり、テストが不安定になった。
    /// </summary>
    private static Task<T> OnNewThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        }) { IsBackground = true };
        thread.Start();
        return tcs.Task;
    }

    // 名前付き Mutex / イベントはユーザーのセッション内で共有される。本物のアプリや別のテストと衝突しないよう、毎回一意のデータのディレクトリ名を使う
    /// <summary>
    /// 1 つ目のインスタンスを、<b>テストが終わるまで生きている専用のスレッド</b>で取得して持つ。Mutex は取得したスレッドが所有するので、そのスレッドが終わると
    /// 「放棄された Mutex」になり、2 つ目が所有者になってしまう。xunit のテストは、await のあとは別のスレッドで続き、最初のスレッドは終わることがある
    /// (テストの実行の順序・並列の度合いで揺れて、まれに、あるいは毎回落ちた)ので、テストの本体のスレッドで取得しない。
    /// </summary>
    private sealed class HeldInstance : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private readonly SingleInstance _instance;

        public HeldInstance(string dataDirectory)
        {
            SingleInstance? created = null;
            Exception? failure = null;
            using var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                try { created = SingleInstance.Acquire(dataDirectory); }
                catch (Exception e) { failure = e; }
                ready.Set();
                if (created is null) return;
                _release.Wait();
                created.Dispose();   // 取得したスレッドで解放する
            }) { IsBackground = true };
            _thread.Start();
            ready.Wait();
            if (failure is not null) throw failure;
            _instance = created!;
        }

        public SingleInstance Instance => _instance;

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _release.Dispose();
        }
    }

    private static string UniqueDataDirectory() => Path.Combine(Path.GetTempPath(), "ohanami-single-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task First_start_owns_and_second_does_not_and_tells_the_first_to_come_forward()
    {
        string dir = UniqueDataDirectory();
        using var held = new HeldInstance(dir);
        SingleInstance first = held.Instance;
        Assert.True(first.IsOwner);
        int activations = 0;
        first.ActivationRequested += () => Interlocked.Increment(ref activations);

        using SingleInstance second = await OnNewThread(() => SingleInstance.Acquire(dir));

        Assert.False(second.IsOwner);
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref activations) >= 1));
    }

    [Fact]
    public async Task The_mutex_is_owned_by_one_thread_so_a_second_acquire_from_another_thread_is_refused()
    {
        // 同じスレッドでの再取得は Mutex の再入で成功してしまうので、アプリの実際の二重起動(別プロセス = 別スレッド)の形で確かめる
        string dir = UniqueDataDirectory();
        using var held = new HeldInstance(dir);

        bool secondOwns = await OnNewThread(() =>
        {
            using SingleInstance second = SingleInstance.Acquire(dir);
            return second.IsOwner;
        });

        Assert.False(secondOwns);
    }

    [Fact]
    public async Task After_the_first_is_disposed_a_new_start_becomes_the_owner()
    {
        string dir = UniqueDataDirectory();
        SingleInstance first = SingleInstance.Acquire(dir);
        Assert.True(first.IsOwner);
        first.Dispose();

        bool secondOwns = await OnNewThread(() =>
        {
            using SingleInstance second = SingleInstance.Acquire(dir);
            return second.IsOwner;
        });

        Assert.True(secondOwns);
    }

    [Fact]
    public async Task Different_data_directories_are_independent_instances()
    {
        using var held = new HeldInstance(UniqueDataDirectory());
        Assert.True(held.Instance.IsOwner);

        bool bOwns = await OnNewThread(() =>
        {
            using SingleInstance b = SingleInstance.Acquire(UniqueDataDirectory());
            return b.IsOwner;
        });

        Assert.True(bOwns);
    }

    [Fact]
    public async Task A_mutex_abandoned_by_an_owner_that_died_is_taken_over_by_the_next_start()
    {
        // 前の起動が落ちて(所有したまま終わって)Mutex が「放棄」された場合は、次の起動が所有者になる(製品の挙動)。
        // これは、テストが「1 つ目を取得したスレッドが終わる」と揺れた原因でもある: Mutex は取得したスレッドが所有するので、そのスレッドが終わると放棄される
        string dir = UniqueDataDirectory();
        // 取得して、Dispose せずにスレッドを終える。スレッドが完全に終わる(Mutex が放棄される)のを、Join で待つ(結果を返した時点では、まだ終わっていない)
        bool firstOwned = false;
        var owner = new Thread(() => firstOwned = SingleInstance.Acquire(dir).IsOwner) { IsBackground = true };
        owner.Start();
        await Task.Run(owner.Join, TestContext.Current.CancellationToken);
        Assert.True(firstOwned);

        bool secondOwns = await OnNewThread(() =>
        {
            using SingleInstance second = SingleInstance.Acquire(dir);
            return second.IsOwner;
        });

        Assert.True(secondOwns);
    }

    [Fact]
    public void The_name_ignores_case_and_a_trailing_separator_but_not_the_directory()
    {
        string dir = UniqueDataDirectory();

        Assert.Equal(SingleInstance.NameFor(dir), SingleInstance.NameFor(dir.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.NotEqual(SingleInstance.NameFor(dir), SingleInstance.NameFor(dir + "-other"));
        Assert.Equal(16, SingleInstance.NameFor(dir).Length);
    }
}
