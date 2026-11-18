using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace LeBot.Infrastructure.Telegram;

/// <summary>
/// Runs update handlers so that one slow update holds up only its own chat (ADR&#160;0016). Work queued
/// under the same key runs strictly in the order it was queued; work under different keys runs side by
/// side; and no more than <c>maxConcurrency</c> items run at once.
/// </summary>
/// <remarks>
/// <para>An item takes a slot only once its key's previous item has finished, so a chat with work piled up
/// behind a slow (or stuck) update holds one slot at most — it can neither crowd out the other chats nor
/// pin slots while it waits.</para>
/// <para>Each item is its own boundary: whatever escapes it is logged, and the key's next item still runs.
/// Once the token is cancelled, items not yet started are dropped; running ones see the same token.</para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A SemaphoreSlim holds nothing to free unless its AvailableWaitHandle is read, which this class never does. Disposing it could only make work abandoned at shutdown throw on its way out.")]
internal sealed class ChatUpdateScheduler
{
    private readonly SemaphoreSlim _slots;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    /// <summary>Keys with work running or waiting. A key leaves once its last queued item has finished.</summary>
    private readonly Dictionary<long, Lane> _lanes = [];

    public ChatUpdateScheduler(int maxConcurrency, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _logger = logger;
    }

    /// <summary>
    /// Queues <paramref name="work"/> behind everything already queued under <paramref name="key"/> and
    /// returns at once — the caller never runs, or waits on, any of the work itself.
    /// </summary>
    public void Enqueue(long key, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_lanes.TryGetValue(key, out var lane))
            {
                lane = new Lane();
                _lanes[key] = lane;
            }

            lane.Queued++;
            var previous = lane.Tail;

            // On the pool, not inline: not even the part of a handler before its first await may run on the
            // caller's thread — that thread is the poll loop. CancellationToken.None so the item always starts
            // and its bookkeeping always runs; a cancelled one drops itself straight away.
            lane.Tail = Task.Run(() => RunAsync(key, lane, previous, work, cancellationToken), CancellationToken.None);
        }
    }

    /// <summary>
    /// Completes once every item queued so far has finished or been dropped. Never faults. An item that
    /// ignores cancellation keeps it pending, so a caller that must not hang bounds the wait itself.
    /// </summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_lanes.Values.Select(lane => lane.Tail).ToList());
        }
    }

    private async Task RunAsync(long key, Lane lane, Task previous, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        try
        {
            // Never faults — this method catches everything — and isn't abandoned on cancellation either, so a
            // lane's tail finishes only after every item before it: that's what WhenIdleAsync relies on.
            await previous;
            await _slots.WaitAsync(cancellationToken);
            try
            {
                await work(cancellationToken);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: an item that hadn't started is dropped, one that had has stopped.
        }
        catch (Exception ex)
        {
            // The boundary of last resort — the dispatcher's own handler logs everything it can under the
            // update's CorrelationId. Whatever still gets here mustn't break the chat's lane.
            _logger.LogError(ex, "Unhandled error in queued work for chat {ChatId}", key);
        }
        finally
        {
            lock (_gate)
            {
                if (--lane.Queued == 0)
                {
                    _lanes.Remove(key);
                }
            }
        }
    }

    /// <summary>One key's queue: the last item queued, which every later one waits on, and how many are left.</summary>
    private sealed class Lane
    {
        public Task Tail { get; set; } = Task.CompletedTask;

        public int Queued { get; set; }
    }
}
