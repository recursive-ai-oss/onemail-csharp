namespace RecursiveAI.OneMail.Stores.Mbox;

/// <summary>Recognises envelope ("From_") lines.</summary>
internal static class MboxFromLine
{
    private static ReadOnlySpan<byte> Months => "janfebmaraprmayjunjulaugsepoctnovdec"u8;

    /// <summary>
    /// Whether a line (without its line ending) reads as an envelope line, RFC 4155 §3: <c>From</c>, a space, the sender,
    /// a space and a date. Real files vary in the date's form (<c>Mon Jan  1 00:00:00 2024</c>, <c>Mon Jan 01 00:00:00 +0000 2024</c>,
    /// <c>Mon, 1 Jan 2024 00:00:00</c>), so the test is: a month name with a one or two digit day next to it, and a
    /// time containing a colon after it.
    /// </summary>
    public static bool IsEnvelope(ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (!line.StartsWith("From "u8))
        {
            return false;
        }

        ReadOnlySpan<byte> rest = line[5..];

        // Split into at most 24 space/tab separated tokens: start and length of each.
        Span<int> starts = stackalloc int[24];
        Span<int> lens = stackalloc int[24];
        int n = 0;
        int i = 0;
        while (i < rest.Length && n < 24)
        {
            while (i < rest.Length && (rest[i] == (byte)' ' || rest[i] == (byte)'\t'))
            {
                i++;
            }

            if (i >= rest.Length)
            {
                break;
            }

            int s = i;
            while (i < rest.Length && rest[i] != (byte)' ' && rest[i] != (byte)'\t')
            {
                i++;
            }

            starts[n] = s;
            lens[n] = i - s;
            n++;
        }

        // Token 0 is the sender; the date follows it.
        for (int j = 1; j < n; j++)
        {
            if (!IsMonth(rest.Slice(starts[j], lens[j])))
            {
                continue;
            }

            bool dayNext = j + 1 < n && IsDay(rest.Slice(starts[j + 1], lens[j + 1]));
            bool dayPrev = j - 1 >= 1 && IsDay(rest.Slice(starts[j - 1], lens[j - 1]));
            if (!dayNext && !dayPrev)
            {
                continue;
            }

            for (int k = j + 1; k < n; k++)
            {
                ReadOnlySpan<byte> t = rest.Slice(starts[k], lens[k]);
                if (t.Length >= 3 && t[0] is >= (byte)'0' and <= (byte)'9' && t.IndexOf((byte)':') > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsMonth(ReadOnlySpan<byte> t)
    {
        if (t.Length != 3)
        {
            return false;
        }

        Span<byte> lower = stackalloc byte[3];
        for (int i = 0; i < 3; i++)
        {
            byte b = t[i];
            lower[i] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
        }

        for (int m = 0; m < 12; m++)
        {
            if (lower.SequenceEqual(Months.Slice(m * 3, 3)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDay(ReadOnlySpan<byte> t)
    {
        if (t.Length is < 1 or > 2)
        {
            return false;
        }

        foreach (byte b in t)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Applies or removes mbox quoting on a stream of bytes, line by line. It holds at most six bytes between calls, so
/// it works on chunks of any size.
/// </summary>
/// <remarks>
/// Quoting: mboxrd adds one <c>&gt;</c> to a line made of zero or more <c>&gt;</c> then <c>From </c>; mboxo (and mboxcl)
/// adds it only to a line starting with <c>From </c>. Unquoting is the inverse: mboxrd removes one <c>&gt;</c> from a
/// <c>&gt;+From </c> line; mboxo and mboxcl remove it only when exactly one <c>&gt;</c> precedes <c>From </c>.
/// </remarks>
internal struct MboxQuoter
{
    private static ReadOnlySpan<byte> FromText => "From "u8;

    private readonly bool _quote;
    private readonly bool _reversible; // mboxrd: any number of '>' counts
    private int _gt;        // '>' seen at the start of this line (unquote: all but the held one were already written)
    private int _from;      // bytes of "From " matched at the line start
    private bool _inLine;   // the line start decided: nothing to do for the rest of the line

    public MboxQuoter(bool quote, bool reversible)
    {
        _quote = quote;
        _reversible = reversible;
    }

    /// <summary>The most bytes <see cref="Process"/> can write for <paramref name="inputLength"/> input bytes.</summary>
    public static int MaxOutput(int inputLength) => inputLength + (inputLength / 5) + 16;

    /// <summary>Transforms a chunk. <paramref name="output"/> must hold at least <see cref="MaxOutput"/> bytes.</summary>
    public int Process(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int o = 0;
        int i = 0;
        while (i < input.Length)
        {
            if (_inLine)
            {
                // Copy through the end of the line in one go.
                int nl = input[i..].IndexOf((byte)'\n');
                int take = nl < 0 ? input.Length - i : nl + 1;
                input.Slice(i, take).CopyTo(output[o..]);
                o += take;
                i += take;
                if (nl >= 0)
                {
                    Reset();
                }

                continue;
            }

            byte b = input[i++];
            if (_quote)
            {
                o = StepQuote(b, output, o);
            }
            else
            {
                o = StepUnquote(b, output, o);
            }
        }

        return o;
    }

    /// <summary>Writes out bytes held back at the end of the input.</summary>
    public int Finish(Span<byte> output)
    {
        int o = 0;
        if (!_quote && _gt > 0)
        {
            output[o++] = (byte)'>';
        }

        FromText[.._from].CopyTo(output[o..]);
        o += _from;
        Reset();
        return o;
    }

    private void Reset()
    {
        _gt = 0;
        _from = 0;
        _inLine = false;
    }

    private int StepQuote(byte b, Span<byte> output, int o)
    {
        if (_from == 0 && b == (byte)'>' && _reversible)
        {
            output[o++] = b;
            _gt++;
            return o;
        }

        if (b == FromText[_from])
        {
            _from++;
            if (_from == FromText.Length)
            {
                output[o++] = (byte)'>';
                FromText.CopyTo(output[o..]);
                o += FromText.Length;
                _from = 0;
                _inLine = true;
            }

            return o;
        }

        // Not an envelope-like line: release what was held and carry on.
        FromText[.._from].CopyTo(output[o..]);
        o += _from;
        _from = 0;
        output[o++] = b;
        if (b == (byte)'\n')
        {
            Reset();
        }
        else
        {
            _inLine = true;
        }

        return o;
    }

    private int StepUnquote(byte b, Span<byte> output, int o)
    {
        if (_from == 0 && b == (byte)'>' && (_reversible || _gt == 0))
        {
            // Hold one '>' back: it is dropped if "From " follows the run, kept otherwise.
            if (_gt > 0)
            {
                output[o++] = (byte)'>';
            }

            _gt++;
            return o;
        }

        if (b == FromText[_from])
        {
            _from++;
            if (_from == FromText.Length)
            {
                // Matched: drop the held '>' (if any) and write the text.
                FromText.CopyTo(output[o..]);
                o += FromText.Length;
                _from = 0;
                _gt = 0;
                _inLine = true;
            }

            return o;
        }

        // Mismatch: release the held '>' and matched prefix.
        if (_gt > 0)
        {
            output[o++] = (byte)'>';
        }

        FromText[.._from].CopyTo(output[o..]);
        o += _from;
        _from = 0;
        _gt = 0;
        output[o++] = b;
        if (b == (byte)'\n')
        {
            Reset();
        }
        else
        {
            _inLine = true;
        }

        return o;
    }
}
