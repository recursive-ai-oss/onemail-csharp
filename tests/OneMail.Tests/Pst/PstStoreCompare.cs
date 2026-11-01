using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>
/// Compares messages that were written into a PST with the messages they came from, property by property: tag, type, raw value, named-property
/// name, recipients and attachments in order, embedded messages. Test-side only.
/// </summary>
public static class PstStoreCompare
{
    // The properties MS-PST says every message (2.4.5.1.1) and attachment (2.4.6.2.1) must have; the builder adds them when the source lacks them.
    private static readonly HashSet<ushort> AddedOnMessages = [0x001A, 0x0E07, 0x0E08, 0x0E17, 0x3007, 0x3008, 0x300B];
    private static readonly HashSet<ushort> AddedOnAttachments = [0x0E20, 0x3705, 0x370B];

    public sealed class Result
    {
        public int Source { get; set; }
        public int Written { get; set; }
        public int Exact { get; set; }
        public int WithAdditions { get; set; }
        public int Unmatched { get; set; }
        public int PropertiesAdded { get; set; }
        public int PropertiesNotCarried { get; set; }
        public List<string> Notes { get; } = [];
    }

    private static string Key(MapiProperty p) =>
        p.Name is { } n ? $"N:{n.PropertySet}:{(n.IsString ? "s" + n.Name : "l" + n.Lid)}:{p.Tag.Type:x4}" : $"{p.Id:x4}:{p.Tag.Type:x4}";

