using System.Runtime.CompilerServices;
using RecursiveAI.OneMail.Client.Imap.Protocol;
using RecursiveAI.OneMail.Client.Protocol;

namespace RecursiveAI.OneMail.Client.Imap;

public sealed partial class ImapClient
{
    /// <summary>
    /// Waits for the server to report changes (<c>IDLE</c>, RFC 2177) and yields them as they arrive. The state in <see cref="Mailbox"/> is updated before each event is yielded.
    /// </summary>
    /// <remarks>
    /// <para>The enumeration ends when <paramref name="stop"/> is cancelled or the consumer stops iterating: the client then sends <c>DONE</c> and reads the server's
    /// completion, so the connection stays usable. (This is why stopping is a token and not a cancelled read, which would leave the stream mid-response.)</para>
    /// <para>IDLE is restarted every <paramref name="renewEvery"/> (default 29 minutes, RFC 2177 §3) so the server does not time the session out. The connection's read timeout is raised for the duration.</para>
    /// <para>A mailbox must be selected.</para>
    /// </remarks>
    /// <param name="renewEvery">How long one IDLE lasts before it is ended and started again. Default 29 minutes.</param>
    /// <param name="stop">Ends the idle wait. Cancelling it is not an error: the enumeration just ends.</param>
    /// <returns>The events, in the order the server sent them.</returns>
    /// <exception cref="NotSupportedException">The server has no IDLE.</exception>
    /// <exception cref="InvalidOperationException">No mailbox is selected, or another command is running.</exception>
    public async IAsyncEnumerable<ImapEvent> IdleAsync(TimeSpan? renewEvery = null, [EnumeratorCancellation] CancellationToken stop = default)
    {
        RequireCapability("IDLE", "IMAP4rev2");
        if (Mailbox is null)
        {
            throw new InvalidOperationException("IDLE needs a selected mailbox.");
        }

        TimeSpan renew = renewEvery ?? TimeSpan.FromMinutes(29);
        if (renew <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(renewEvery));
        }

        EnterBusy();
        ProtocolConnection conn = _raw.Connection;
        TimeSpan oldTimeout = conn.ReadTimeout;
        conn.ReadTimeout = renew + TimeSpan.FromMinutes(1);
        var queue = new List<ImapEvent>();
        _collector = queue;
        string? tag = null;
        int doneSent = 1; // 1 = nothing to end
        bool bye = false;

        void SendDone()
        {
            // Called from the token callback or the renewal timer while the loop below is blocked reading: only the writer is touched.
            if (Interlocked.CompareExchange(ref doneSent, 1, 0) == 0)
            {
                conn.Writer.WriteAscii("DONE");
                conn.Writer.WriteCrLf();
                conn.Writer.Flush();
            }
        }

        try
        {
            while (!stop.IsCancellationRequested && !bye)
            {
                tag = await _raw.IdleStartAsync(CancellationToken.None).ConfigureAwait(false);
                if (tag is null)
                {
                    ImapResponse refused = await _raw.ReadResponseAsync(null, CancellationToken.None).ConfigureAwait(false);
                    throw new ImapCommandException(refused);
                }

                Volatile.Write(ref doneSent, 0);
                using CancellationTokenRegistration reg = stop.Register(SendDone);
                using var timer = new Timer(_ => SendDone(), null, renew, Timeout.InfiniteTimeSpan);
                while (true)
                {
                    ImapResponse r;
                    try
                    {
                        r = await _raw.ReadResponseAsync(null, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (MailConnectionClosedException) when (bye)
                    {
                        tag = null;
                        break;
                    }

                    if (r.Kind == ImapResponseKind.Tagged && r.Tag == tag)
                    {
                        tag = null;
                        Volatile.Write(ref doneSent, 1);
                        if (r.Code is { } code)
                        {
                            ObserveCode(code);
                        }

                        break;
                    }

                    Observe(r);
                    foreach (ImapEvent e in queue.ToArray())
                    {
                        bye |= e.Kind == ImapEventKind.Bye;
                    }

                    if (queue.Count > 0)
                    {
                        ImapEvent[] batch = [.. queue];
                        queue.Clear();
                        foreach (ImapEvent e in batch)
                        {
                            yield return e;
                        }
                    }
                }
            }
        }
        finally
        {
            _collector = null;
            conn.ReadTimeout = oldTimeout;
            if (tag is not null && !bye)
            {
                // The consumer stopped (or something threw) while idling: end IDLE properly so the connection is reusable.
                try
                {
                    Volatile.Write(ref doneSent, 0);
                    SendDone();
                    while (true)
                    {
                        ImapResponse r = await _raw.ReadResponseAsync(null, CancellationToken.None).ConfigureAwait(false);
                        if (r.Kind == ImapResponseKind.Tagged && r.Tag == tag)
                        {
                            break;
                        }

                        Observe(r);
                    }
                }
                catch (IOException)
                {
                    // The connection is gone; the next command will report it.
                }
            }

            _busy = false;
        }
    }
}
