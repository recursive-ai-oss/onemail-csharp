using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Recovery;

namespace RecursiveAI.OneMail.Pst.Recovery;

/// <summary>What <see cref="PstRecovery.RepairTo(string, string?, CancellationToken)"/> wrote.</summary>
/// <param name="Written">Items added to the new file.</param>
/// <param name="Failed">Items that could not be copied (the report says why); a failed item may have left unreferenced blocks in the new file.</param>
/// <param name="ByStatus">Written items per tag, indexed by <see cref="RecoveryStatus"/>.</param>
/// <param name="File">The new file's size and counts.</param>
public sealed record PstRepairSummary(int Written, int Failed, IReadOnlyList<int> ByStatus, PstFileSummary File);

public sealed partial class PstRecovery
{
    /// <summary>The property set the repaired file's provenance properties live in.</summary>
    public static readonly Guid ProvenancePropertySet = new("6f1c2f4e-0a39-4bd1-9d5e-6a1e3d9c5b01");

    private const uint NidIpmSubtree = 0x8022;

    private static readonly MapiNamedProperty StatusName = new(ProvenancePropertySet, "OneMailRecoveryStatus");
    private static readonly MapiNamedProperty SourceName = new(ProvenancePropertySet, "OneMailRecoverySource");
    private static readonly MapiNamedProperty ReasonName = new(ProvenancePropertySet, "OneMailRecoveryReason");

    /// <summary>
    /// Writes every recoverable item to a new Unicode PST through <see cref="PstBuilder"/> and, optionally, a JSON report beside it. Items go in their
    /// folders; deleted, stale, superseded and free-space items (with <see cref="PstRecoveryOptions.Forensic"/>) go under
    /// <c>Recovered/&lt;tag&gt;</c> in the top of the store. Each item carries three properties (named, in <see cref="ProvenancePropertySet"/>):
    /// <c>OneMailRecoveryStatus</c>, <c>OneMailRecoverySource</c> (<c>nid=…;bid=…;ranges=offset+length,…;lost=…</c>) and <c>OneMailRecoveryReason</c>.
    /// Folder properties other than the name and container class are not carried over. The source is never modified.
    /// </summary>
    /// <param name="path">The new PST; created or overwritten.</param>
    /// <param name="reportPath">Where to write the JSON report; null for none.</param>
    /// <param name="cancellationToken">Cancels the repair; the new file is then incomplete and should be deleted.</param>
    /// <returns>Counts.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is the file being recovered.</exception>
    /// <exception cref="IOException">The output cannot be written.</exception>
    public PstRepairSummary RepairTo(string path, string? reportPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (SamePath(path))
        {
            throw new ArgumentException("recovery never writes to the file it reads", nameof(path));
        }

        using var report = reportPath is null ? null : new FileStream(reportPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        return RepairTo(output, report, cancellationToken);
    }

    private bool SamePath(string path) => SourcePath is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.Ordinal);

    internal string? SourcePath { get; set; }

    /// <summary>Writes the repaired PST to a seekable stream; see <see cref="RepairTo(string, string?, CancellationToken)"/>.</summary>
    /// <param name="output">A seekable, writable stream positioned at 0; left open.</param>
    /// <param name="report">The stream for the JSON report, or null; left open.</param>
    /// <param name="cancellationToken">Cancels the repair.</param>
    /// <returns>Counts.</returns>
    public PstRepairSummary RepairTo(Stream output, Stream? report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var counts = new int[5];
        int written = 0, failed = 0;
        using var ms = report is null ? null : new MemoryStream();
        Utf8JsonWriter? w = ms is null ? null : new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
        w?.WriteStartObject();
        WriteHeader(w);
        w?.WriteStartArray("items");

        PstFileSummary file;
        string display = _store!.DisplayName ?? "Recovered";
        using (PstBuilder b = PstBuilder.Create(output, leaveOpen: true, new PstBuilderOptions { DisplayName = display }))
        {
            var ipm = b.IpmSubtree;
            string? prefix = _entries.ContainsKey(NidIpmSubtree) ? FolderOf(NidIpmSubtree).Path : null;
            foreach (PstRecoveredItem item in EnumerateItems(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecoveryProvenance p = item.Provenance;
                string target = string.Empty;
                string? error = null;
                var dropped = new List<string>();
                uint newNid = 0;
                try
                {
                    PstFolderWriter folder = TargetFolder(ipm, prefix, item, out target);
                    if (item.Provenance.Status != RecoveryStatus.Live)
                    {
                        Salvage(item.Message, "message", dropped);
                    }

                    AddProvenance(item);
                    newNid = folder.AddMessage(item.Message, item.Associated).Value;
                    written++;
                    counts[(int)p.Status]++;
                }
                catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or ArgumentException or NotSupportedException or OverflowException)
                {
                    failed++;
                    error = e.GetType().Name + ": " + e.Message + " @ " + string.Join(" <- ", (e.StackTrace ?? "").Split((char)10).Take(6).Select(x => x.Trim()));
                }

                if (w is not null)
                {
                    WriteItem(w, item, target, newNid, error, dropped);
                }
            }

            file = b.Complete();
        }

        w?.WriteEndArray();
        if (w is not null)
        {
            w.WriteNumber("written", written);
            w.WriteNumber("failed", failed);
            w.WriteStartObject("byStatus");
            for (int i = 0; i < counts.Length; i++)
            {
                w.WriteNumber(((RecoveryStatus)i).ToString(), counts[i]);
            }

            w.WriteEndObject();
            w.WriteEndObject();
            w.Flush();
            ms!.Position = 0;
            ms.CopyTo(report!);
        }

        return new PstRepairSummary(written, failed, counts, file);
    }

