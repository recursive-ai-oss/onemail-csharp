namespace RecursiveAI.OneMail.Client.Protocol;

/// <summary>
/// A timeout source that one reader or writer reuses for every operation, so a download made of thousands of reads allocates
/// the same as one made of a single read. The source is linked to the caller's token once (the token rarely changes between
/// reads of one command) and its timer is re-armed with <c>CancelAfter</c>, which allocates nothing after the first call.
/// </summary>
/// <remarks>Not thread-safe: the reader and the writer each own one, and each runs one operation at a time.</remarks>
internal sealed class ReusableDeadline : IDisposable
{
    private CancellationTokenSource? _cts;
    private CancellationToken _linkedTo;

    /// <summary>Arms the deadline and returns the token to pass to the operation.</summary>
    /// <param name="timeout">The time allowed; infinite or non-positive means none.</param>
    /// <param name="token">The caller's token.</param>
    /// <returns>The caller's token itself when there is no timeout to enforce, else the reusable source's token.</returns>
    public CancellationToken Begin(TimeSpan timeout, CancellationToken token)
    {
        if (timeout == Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero)
        {
            return token;
        }

        if (_cts is null || _cts.IsCancellationRequested || _linkedTo != token)
        {
            _cts?.Dispose();
            _cts = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token) : new CancellationTokenSource();
            _linkedTo = token;
        }

        _cts.CancelAfter(timeout);
        return _cts.Token;
    }

    /// <summary>Disarms the timer after the operation, whatever its outcome.</summary>
    public void End()
    {
        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            _cts.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }
}
