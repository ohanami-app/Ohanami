using System;
using System.Threading;
using Ohanami.Core.Power;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>SleepGuard と PowerEvents(スリープの抑止と、復帰の通知)。実際のスリープ・復帰は手動で確かめる(ADR 0026)。ここでは、登録・解除・通知の解釈を確かめる。</summary>
public sealed class PowerTests
{
    [Fact]
    public void A_sleep_guard_starts_and_can_be_disposed_twice()
    {
        SleepGuard guard = SleepGuard.Start();

        guard.Dispose();
        guard.Dispose();
    }

    [Fact]
    public void Several_sleep_guards_are_independent()
    {
        // スレッド単位の設定なので、2 つあっても、片方の解除が他方に影響しない(どちらも専用のスレッド)
        SleepGuard a = SleepGuard.Start();
        SleepGuard b = SleepGuard.Start();

        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void Power_events_register_with_the_os_and_unregister_on_dispose()
    {
        var events = new PowerEvents();

        Assert.True(events.Registered, "この機(Windows 11)では登録できるはず: " + events.RegistrationError);
        Assert.Null(events.RegistrationError);
        events.Dispose();
        events.Dispose();
    }

    [Theory]
    [InlineData(PowerEvents.ResumeSuspend, 1)]
    [InlineData(PowerEvents.ResumeAutomatic, 1)]
    [InlineData(PowerEvents.Suspend, 0)]
    [InlineData(0u, 0)]
    [InlineData(10u, 0)]
    public void Only_the_two_resume_notifications_raise_the_resumed_event(uint type, int expected)
    {
        using var events = new PowerEvents();
        int raised = 0;
        events.Resumed += () => Interlocked.Increment(ref raised);

        events.Handle(type);

        Assert.Equal(expected, raised);
        Assert.Equal(expected, events.ResumeCount);
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_break_the_os_callback_and_later_notifications_still_arrive()
    {
        using var events = new PowerEvents();
        int raised = 0;
        events.Resumed += () => throw new InvalidOperationException("購読者の失敗(テスト)");
        events.Resumed += () => Interlocked.Increment(ref raised);

        events.Handle(PowerEvents.ResumeAutomatic);
        events.Handle(PowerEvents.ResumeSuspend);

        Assert.Equal(2, events.ResumeCount);
        Assert.Equal(0, raised); // 最初の購読者が投げたので、2 つ目には届かない(購読者の例外は握りつぶすだけ。OS のコールバックには伝えない)
    }

    [Fact]
    public void After_dispose_the_subscribers_are_released()
    {
        var events = new PowerEvents();
        int raised = 0;
        events.Resumed += () => Interlocked.Increment(ref raised);
        events.Dispose();

        events.Handle(PowerEvents.ResumeAutomatic);

        Assert.Equal(0, raised);
    }
}