    private PstFolderWriter TargetFolder(PstFolderWriter ipm, string? prefix, PstRecoveredItem item, out string target)
    {
        string source = item.FolderPath;
        string rel;
        bool inIpm;
        if (prefix is not null && source == prefix)
        {
            rel = string.Empty;
            inIpm = true;
        }
        else if (prefix is not null && source.StartsWith(prefix + "/", StringComparison.Ordinal))
        {
            rel = source[(prefix.Length + 1)..];
            inIpm = true;
        }
        else
        {
            rel = source;
            inIpm = false;
        }

        string path;
        if (item.PlaceInFolder && inIpm)
        {
            path = rel;
        }
        else
        {
            string tag = item.Provenance.Status.ToString();
            path = "Recovered/" + tag + (rel.Length > 0 ? "/" + rel : string.Empty);
        }

        target = path;
        PstFolderWriter f = ipm;
        string cumulative = string.Empty;
        foreach (string part in path.Length == 0 ? [] : path.Split('/'))
        {
            cumulative = cumulative.Length == 0 ? part : cumulative + "/" + part;
            string? cls = "IPF.Note";
            if (item.PlaceInFolder && inIpm && FindClass(prefix!, cumulative) is { } c)
            {
                cls = c;
            }

            f = f.GetOrCreateFolder(part, cls);
        }

        return f;
    }

    private string? FindClass(string prefix, string rel)
    {
        string full = prefix + "/" + rel;
        foreach (KeyValuePair<uint, FolderFact> kv in _folders)
        {
            if (kv.Value.Path == full)
            {
                return kv.Value.ContainerClass;
            }
        }

        foreach (KeyValuePair<uint, FolderFact> kv in _unreachedFolders)
        {
            if (kv.Value.Path == full)
            {
                return kv.Value.ContainerClass;
            }
        }

        return null;
    }