    private static string ValueHash(MapiProperty p)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < p.ValueCount; i++)
        {
            sb.Append(Convert.ToHexString(SHA256.HashData(p.GetBytes(i).Span.ToArray()).AsSpan(0, 8))).Append(':').Append(p.GetByteLength(i)).Append(',');
        }

        return sb.ToString();
    }

    private static string ObjectSignature(MapiObject o)
    {
        var lines = o.Properties.Select(p => Key(p) + "=" + ValueHash(p)).Order(StringComparer.Ordinal);
        return string.Join(";", lines);
    }

    public static string Signature(MapiMessage m)
    {
        var sb = new StringBuilder(ObjectSignature(m));
        foreach (MapiRecipient r in m.Recipients)
        {
            sb.Append("|R:").Append(ObjectSignature(r));
        }

        foreach (MapiAttachment a in m.Attachments)
        {
            sb.Append("|A:").Append(ObjectSignature(a));
            if (a.EmbeddedMessage is { } e)
            {
                sb.Append("|E{").Append(Signature(e)).Append('}');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static bool Carried(MapiProperty p) =>
        !(p.Tag.IsNamed && p.Name is null) && p.Tag.Type != 0x000D && ((ushort)(p.Tag.Type & 0x0FFF)) is 2 or 3 or 4 or 5 or 6 or 7 or 0xA or 0xB or 0x14 or 0x1E or 0x1F or 0x40 or 0x48 or 0x102
        && p.Id is not (0x67F2 or 0x67F3);

    // Source properties must all be in the written object, equal; the written object may have extra properties only from the allowed set.
    private static bool ObjectEquivalent(MapiObject written, MapiObject source, HashSet<ushort> allowedAdditions, Result r, StringBuilder? why)
    {
        var w = new Dictionary<string, MapiProperty>(StringComparer.Ordinal);
        foreach (MapiProperty p in written.Properties)
        {
            w.TryAdd(Key(p), p);
        }

        int notCarried = 0;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapiProperty p in source.Properties)
        {
            string k = Key(p);
            if (!Carried(p))
            {
                notCarried++;
                continue;
            }

            if (!used.Add(k))
            {
                notCarried++;   // a repeated key: the first was carried
                continue;
            }

            if (!w.TryGetValue(k, out MapiProperty? wp))
            {
                // A property ID repeated with another type is one property in a PST property context; the first wins.
                if (source.Properties.Any(q => q != p && q.Id == p.Id && !p.Tag.IsNamed && Carried(q) && source.Properties.ToList().IndexOf(q) < source.Properties.ToList().IndexOf(p)))
                {
                    notCarried++;
                    continue;
                }

                why?.Append("missing ").Append(k).Append("; ");
                return false;
            }

            if (ValueHash(wp) != ValueHash(p))
            {
                why?.Append("different value ").Append(k).Append(" written ").Append(Convert.ToHexString(wp.GetBytes(0).Span[..Math.Min(24, wp.GetBytes(0).Length)])).Append(" source ").Append(Convert.ToHexString(p.GetBytes(0).Span[..Math.Min(24, p.GetBytes(0).Length)])).Append("; ");
                return false;
            }
        }

        int added = 0;
        foreach ((string k, MapiProperty p) in w)
        {
            if (!used.Contains(k))
            {
                if (!allowedAdditions.Contains(p.Id) || p.Tag.IsNamed)
                {
                    why?.Append("extra ").Append(k).Append("; ");
                    return false;
                }

                added++;
            }
        }

        r.PropertiesAdded += added;
        r.PropertiesNotCarried += notCarried;
        return true;
    }

    private static bool MessageEquivalent(MapiMessage w, MapiMessage s, Result r, StringBuilder? why)
    {
        if (!ObjectEquivalent(w, s, AddedOnMessages, r, why))
        {
            return false;
        }

        if (w.Recipients.Count != s.Recipients.Count || w.Attachments.Count != s.Attachments.Count)
        {
            why?.Append($"counts: recipients {w.Recipients.Count}/{s.Recipients.Count} attachments {w.Attachments.Count}/{s.Attachments.Count}; ");
            return false;
        }

        for (int i = 0; i < w.Recipients.Count; i++)
        {
            if (!ObjectEquivalent(w.Recipients[i], s.Recipients[i], [], r, why))
            {
                why?.Append("recipient ").Append(i).Append("; ");
                return false;
            }
        }

        for (int i = 0; i < w.Attachments.Count; i++)
        {
            MapiAttachment wa = w.Attachments[i], sa = s.Attachments[i];
            // The object property of an embedded message is rewritten with the new file's subnode NID, so it is not compared as bytes.
            if (!ObjectEquivalentIgnoringObject(wa, sa, r, why))
            {
                why?.Append("attachment ").Append(i).Append("; ");
                return false;
            }

            if ((wa.EmbeddedMessage is null) != (sa.EmbeddedMessage is null))
            {
                why?.Append("embedded presence ").Append(i).Append("; ");
                return false;
            }

            if (wa.EmbeddedMessage is { } we && sa.EmbeddedMessage is { } se && !MessageEquivalent(we, se, r, why))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ObjectEquivalentIgnoringObject(MapiAttachment w, MapiAttachment s, Result r, StringBuilder? why)
    {
        var wc = new Detached(w);
        var sc = new Detached(s);
        return ObjectEquivalent(wc, sc, AddedOnAttachments, r, why);
    }

    // A copy of an attachment's properties without its object property (0x3701 of type PtypObject).
    private sealed class Detached : MapiObject
    {
        public Detached(MapiAttachment a)
            : base(null)
        {
            foreach (MapiProperty p in a.Properties)
            {
                if (p.Tag.Type != 0x000D)
                {
                    Properties.Add(p);
                }
            }
        }
    }

    public static Result Compare(IEnumerable<MapiMessage> source, PstStore written, bool includeAssociated = true)
    {
        var r = new Result();
        var strict = new Dictionary<string, int>(StringComparer.Ordinal);
        var pending = new List<MapiMessage>();
        foreach (MapiMessage s in source)
        {
            r.Source++;
            pending.Add(s);
            string sig = Signature(s);
            strict[sig] = strict.GetValueOrDefault(sig) + 1;
        }

        var unmatched = new List<MapiMessage>();
        foreach (PstMessage w in written.EnumerateMessages(includeAssociated ? PstMessageSet.Normal | PstMessageSet.Associated : PstMessageSet.Normal))
        {
            r.Written++;
            string sig = Signature(w);
            if (strict.TryGetValue(sig, out int n) && n > 0)
            {
                strict[sig] = n - 1;
                r.Exact++;
            }
            else
            {
                unmatched.Add(w);
            }
        }

        // Source messages not matched exactly, then compared structurally: the written message may have the required properties added.
        var leftSource = new List<MapiMessage>();
        var sourceCounts = new Dictionary<string, int>(strict);
        foreach (MapiMessage s in pending)
        {
            string sig = Signature(s);
            if (sourceCounts.TryGetValue(sig, out int n) && n > 0)
            {
                sourceCounts[sig] = n - 1;
                leftSource.Add(s);
            }
        }

        foreach (MapiMessage w in unmatched)
        {
            bool found = false;
            var why = new StringBuilder();
            for (int i = 0; i < leftSource.Count && !found; i++)
            {
                if (leftSource[i].Recipients.Count == w.Recipients.Count && leftSource[i].Attachments.Count == w.Attachments.Count && MessageEquivalent(w, leftSource[i], r, null))
                {
                    leftSource.RemoveAt(i);
                    found = true;
                }
            }

            if (found)
            {
                r.WithAdditions++;
            }
            else
            {
                r.Unmatched++;
                if (r.Notes.Count < 20)
                {
                    MapiMessage? nearest = leftSource.FirstOrDefault(s => s.Subject == w.Subject) ?? leftSource.FirstOrDefault();
                    if (nearest is not null)
                    {
                        MessageEquivalent(w, nearest, new Result(), why);
                    }

                    r.Notes.Add($"unmatched '{w.Subject}': {why}");
                }
            }
        }

        return r;
    }
}
