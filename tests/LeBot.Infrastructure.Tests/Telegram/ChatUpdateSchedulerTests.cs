using System.Collections.Concurrent;
using LeBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeBot.Infrastructure.Tests.Telegram;

public sealed class ChatUpdateSchedulerTests : IDisposable
{
    private const long ChatA = -1001;
    private const long ChatB = -1002;

    /// <summary>Only a guard against a hung run: every wait here is released by the test itself.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stopping = new();

    public void Dispose() => _stopping.Dispose();

    [Fact]
    public async Task Enqueue_SlowUpdateInOneChat_DoesNotHoldUpAnotherChat()
    {
        var sut = Sut();
        var slow = new TaskCompletionSource();
        var otherChatHandled = new TaskCompletionSource();
        sut.Enqueue(ChatA, _ => slow.Task, _stopping.Token);

        sut.Enqueue(ChatB, _ => Complete(otherChatHandled), _stopping.Token);

        Func<Task> otherChat = () => otherChatHandled.Task;
        await otherChat.Should().CompleteWithinAsync(Patience);
        slow.Task.IsCompleted.Should().BeFalse("chat B was served while chat A's update was still running");
        slow.SetResult();
    }

    [Fact]
    public async Task Enqueue_TwoUpdatesOfOneChat_SecondWaitsForTheFirst()
    {
        var sut = Sut();
        var order = new ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var otherChatHandled = new TaskCompletionSource();
        sut.Enqueue(ChatA, async _ =>
        {
            order.Enqueue("first started");
            firstStarted.SetResult();
            await releaseFirst.Task;
            order.Enqueue("first finished");
        }, _stopping.Token);
        sut.Enqueue(ChatA, _ => Record(order, "second"), _stopping.Token);

        // A full turn for another chat while the first is parked: a second that didn't wait would have run by now.
        await firstStarted.Task.WaitAsync(Patience);
        sut.Enqueue(ChatB, _ => Complete(otherChatHandled), _stopping.Token);
        await otherChatHandled.Task.WaitAsync(Patience);
        releaseFirst.SetResult();
        await sut.WhenIdleAsync().WaitAsync(Patience);

        order.Should().Equal("first started", "first finished", "second");
    }

    [Fact]
    public async Task Enqueue_ManyUpdatesOfOneChat_RunInTheOrderQueued()
    {
        var sut = Sut(maxConcurrency: 4);
        var order = new ConcurrentQueue<int>();

        for (var i = 0; i < 50; i++)
        {
            var n = i;
            sut.Enqueue(ChatA, async _ =>
            {
                await Task.Yield();
                order.Enqueue(n);
            }, _stopping.Token);
        }

        await sut.WhenIdleAsync().WaitAsync(Patience);

        order.Should().Equal(Enumerable.Range(0, 50));
    }

    [Fact]
    public async Task Enqueue_UpdateThrows_LaterUpdatesOfThatChatAndOthersStillRun()
    {
        var sut = Sut();
        var handled = new ConcurrentQueue<string>();

        sut.Enqueue(ChatA, _ => throw new InvalidOperationException("one bad update"), _stopping.Token);
        sut.Enqueue(ChatA, async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("one more, failing after its first await");
        }, _stopping.Token);
        sut.Enqueue(ChatA, _ => Record(handled, "chat A, next update"), _stopping.Token);
        sut.Enqueue(ChatB, _ => Record(handled, "chat B"), _stopping.Token);
        await sut.WhenIdleAsync().WaitAsync(Patience);

        handled.Should().BeEquivalentTo("chat A, next update", "chat B");
    }

    [Fact]
    public async Task Enqueue_ChatWaitingBehindItsOwnSlowUpdate_TakesNoSlotFromOtherChats()
    {
        var sut = Sut(maxConcurrency: 2);
        var stuck = new TaskCompletionSource();
        var otherChatHandled = new TaskCompletionSource();
        sut.Enqueue(ChatA, _ => stuck.Task, _stopping.Token);
        sut.Enqueue(ChatA, _ => Task.CompletedTask, _stopping.Token);
        sut.Enqueue(ChatA, _ => Task.CompletedTask, _stopping.Token);

        // Two slots: chat A's running update holds one. Were its queued ones holding slots too, B would never run.
        sut.Enqueue(ChatB, _ => Complete(otherChatHandled), _stopping.Token);

        Func<Task> otherChat = () => otherChatHandled.Task;
        await otherChat.Should().CompleteWithinAsync(Patience);
        stuck.SetResult();
    }

    [Fact]
    public async Task Enqueue_MoreBusyChatsThanSlots_RunsNoMoreThanTheLimitAtOnce()
    {
        var sut = Sut(maxConcurrency: 2);
        var gate = new Lock();
        var running = 0;
        var peak = 0;
        var started = 0;
        var slotsFull = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        async Task WorkAsync(CancellationToken _)
        {
            lock (gate)
            {
                running++;
                peak = Math.Max(peak, running);
                if (++started == 2)
                {
                    slotsFull.SetResult();
                }
            }

            await release.Task;
            lock (gate)
            {
                running--;
            }
        }

        for (var chat = 1L; chat <= 4; chat++)
        {
            sut.Enqueue(chat, WorkAsync, _stopping.Token);
        }

        await slotsFull.Task.WaitAsync(Patience);
        release.SetResult();
        await sut.WhenIdleAsync().WaitAsync(Patience);

        peak.Should().Be(2);
        started.Should().Be(4, "the chats over the limit run once a slot frees up");
    }

    [Fact]
    public async Task WhenIdleAsync_UpdateStillRunning_CompletesOnlyOnceItFinishes()
    {
        var sut = Sut();
        var running = new TaskCompletionSource();
        sut.Enqueue(ChatA, _ => running.Task, _stopping.Token);

        var idle = sut.WhenIdleAsync();

        idle.IsCompleted.Should().BeFalse();
        running.SetResult();
        await idle.WaitAsync(Patience);
    }

    [Fact]
    public async Task Enqueue_Cancelled_DropsUpdatesNotYetStarted_AndGoesIdle()
    {
        var sut = Sut();
        var queuedRan = false;
        sut.Enqueue(ChatA, token => Task.Delay(Timeout.Infinite, token), _stopping.Token);
        sut.Enqueue(ChatA, _ =>
        {
            queuedRan = true;
            return Task.CompletedTask;
        }, _stopping.Token);

        await _stopping.CancelAsync();
        await sut.WhenIdleAsync().WaitAsync(Patience);

        queuedRan.Should().BeFalse();
    }

    private static ChatUpdateScheduler Sut(int maxConcurrency = 8) => new(maxConcurrency, NullLogger.Instance);

    private static Task Complete(TaskCompletionSource signal)
    {
        signal.SetResult();
        return Task.CompletedTask;
    }

    private static Task Record<T>(ConcurrentQueue<T> log, T entry)
    {
        log.Enqueue(entry);
        return Task.CompletedTask;
    }
}