    private static string RangesText(IReadOnlyList<ByteRange> ranges)
    {
        var sb = new StringBuilder();
        foreach (ByteRange r in ranges)
        {
            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(r.Offset.ToString(CultureInfo.InvariantCulture)).Append('+').Append(r.Length.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    // A value whose blocks are gone cannot be copied. Reading each value of a damaged item first drops the ones that fail, so the rest of the item is
    // written instead of the whole item being lost; what was dropped goes in the report.
    private static void Salvage(MapiObject o, string where, List<string> dropped)
    {
        byte[] buffer = new byte[64 * 1024];
        foreach (MapiProperty p in o.Properties.ToList())
        {
            try
            {
                for (int i = 0; i < p.ValueCount; i++)
                {
                    // The builder streams large values, so a value is read the same way here.
                    // A value that comes up shorter than its declared length is cut by a missing block; the builder would refuse it.
                    using Stream stream = p.OpenRead(i);
                    long read = 0;
                    int got;
                    while ((got = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        read += got;
                    }

                    if (read != p.GetByteLength(i))
                    {
                        throw new EndOfStreamException("the value is " + read + " bytes of " + p.GetByteLength(i));
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
            {
                o.Properties.Remove(p.Tag);
                dropped.Add(where + " " + p.Tag + " (" + e.GetType().Name + ")");
            }
        }

        if (o is MapiMessage m)
        {
            int n = 0;
            foreach (MapiRecipient r in m.Recipients)
            {
                Salvage(r, where + " recipient " + n++, dropped);
            }

            n = 0;
            foreach (MapiAttachment a in m.Attachments)
            {
                Salvage(a, where + " attachment " + n, dropped);
                if (a.EmbeddedMessage is { } em)
                {
                    Salvage(em, where + " attachment " + n + " message", dropped);
                }

                n++;
            }
        }
    }

    private static void AddProvenance(PstRecoveredItem item)
    {
        RecoveryProvenance p = item.Provenance;
        var source = new StringBuilder();
        if (p.Nid is { } nid)
        {
            source.Append("nid=0x").Append(nid.ToString("x", CultureInfo.InvariantCulture)).Append(';');
        }

        if (p.Bid is { } bid)
        {
            source.Append("bid=0x").Append(bid.ToString("x", CultureInfo.InvariantCulture)).Append(';');
        }

        source.Append("ranges=").Append(RangesText(p.Ranges)).Append(";lost=").Append(RangesText(p.Lost));
        MapiPropertyBag bag = item.Message.Properties;
        bag.Set(MapiProperty.Create(new MapiTag(0x8000, MapiType.UnicodeString), Encoding.Unicode.GetBytes(p.Status.ToString()), StatusName));
        bag.Set(MapiProperty.Create(new MapiTag(0x8001, MapiType.UnicodeString), Encoding.Unicode.GetBytes(source.ToString()), SourceName));
        bag.Set(MapiProperty.Create(new MapiTag(0x8002, MapiType.UnicodeString), Encoding.Unicode.GetBytes(p.Reason ?? string.Empty), ReasonName));
    }

    private void WriteHeader(Utf8JsonWriter? w)
    {
        if (w is null)
        {
            return;
        }

        w.WriteString("format", _file.Format.ToString());
        w.WriteString("encoding", _file.Header.Encryption.ToString());
        w.WriteBoolean("headerUsable", _headerUsable);
        w.WriteBoolean("indexRebuilt", IndexRebuilt);
        w.WriteBoolean("forensic", _options.Forensic);
        w.WriteStartObject("scan");
        w.WriteNumber("pages", _stats.Pages);
        w.WriteNumber("blocks", _stats.Blocks);
        w.WriteNumber("damagedBlocks", _stats.DamagedBlocks);
        w.WriteNumber("damagedPages", _stats.DamagedPages);
        w.WriteNumber("nodes", _stats.Nodes);
        w.WriteNumber("reachedNodes", _stats.ReachedNodes);
        w.WriteNumber("staleNodes", _stats.StaleNodes);
        w.WriteNumber("supersededVersions", _stats.SupersededVersions);
        w.WriteEndObject();
        w.WriteStartArray("defects");
        foreach (PstRecoveryDefect d in _defects)
        {
            w.WriteStartObject();
            w.WriteString("kind", d.Kind.ToString());
            w.WriteNumber("offset", d.Offset);
            w.WriteString("detail", d.Detail);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteItem(Utf8JsonWriter w, PstRecoveredItem item, string target, uint newNid, string? error, List<string> dropped)
    {
        RecoveryProvenance p = item.Provenance;
        w.WriteStartObject();
        w.WriteString("subject", item.Message.Subject);
        w.WriteString("status", p.Status.ToString());
        w.WriteString("sourceFolder", item.FolderPath);
        w.WriteString("targetFolder", target);
        if (p.Nid is { } nid)
        {
            w.WriteString("nid", "0x" + nid.ToString("x", CultureInfo.InvariantCulture));
        }

        if (p.Bid is { } bid)
        {
            w.WriteString("bid", "0x" + bid.ToString("x", CultureInfo.InvariantCulture));
        }

        if (newNid != 0)
        {
            w.WriteString("newNid", "0x" + newNid.ToString("x", CultureInfo.InvariantCulture));
        }

        w.WriteString("reason", p.Reason);
        WriteRanges(w, "ranges", p.Ranges);
        WriteRanges(w, "lost", p.Lost);
        w.WriteStartArray("defects");
        foreach (MailDefect d in p.Defects)
        {
            w.WriteStringValue(d.Code);
        }

        w.WriteEndArray();
        if (dropped.Count > 0)
        {
            w.WriteStartArray("droppedProperties");
            foreach (string d in dropped)
            {
                w.WriteStringValue(d);
            }

            w.WriteEndArray();
        }

        if (error is not null)
        {
            w.WriteString("error", error);
        }

        w.WriteEndObject();
    }

    private static void WriteRanges(Utf8JsonWriter w, string name, IReadOnlyList<ByteRange> ranges)
    {
        w.WriteStartArray(name);
        foreach (ByteRange r in ranges)
        {
            w.WriteStartArray();
            w.WriteNumberValue(r.Offset);
            w.WriteNumberValue(r.Length);
            w.WriteEndArray();
        }

        w.WriteEndArray();
    }
}
