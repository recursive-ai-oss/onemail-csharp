using System.Buffers.Binary;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Tnef;

/// <summary>
/// A TNEF stream read down to its attributes (MS-OXTNEF 2.1.3.2): the signature and legacy key, then a list of attributes whose data stays in the source
/// until asked for. Nothing here interprets attributes; <see cref="TnefMessage"/> does that. Writing the document back with <see cref="WriteTo"/> reproduces
/// the input byte for byte, damaged streams included.
/// </summary>
/// <remarks>The document keeps a reference to its source; the caller keeps the source open for as long as attribute data is needed. Reading never throws on malformed content.</remarks>
public sealed class TnefDocument
{
    private readonly List<TnefAttributeRecord> _attributes = [];
    private readonly List<TnefDefect> _defects = [];
    private bool _headerCut;

    private TnefDocument(IRandomAccessSource source)
    {
        Source = source;
    }

    /// <summary>The source the document was read from.</summary>
    public IRandomAccessSource Source { get; }

    /// <summary>Whether the stream starts with the TNEF signature. When false there are no attributes.</summary>
    public bool HasSignature { get; private set; }

    /// <summary>The 16-bit legacy key after the signature (MS-OXTNEF 2.1.3.2: any number will suffice).</summary>
    public ushort LegacyKey { get; private set; }

    /// <summary>The attributes in stream order, including a final truncated one when the stream is cut inside it.</summary>
    public IReadOnlyList<TnefAttributeRecord> Attributes => _attributes;

    /// <summary>Where the bytes that follow the last attribute start (the end of the stream when there are none).</summary>
    public long TrailingOffset { get; private set; }

    /// <summary>How many bytes follow the last attribute: a partial attribute header, or everything after an attribute over the size limit.</summary>
    public long TrailingLength { get; private set; }

    /// <summary>What was wrong with the attribute structure.</summary>
    public IReadOnlyList<TnefDefect> Defects => _defects;

    /// <summary>Defects beyond <see cref="TnefReaderOptions.MaxDefects"/>, which are counted and not kept.</summary>
    public int DroppedDefects { get; private set; }

    /// <summary>Reads the attribute list of a stream.</summary>
    /// <param name="source">The bytes. The document reads attribute data from it later.</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>The document. Never null and never throws for malformed content; a stream without the signature has <see cref="HasSignature"/> false.</returns>
    public static TnefDocument Read(IRandomAccessSource source, TnefReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var doc = new TnefDocument(source);
        doc.Parse(options ?? new TnefReaderOptions());
        return doc;
    }

    /// <summary>Reads the attribute list of bytes in memory.</summary>
    /// <param name="data">The stream; it must stay unchanged while the document is used.</param>
    /// <param name="options">Limits, or null for the defaults.</param>
    /// <returns>The document.</returns>
    public static TnefDocument Read(ReadOnlyMemory<byte> data, TnefReaderOptions? options = null) => Read(new MemorySource(data), options);

    /// <summary>Whether the first bytes are the TNEF signature.</summary>
    /// <param name="head">At least the first 4 bytes of a candidate.</param>
    /// <returns>True when they are 78 9F 3E 22.</returns>
    public static bool HasTnefSignature(ReadOnlySpan<byte> head) => head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == TnefWriter.Signature;

    internal void AddDefect(TnefDefect d, int max)
    {
        if (_defects.Count < max)
        {
            _defects.Add(d);
        }
        else
        {
            DroppedDefects++;
        }
    }

