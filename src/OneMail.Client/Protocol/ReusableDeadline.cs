using System.Diagnostics;

namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>
/// The time limit of a connection's reads (or writes), enforced without touching a timer for each operation. A reader that made a
/// timer and a linked cancellation source per read allocated for every one of thousands of reads in a download, and re-arming a
/// timer per request costs a wake-up of the runtime's timer thread on every round trip, which a request/response protocol pays
/// each time.
/// </summary>
/// <remarks>
/// <para>One cancellation source is reused for every operation (linked to the caller's token once, while the token stays the same).
/// An operation only records when it started. A coarse timer, running while operations are in flight, checks every quarter of the
/// limit whether the running operation started longer ago than the limit and cancels the source if so. An operation therefore times
/// out after between one and 1.25 times the limit, the limit is a bound on a stall rather than a stopwatch, and nothing is armed or
/// disarmed per read.</para>
/// <para>Not for concurrent operations: the reader and the writer each own one, and each runs one operation at a time.</para>
/// </remarks>
internal sealed class ReusableDeadline : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private CancellationToken _linkedTo;
    private Timer? _timer;
    private long _started;        // Stopwatch timestamp of the running operation; 0 when none runs
    private long _limit;          // the running operation's limit in Stopwatch ticks
    private int _idleTicks;
    private bool _disposed;

    /// <summary>Starts the clock for one operation and returns the token to pass to it.</summary>
    /// <param name="timeout">The time allowed; infinite or non-positive means none.</param>
    /// <param name="token">The caller's token.</param>
    /// <returns>The caller's token itself when there is no limit to enforce, else the reusable source's token.</returns>
    public CancellationToken Begin(TimeSpan timeout, CancellationToken token)
    {
        if (timeout == Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero)
        {
            return token;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return token;
            }

            if (_cts is null || _cts.IsCancellationRequested || _linkedTo != token)
            {
                _cts?.Dispose();
                _cts = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token) : new CancellationTokenSource();
                _linkedTo = token;
            }

            long limit = (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            if (_timer is null || limit != _limit)
            {
                // A quarter of the limit, but never faster than every 10 ms.
                TimeSpan period = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond * 10, timeout.Ticks / 4));
                if (_timer is null) { _timer = new Timer(static s => ((ReusableDeadline)s!).Check(), this, period, period); }
                else { _timer.Change(period, period); }
            }

            _limit = limit;
            _idleTicks = 0;
            _started = Stopwatch.GetTimestamp();
            return _cts.Token;
        }
    }

    /// <summary>Stops the clock after the operation, whatever its outcome.</summary>
    public void End()
    {
        lock (_gate)
        {
            _started = 0;
        }
    }

    private void Check()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_started == 0)
            {
                // Nothing in flight: after two quiet ticks the timer goes away, so an idle connection holds no timer.
                if (++_idleTicks >= 2)
                {
                    _timer?.Dispose();
                    _timer = null;
                }

                return;
            }

            if (Stopwatch.GetTimestamp() - _started >= _limit)
            {
                _started = 0;
                _cts?.Cancel();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
