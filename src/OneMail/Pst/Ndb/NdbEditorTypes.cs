using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMail.Sources;

namespace RecursiveAI.OneMail.Pst.Ndb;

/// <summary>
/// What a caller accepts when a file looks suspicious (<see cref="NdbEditorOptions.Allow"/>). Each flag lets one class of finding through; a file
/// that is not a Unicode PST at all (ANSI, OST, an unknown version, protected content) is never opened for writing, whatever is allowed.
/// </summary>
[Flags]
public enum NdbOpenRisks
{
    /// <summary>Nothing: any suspicious finding refuses the file.</summary>
    None = 0,

    /// <summary>The header's partial or full CRC or its sentinel is wrong.</summary>
    HeaderChecksum = 1,

    /// <summary>
    /// The allocation maps are marked invalid (a writer was stopped part-way, as the file's header says), or a map page fails its checks. The editor
    /// rebuilds every map from the two B-trees before its first commit (MS-PST 2.6.1.3.7).
    /// </summary>
    AllocationMaps = 2,

    /// <summary>The density list is damaged. It is rebuilt from the maps.</summary>
    DensityList = 4,

    /// <summary>The node database reports defects beyond the ones above (a bad B-tree page, a bad block). Edits may build on damage.</summary>
    Damage = 8,
}

/// <summary>Why a file was refused for writing.</summary>
public enum NdbRefusalReason
{
    /// <summary>Not a PST file at all (no <c>!BDN</c> header).</summary>
    NotAPst,

    /// <summary>An ANSI (wVer 14 or 15) file. These are read-only.</summary>
    Ansi,

    /// <summary>An offline store (OST). These are read-only.</summary>
    OfflineStore,

    /// <summary>A file with 4 KiB pages (wVer 36 and later). These are read-only.</summary>
    LargePages,

    /// <summary>A version other than the Unicode 512-byte-page versions 21 and 23.</summary>
    UnsupportedVersion,

    /// <summary>Blocks are protected with Windows Information Protection, or the encryption method is not defined.</summary>
    ProtectedContent,

    /// <summary>The header checksums or sentinel are wrong. Allowed by <see cref="NdbOpenRisks.HeaderChecksum"/>.</summary>
    HeaderChecksum,

    /// <summary>The allocation maps are invalid or fail their checks. Allowed by <see cref="NdbOpenRisks.AllocationMaps"/>.</summary>
    AllocationMaps,

    /// <summary>The density list is damaged. Allowed by <see cref="NdbOpenRisks.DensityList"/>.</summary>
    DensityList,

    /// <summary>The node database reports other defects. Allowed by <see cref="NdbOpenRisks.Damage"/>.</summary>
    Damage,
}

/// <summary>One finding that stopped a file from being opened for writing.</summary>
/// <param name="Reason">The class of finding.</param>
/// <param name="Detail">What was found, with file offsets.</param>
/// <param name="CanAllow">True when the caller can open the file anyway with the matching <see cref="NdbOpenRisks"/> flag.</param>
public readonly record struct NdbRefusal(NdbRefusalReason Reason, string Detail, bool CanAllow);

/// <summary>Thrown by <see cref="NdbEditor.Open(string, NdbEditorOptions?)"/> when a file is refused for writing.</summary>
public sealed class NdbEditRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="refusals">The findings.</param>
    public NdbEditRefusedException(IReadOnlyList<NdbRefusal> refusals)
        : base(Describe(refusals))
    {
        Refusals = refusals;
    }

    /// <summary>Every finding that applies, not only the first.</summary>
    public IReadOnlyList<NdbRefusal> Refusals { get; }

    private static string Describe(IReadOnlyList<NdbRefusal> refusals)
    {
        if (refusals.Count == 0)
        {
            return "the file was refused for writing";
        }

        string more = refusals.Count > 1 ? $" (and {refusals.Count - 1} more)" : string.Empty;
        return $"the file was refused for writing: {refusals[0].Reason}: {refusals[0].Detail}{more}";
    }
}

/// <summary>Options for <see cref="NdbEditor"/>.</summary>
public sealed class NdbEditorOptions
{
    /// <summary>The kinds of suspicious finding to accept. The default accepts none.</summary>
    public NdbOpenRisks Allow { get; init; } = NdbOpenRisks.None;

    /// <summary>
    /// True (the default) to overwrite every freed block and page with zeros after the commit that freed it, so deleted content does not stay in the file.
    /// False leaves freed space as it was.
    /// </summary>
    public bool ZeroFreedSpace { get; init; } = true;

    /// <summary>
    /// How many defects a file may report on opening before it counts as damaged (see <see cref="NdbOpenRisks.Damage"/>). Defects that only say the
    /// maps or the header are suspicious are classified separately. The default is 0.
    /// </summary>
    public int DefectThreshold { get; init; }

    /// <summary>Limits for the node database reader the editor uses; null for the defaults.</summary>
    public NdbOptions? Ndb { get; init; }
}

/// <summary>
/// The file the editor reads and writes: positioned reads and writes, a barrier that makes everything written so far durable, and a length. The
/// editor's commit protocol is written in terms of these barriers, so a wrapper that records them can replay any crash.
/// </summary>
internal abstract class EditorIo : IRandomAccessSource
{
    public abstract long Length { get; }

    public bool SupportsSyncRead => true;

    public abstract int Read(long offset, Span<byte> buffer);

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(offset, buffer.Span));

    public abstract void Write(long offset, ReadOnlySpan<byte> data);

    /// <summary>Makes every write so far durable before returning. Writes after this call may reach the disk in any order until the next barrier.</summary>
    public abstract void Barrier();

    public abstract void SetLength(long length);

    public abstract void Dispose();
}

/// <summary>An <see cref="EditorIo"/> over a seekable, readable, writable stream: a file opened for exclusive use, or memory.</summary>
internal sealed class StreamEditorIo(Stream stream, bool leaveOpen) : EditorIo
{
    public override long Length => stream.Length;

    public override int Read(long offset, Span<byte> buffer)
    {
        // An offset taken from a header or tree entry can be anything, a 64-bit value above the signed range included; a read there finds nothing.
        if (offset < 0 || offset >= stream.Length)
        {
            return 0;
        }

        stream.Position = offset;
        return stream.Read(buffer);
    }

    public override void Write(long offset, ReadOnlySpan<byte> data)
    {
        stream.Position = offset;
        stream.Write(data);
    }

    public override void Barrier()
    {
        if (stream is FileStream fs)
        {
            fs.Flush(flushToDisk: true);
        }
        else
        {
            stream.Flush();
        }
    }

    public override void SetLength(long length) => stream.SetLength(length);

    public override void Dispose()
    {
        if (!leaveOpen)
        {
            stream.Dispose();
        }
    }
}