    private void Parse(TnefReaderOptions o)
    {
        long length = Source.Length;
        Span<byte> head = stackalloc byte[6];
        int n = Source.ReadFully(0, head);
        if (n < 4 || !HasTnefSignature(head))
        {
            AddDefect(new TnefDefect(TnefDefectKind.NotTnef, 0, length == 0 ? "empty input" : "no TNEF signature"), o.MaxDefects);
            TrailingOffset = 0;
            TrailingLength = length;
            return;
        }

        HasSignature = true;
        if (n < 6)
        {
            AddDefect(new TnefDefect(TnefDefectKind.TrailingBytes, 4, "the legacy key is cut off"), o.MaxDefects);
            _headerCut = true;
            TrailingOffset = 0;
            TrailingLength = length;
            return;
        }

        LegacyKey = BinaryPrimitives.ReadUInt16LittleEndian(head[4..]);
        long pos = 6;
        Span<byte> h = stackalloc byte[9];
        Span<byte> cs = stackalloc byte[2];
        while (pos < length)
        {
            long remaining = length - pos;
            if (remaining < 9)
            {
                AddDefect(new TnefDefect(TnefDefectKind.TrailingBytes, pos, remaining + " bytes after the last attribute are too few for an attribute header"), o.MaxDefects);
                break;
            }

            if (_attributes.Count >= o.MaxAttributes)
            {
                AddDefect(new TnefDefect(TnefDefectKind.LimitReached, pos, "more than " + o.MaxAttributes + " attributes"), o.MaxDefects);
                break;
            }

            Source.ReadFully(pos, h);
            byte level = h[0];
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(h[1..]);
            long len = BinaryPrimitives.ReadUInt32LittleEndian(h[5..]);
            if (len > o.MaxAttributeBytes)
            {
                AddDefect(new TnefDefect(TnefDefectKind.LimitReached, pos, "attribute 0x" + id.ToString("X8") + " declares " + len + " bytes, over the limit of " + o.MaxAttributeBytes), o.MaxDefects);
                break;
            }

            long dataStart = pos + 9;
            if (level is not (1 or 2))
            {
                AddDefect(new TnefDefect(TnefDefectKind.InvalidLevel, pos, "level byte " + level), o.MaxDefects);
            }

            if (dataStart + len + 2 > length)
            {
                // MS-OXTNEF has no rule for a cut stream; keep what is there so the message can still be read as far as it goes.
                long avail = Math.Max(0, Math.Min(len, length - dataStart));
                AddDefect(new TnefDefect(TnefDefectKind.AttributeTruncated, pos, "attribute 0x" + id.ToString("X8") + " declares " + len + " data bytes and ends after " + avail), o.MaxDefects);
                _attributes.Add(new TnefAttributeRecord(Source, pos, level, id, len, avail, 0, null, true, length - pos));
                pos = length;
                break;
            }

            Source.ReadFully(dataStart + len, cs);
            ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(cs);
            ushort? computed = o.VerifyChecksums ? TnefAttributeRecord.Sum(Source, dataStart, len) : null;
            if (computed is { } c && c != stored)
            {
                // MS-OXTNEF 2.3.3.4: some legacy writers produced bad checksums on message class attributes, so readers ignore those.
                bool classAttr = TnefAttributeIds.Same(id, TnefAttributeIds.MessageClass) || TnefAttributeIds.Same(id, TnefAttributeIds.OriginalMessageClass);
                AddDefect(new TnefDefect(classAttr ? TnefDefectKind.AmbiguousMessageClassChecksum : TnefDefectKind.ChecksumMismatch, dataStart + len,
                    "stored 0x" + stored.ToString("X4") + ", computed 0x" + c.ToString("X4") + " for attribute 0x" + id.ToString("X8")), o.MaxDefects);
            }

            _attributes.Add(new TnefAttributeRecord(Source, pos, level, id, len, len, stored, computed, false, 9 + len + 2));
            pos = dataStart + len + 2;
        }

        TrailingOffset = pos;
        TrailingLength = length - pos;
    }

    /// <summary>Writes the document back through a <see cref="TnefWriter"/>: the signature and key, every attribute with its stored checksum, then the trailing bytes.</summary>
    /// <param name="output">Where the bytes go.</param>
    /// <exception cref="InvalidOperationException">The input had no TNEF signature, so there is nothing to write.</exception>
    public void WriteTo(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!HasSignature)
        {
            throw new InvalidOperationException("The input has no TNEF signature.");
        }

        if (_headerCut)
        {
            // The input ends inside the legacy key; there is no attribute to write, only the bytes.
            var raw = new byte[TrailingLength];
            Source.ReadFully(0, raw);
            output.Write(raw);
            return;
        }

        var w = new TnefWriter(output, LegacyKey);
        foreach (TnefAttributeRecord a in _attributes)
        {
            if (a.IsTruncated)
            {
                CopyRaw(w, a.Offset, a.RawLength);
            }
            else
            {
                using Stream data = a.OpenData();
                w.WriteAttribute(a.LevelValue, a.Id, data, a.Length, a.StoredChecksum);
            }
        }

        CopyRaw(w, TrailingOffset, TrailingLength);
    }

    private void CopyRaw(TnefWriter w, long offset, long count)
    {
        byte[] buf = new byte[(int)Math.Min(count, 16 * 1024)];
        long done = 0;
        while (done < count)
        {
            int want = (int)Math.Min(buf.Length, count - done);
            int n = Source.ReadFully(offset + done, buf.AsSpan(0, want));
            if (n <= 0)
            {
                break;
            }

            w.WriteRaw(buf.AsSpan(0, n));
            done += n;
        }
    }
}
